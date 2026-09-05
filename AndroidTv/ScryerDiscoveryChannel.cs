using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.OAuth;
using Jellyfin.Plugin.Scryer.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Scryer.AndroidTv;

/// <summary>
/// Projects Scryer Discovery through Jellyfin's native channel contract so unmodified clients,
/// including Android TV, can browse it from My Media.
/// </summary>
public sealed class ScryerDiscoveryChannel : IChannel, IHasCacheKey
{
    /// <summary>
    /// The channel's Jellyfin-facing name. Jellyfin derives the internal channel id from it
    /// ("Channel " + name), so cleanup has to use the exact same value.
    /// </summary>
    internal const string ChannelName = "Scryer Discovery";
    internal const string TargetProviderId = "ScryerTarget";
    internal const string KindProviderId = "ScryerKind";
    internal const string DataSchemaVersion = "android-tv-v3";

    /// <summary>
    /// Channel item id prefix carried by every guidance stub ("Connect Scryer in Jellyfin Web" and
    /// friends). Jellyfin copies a channel item's id into BaseItem.ExternalId, so cleanup can find
    /// a stub that outlived the condition it described.
    /// </summary>
    internal const string MessageIdPrefix = "scryer-message-";

    private const int MaximumRecentItems = 25;
    private const int MaximumPageSize = 100;

    private readonly Func<PluginConfiguration?> _configuration;
    private readonly Func<string, bool> _hasStoredGrant;
    private readonly IScryerUserSessionService _sessions;
    private readonly IScryerGraphqlService _scryer;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public ScryerDiscoveryChannel(
        IScryerUserSessionService sessions,
        IScryerTokenStore tokens,
        IScryerGraphqlService scryer,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<ScryerDiscoveryChannel> logger)
        : this(
            sessions,
            scryer,
            libraryManager,
            userManager,
            TimeProvider.System,
            static () => Plugin.Instance?.Configuration,
            (tokens ?? throw new ArgumentNullException(nameof(tokens))).HasStoredGrant,
            logger)
    {
    }

    internal ScryerDiscoveryChannel(
        IScryerUserSessionService sessions,
        IScryerGraphqlService scryer,
        ILibraryManager libraryManager,
        IUserManager userManager,
        TimeProvider timeProvider,
        Func<PluginConfiguration?>? configuration = null,
        Func<string, bool>? hasStoredGrant = null,
        ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _configuration = configuration ?? (static () => Plugin.Instance?.Configuration);
        _hasStoredGrant = hasStoredGrant ?? (static _ => false);
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _scryer = scryer ?? throw new ArgumentNullException(nameof(scryer));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string Name => ChannelName;
    public string Description => "Personalized discovery and recommendations from Scryer.";
    public string DataVersion => DataSchemaVersion;
    public string HomePageUrl => string.Empty;
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
        MaxPageSize = MaximumPageSize,
        // Jellyfin discovers IChannel implementations by type scanning, so this class is always
        // constructed. When the administrator has not opted in it must therefore ask Jellyfin for
        // no automatic recursion at all, on top of returning nothing from GetChannelItems.
        AutoRefreshLevels = IsChannelEnabled() ? 2 : 0,
        SupportsContentDownloading = false,
        SupportsSortOrderToggle = false
    };

    public bool IsEnabledFor(string userId) => IsChannelEnabled();

    public string? GetCacheKey(string? userId)
    {
        // Daily, not hourly: every key change makes Jellyfin refetch the channel and upsert its
        // items into the server's own library database for each user.
        var day = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 86400;
        // Connection state has to be part of the key. Jellyfin retires a channel row only when it
        // actually re-queries the channel, so a "Connect Scryer" stub published while the user was
        // disconnected otherwise outlives the moment they connect. Jellyfin passes the same "N"
        // formatted user id the plugin stores grants under.
        var connected = !string.IsNullOrEmpty(userId) && _hasStoredGrant(userId) ? "1" : "0";
        return $"{DataSchemaVersion}:{day}:{connected}:{StableHash(userId)}";
    }

    private bool IsChannelEnabled()
    {
        var configuration = _configuration();
        return configuration is { EnableDiscovery: true, EnableAndroidTvChannel: true };
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // The daily "Refresh Channels" task calls this with Guid.Empty. There is no Scryer identity
        // for that caller, so returning stubs would persist rows nobody asked for.
        if (!IsChannelEnabled() || query.UserId == Guid.Empty || _userManager.GetUserById(query.UserId) is null)
        {
            return Empty();
        }

        var jellyfinUserId = query.UserId.ToString("N");
        var status = await _sessions.GetGrantStatusAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess || status.Value is null || !status.Value.Connected || !status.Value.AccountLinked)
        {
            // A user who has simply not linked yet is an ordinary state, not a fault. A grant
            // lookup that actually failed is a fault, and the card the user sees says nothing
            // about which of the two happened, so the log has to.
            if (status.IsSuccess)
            {
                _logger.LogDebug(
                    "Scryer discovery channel is showing the connect card to Jellyfin user {UserId}: no linked Scryer account.",
                    query.UserId);
            }
            else
            {
                _logger.LogWarning(
                    "Scryer discovery channel could not read the Scryer grant for Jellyfin user {UserId} ({Code}); showing the connect card.",
                    query.UserId,
                    status.Failure?.WireCode ?? "unknown");
            }

            return Page(query, new[]
            {
                MessageItem(jellyfinUserId, "connect", "Connect Scryer in Jellyfin Web", "Open Jellyfin Web, choose a Scryer page, and connect this Jellyfin user to a Scryer account first.")
            });
        }

        IReadOnlyList<ScryerRecommendationSeed> seeds;
        try
        {
            seeds = GetRecentSeeds(query.UserId);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            seeds = Array.Empty<ScryerRecommendationSeed>();
        }

        var projection = await _scryer.GetAndroidTvDiscoveryAsync(jellyfinUserId, seeds, cancellationToken).ConfigureAwait(false);
        if (!projection.IsSuccess || projection.Value is null)
        {
            // Without this the plugin publishes "Scryer Discovery unavailable" and records
            // nothing, leaving an administrator with a card on a television and an empty log.
            // WireCode is the stable, credential-free vocabulary, so it is safe to write out.
            _logger.LogWarning(
                "Scryer discovery is unavailable for Jellyfin user {UserId} ({Code}); showing the unavailable card.",
                query.UserId,
                projection.Failure?.WireCode ?? "unknown");

            return Page(query, new[]
            {
                MessageItem(jellyfinUserId, "unavailable", "Scryer Discovery unavailable", "Reconnect in Jellyfin Web or try again after Scryer is reachable.")
            });
        }

        // Scryer's discovery query takes no size arguments, so the response is whatever the server
        // decided to send. Everything published here becomes a persisted library row - and a
        // downloaded poster - on the Jellyfin server, for every user who opens the channel, so the
        // rails and their contents are capped before Jellyfin ever sees them.
        var configuration = _configuration();
        var railCap = ScryerAndroidTvLimits.RailCap(configuration);
        var itemCap = ScryerAndroidTvLimits.ItemsPerRailCap(configuration);
        var rails = projection.Value.Take(railCap).ToArray();

        if (string.IsNullOrEmpty(query.FolderId))
        {
            var folders = rails.Select(rail => RailItem(jellyfinUserId, rail, itemCap)).ToArray();
            return folders.Length > 0
                ? Page(query, folders)
                : Page(query, new[] { MessageItem(jellyfinUserId, "empty", "No recommendations yet", "Watch some media or try Scryer Discovery again later.") });
        }

        // Resolved against the capped list on purpose: a rail the channel never published must not
        // be browsable through a folder id left over from an earlier, larger response.
        var selectedRail = rails.FirstOrDefault(rail =>
            string.Equals(StableId(jellyfinUserId, "rail", rail.Key), query.FolderId, StringComparison.Ordinal));
        if (selectedRail is null)
        {
            return Page(query, Array.Empty<ChannelItemInfo>());
        }

        return Page(query, selectedRail.Items.Take(itemCap).Select(item => TitleItem(jellyfinUserId, item)).ToArray());
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type != ImageType.Primary)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Scryer.artwork.scryer-plugin.png");
        return Task.FromResult(stream is null
            ? new DynamicImageResponse { HasImage = false }
            : new DynamicImageResponse { HasImage = true, Format = ImageFormat.Png, Stream = stream });
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary };

    internal static string StableId(string jellyfinUserId, string kind, string value) =>
        $"scryer-{kind}-{StableHash(string.Join("\u001f", jellyfinUserId, value))}";

    private IReadOnlyList<ScryerRecommendationSeed> GetRecentSeeds(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Array.Empty<ScryerRecommendationSeed>();
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IsPlayed = true,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = MaximumRecentItems,
            EnableTotalRecordCount = false
        };

        var seeds = new List<ScryerRecommendationSeed>();
        var seen = new HashSet<Guid>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            BaseItem source;
            string facet;
            if (item is Movie)
            {
                source = item;
                facet = "MOVIE";
            }
            else if (item is Episode episode)
            {
                var series = episode.FindParent<Series>();
                if (series is null)
                {
                    continue;
                }

                source = series;
                facet = "SERIES";
            }
            else
            {
                continue;
            }

            if (!seen.Add(source.Id))
            {
                continue;
            }

            var providerIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in source.ProviderIds)
            {
                var sourceKey = pair.Key.ToLowerInvariant();
                if (sourceKey is not ("tmdb" or "tvdb" or "imdb") ||
                    string.IsNullOrWhiteSpace(pair.Value) ||
                    pair.Value.Length > 256 ||
                    providerIds.ContainsKey(sourceKey))
                {
                    continue;
                }

                providerIds[sourceKey] = pair.Value.Trim();
            }
            if (providerIds.Count == 0)
            {
                continue;
            }

            seeds.Add(new ScryerRecommendationSeed(source.Name, facet, providerIds));
            if (seeds.Count == 5)
            {
                break;
            }
        }

        return seeds;
    }

    private ChannelItemInfo RailItem(string jellyfinUserId, ScryerTvDiscoveryRail rail, int itemCap) => new()
    {
        Id = StableId(jellyfinUserId, "rail", rail.Key),
        Name = rail.Title,
        Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container,
        MediaType = ChannelMediaType.Video,
        DateModified = CurrentHourUtc(),
        // The published count, not the received one, so the etag tracks what Jellyfin stores.
        Etag = StableHash(rail.Key + "\u001f" + Math.Min(rail.Items.Count, itemCap))
    };

    private ChannelItemInfo TitleItem(string jellyfinUserId, ScryerTvDiscoveryItem item)
    {
        var result = new ChannelItemInfo
        {
            Id = StableId(jellyfinUserId, "title", item.TargetKey),
            Name = item.DisplayTitle,
            Overview = item.Overview,
            ProductionYear = item.Year,
            Type = ChannelItemType.Folder,
            // Container, not Series: a Series-typed channel folder becomes a real Series row in the
            // Jellyfin library database and gets an external metadata refresh queued against it.
            FolderType = ChannelFolderType.Container,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Movie,
            DateModified = CurrentHourUtc(),
            Etag = StableHash(item.TargetKey),
            ProviderIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TargetProviderId] = item.TargetKey,
                [KindProviderId] = item.TargetKind
            }
        };
        // Scryer posters are AVIF by design. Jellyfin downloads and caches the file (any image/*
        // content type is accepted) and, because Skia cannot transcode AVIF, serves the original
        // bytes to the client, whose Android TV 12+ decoder renders them.
        if (!string.IsNullOrWhiteSpace(item.PosterUrl))
        {
            result.ImageUrl = item.PosterUrl;
        }

        return result;
    }

    private ChannelItemInfo MessageItem(string jellyfinUserId, string key, string name, string overview) => new()
    {
        Id = StableId(jellyfinUserId, "message", key),
        Name = name,
        Overview = overview,
        Type = ChannelItemType.Folder,
        // Never Series: Jellyfin persists a Series-typed channel folder as a real Series row and
        // queues a metadata refresh, so TVDB/TMDB would be searched for this stub's title.
        FolderType = ChannelFolderType.Container,
        MediaType = ChannelMediaType.Video,
        DateModified = CurrentHourUtc(),
        Etag = StableHash(key)
    };

    private DateTime CurrentHourUtc()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static ChannelItemResult Empty() => new()
    {
        Items = Array.Empty<ChannelItemInfo>(),
        TotalRecordCount = 0
    };

    private static ChannelItemResult Page(InternalChannelItemQuery query, IReadOnlyList<ChannelItemInfo> all)
    {
        var start = Math.Clamp(query.StartIndex ?? 0, 0, all.Count);
        var limit = Math.Clamp(query.Limit ?? MaximumPageSize, 0, MaximumPageSize);
        return new ChannelItemResult
        {
            Items = all.Skip(start).Take(limit).ToArray(),
            TotalRecordCount = all.Count
        };
    }

    private static string StableHash(string? value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
}
