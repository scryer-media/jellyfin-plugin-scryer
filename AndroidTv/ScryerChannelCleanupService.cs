using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Scryer.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Scryer.AndroidTv;

/// <summary>
/// Removes the rows Jellyfin persisted into its own library database on behalf of the Scryer
/// discovery channel. Plugin 0.1.14.0 shipped the channel enabled, so Jellyfin stored one Series
/// row per rail and per message stub for every Jellyfin user, downloaded their images, and queued
/// external metadata refreshes against them. Turning the channel off does not retract those rows,
/// so the plugin retracts them itself.
/// </summary>
public sealed class ScryerChannelCleanupService : IHostedService
{
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ScryerChannelCleanupService> _logger;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stopping;
    private Task? _startupSweep;
    private EventHandler<BasePluginConfiguration>? _configurationChanged;

    public ScryerChannelCleanupService(ILibraryManager libraryManager, ILogger<ScryerChannelCleanupService> logger)
        : this(libraryManager, logger, static () => Plugin.Instance?.Configuration)
    {
    }

    internal ScryerChannelCleanupService(
        ILibraryManager libraryManager,
        ILogger<ScryerChannelCleanupService> logger,
        Func<PluginConfiguration?> configuration)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Jellyfin awaits both database migration stages and ApplicationHost.InitializeServices,
        // which is what assigns BaseItem.LibraryManager/ItemRepository, before it starts the
        // generic host (Jellyfin.Server/Program.cs, ApplicationHost.InitializeServices), so the
        // library repository is already usable by the time a plugin hosted service starts. The
        // sweep still runs off StartAsync rather than in it: a large or slow library must never
        // delay server startup, and no exception may escape a hosted service's StartAsync. The
        // bounded retry covers a database that is momentarily busy behind the migration steps.
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startupSweep = Task.Run(() => SweepWithRetryAsync(_stopping.Token), CancellationToken.None);

        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            // Saving the configuration page is the moment an administrator turns the channel off,
            // and the moment the rows they want gone become removable.
            _configurationChanged = (_, _) => OnConfigurationSaved();
            plugin.ConfigurationChanged += _configurationChanged;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null && _configurationChanged is not null)
        {
            plugin.ConfigurationChanged -= _configurationChanged;
        }

        _configurationChanged = null;
        _stopping?.Cancel();
        var sweep = _startupSweep;
        if (sweep is not null)
        {
            try
            {
                await sweep.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _startupSweep = null;
        _stopping?.Dispose();
        _stopping = null;
    }

    /// <summary>Re-runs the sweep after the administrator saves the plugin configuration.</summary>
    internal void OnConfigurationSaved()
    {
        var stopping = _stopping;
        if (stopping is null || stopping.IsCancellationRequested)
        {
            return;
        }

        // Never block the administrator's save request on a library sweep.
        _ = Task.Run(() => SweepWithRetryAsync(stopping.Token), CancellationToken.None);
    }

    private async Task SweepWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                await SweepAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                if (attempt == MaximumAttempts)
                {
                    _logger.LogWarning(
                        error,
                        "Gave up removing Scryer discovery channel rows from the Jellyfin library after {Attempts} attempts.",
                        MaximumAttempts);
                    return;
                }

                _logger.LogDebug(error, "Scryer discovery channel cleanup attempt {Attempt} failed; retrying.", attempt);
                try
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Runs one sweep, serialized against any other sweep of this service.</summary>
    internal async Task<ScryerChannelSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Sweep(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ScryerChannelSweepResult Sweep(CancellationToken cancellationToken)
    {
        // ChannelManager.GetInternalChannelId derives every channel's identity this way, and every
        // row it persists for the channel carries that value in ChannelId.
        var channelId = _libraryManager.GetNewItemId(
            "Channel " + ScryerDiscoveryChannel.ChannelName,
            typeof(Channel));
        if (channelId.Equals(Guid.Empty))
        {
            // Never query on an empty channel id: it is not an identity the plugin owns.
            _logger.LogDebug("Skipping Scryer discovery channel cleanup: Jellyfin returned no channel id.");
            return new ScryerChannelSweepResult(channelId, 0, 0, 0);
        }

        // With the channel enabled only the rows the 0.1.14.0 channel could write are legacy: it
        // emitted Series-typed folders, while the current channel emits containers only. Removing
        // just those keeps any favourite an administrator has already set on a valid v2 row.
        var enabled = _configuration()?.EnableAndroidTvChannel == true;
        var candidates = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                ChannelIds = new[] { channelId },
                Recursive = true,
                EnableTotalRecordCount = false
            })
            .Where(item => IsRemovable(item, channelId, enabled))
            .ToArray();

        if (candidates.Length == 0)
        {
            _logger.LogDebug("No Scryer discovery channel rows to remove from the Jellyfin library ({ChannelId}).", channelId);
            return new ScryerChannelSweepResult(channelId, 0, 0, 0);
        }

        var deleted = 0;
        var failed = 0;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // DeleteFileLocation is false because the channel owns no files on disk (Jellyfin
                // forces it false for channel items anyway) and DeleteFromExternalProvider is false
                // because the plugin must never ask Scryer to delete anything. This still removes
                // the row, its internal metadata directory, and the images downloaded into it.
                _libraryManager.DeleteItem(
                    item,
                    new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false },
                    notifyParentItem: false);
                deleted++;
            }
            catch (Exception error)
            {
                failed++;
                _logger.LogWarning(
                    error,
                    "Could not remove Scryer discovery channel row {ItemId} from the Jellyfin library.",
                    item.Id);
            }
        }

        _logger.LogInformation(
            "Removed Scryer discovery channel rows from the Jellyfin library ({ChannelId}, scope {Scope}): found {Found}, deleted {Deleted}, failed {Failed}.",
            channelId,
            enabled ? "legacy Series rows" : "all channel rows",
            candidates.Length,
            deleted,
            failed);

        return new ScryerChannelSweepResult(channelId, candidates.Length, deleted, failed);
    }

    private static bool IsRemovable(BaseItem? item, Guid channelId, bool channelEnabled)
    {
        // The Channel entity itself carries the same ChannelId as its children. Jellyfin owns it
        // and recreates it on every Refresh Channels run, so it is never the plugin's to delete.
        if (item is null || item is Channel || item.Id.Equals(channelId))
        {
            return false;
        }

        return !channelEnabled || IsLegacySeriesRow(item);
    }

    private static bool IsLegacySeriesRow(BaseItem item)
    {
        try
        {
            return item.GetBaseItemKind() == BaseItemKind.Series;
        }
        catch (ArgumentException)
        {
            // GetBaseItemKind parses the client type name; an unmappable row is not a legacy row.
            return false;
        }
    }
}

/// <summary>Counts from a single cleanup sweep.</summary>
/// <param name="ChannelId">Internal Jellyfin id of the Scryer discovery channel.</param>
/// <param name="Found">Rows selected for removal.</param>
/// <param name="Deleted">Rows Jellyfin removed.</param>
/// <param name="Failed">Rows whose removal threw.</param>
internal readonly record struct ScryerChannelSweepResult(Guid ChannelId, int Found, int Deleted, int Failed);
