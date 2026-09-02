using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.OAuth;
using Jellyfin.Plugin.Scryer.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Scryer.AndroidTv;

/// <summary>Turns Favorite transitions on Scryer channel title stubs into user-bound Scryer actions.</summary>
internal sealed class ScryerTvFavoriteService : BackgroundService
{
    private const int MaximumMessageLength = 160;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IScryerGraphqlService _scryer;
    private readonly IScryerTvActionJournal _journal;
    private readonly ILogger<ScryerTvFavoriteService> _logger;
    private readonly Channel<FavoriteWorkItem> _queue = Channel.CreateBounded<FavoriteWorkItem>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<string, byte> _suppressedRollbacks = new(StringComparer.Ordinal);

    public ScryerTvFavoriteService(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IScryerGraphqlService scryer,
        IScryerTvActionJournal journal,
        ILogger<ScryerTvFavoriteService> logger)
    {
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _scryer = scryer ?? throw new ArgumentNullException(nameof(scryer));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingAsync(stoppingToken).ConfigureAwait(false);
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(work, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Scryer Android TV favorite processing failed.");
            }
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        if (!TryGetTarget(eventArgs.Item, out var targetKey, out var targetKind))
        {
            return;
        }

        var suppressionKey = SuppressionKey(eventArgs.UserId, eventArgs.Item.Id);
        if (_suppressedRollbacks.TryRemove(suppressionKey, out _))
        {
            return;
        }

        var work = new FavoriteWorkItem(
            eventArgs.UserId,
            eventArgs.Item.Id,
            targetKey,
            targetKind,
            eventArgs.UserData.IsFavorite,
            IsRecovery: false);
        if (!_queue.Writer.TryWrite(work))
        {
            _ = EnqueueWhenAvailableAsync(work);
        }
    }

    private async Task EnqueueWhenAvailableAsync(FavoriteWorkItem work)
    {
        try
        {
            await _queue.Writer.WriteAsync(work).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = _libraryManager.GetItemById(entry.JellyfinItemId);
            if (item is null || !TryGetTarget(item, out var targetKey, out var targetKind) ||
                !string.Equals(targetKey, entry.TargetKey, StringComparison.Ordinal) ||
                !string.Equals(targetKind, entry.TargetKind, StringComparison.Ordinal))
            {
                await _journal.AbandonAsync(entry.JellyfinUserId, entry.TargetKey, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var userId = Guid.ParseExact(entry.JellyfinUserId, "N");
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                await _journal.AbandonAsync(entry.JellyfinUserId, entry.TargetKey, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null || !userData.IsFavorite)
            {
                await _journal.RearmAsync(entry.JellyfinUserId, entry.TargetKey, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ProcessAsync(new FavoriteWorkItem(userId, item.Id, targetKey, targetKind, IsFavorite: true, IsRecovery: true), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(FavoriteWorkItem work, CancellationToken cancellationToken)
    {
        var jellyfinUserId = work.UserId.ToString("N");
        if (!work.IsFavorite)
        {
            await _journal.RearmAsync(jellyfinUserId, work.TargetKey, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!work.IsRecovery)
        {
            var begin = await _journal.BeginAsync(new ScryerTvJournalEntry(
                jellyfinUserId,
                work.TargetKey,
                work.TargetKind,
                work.ItemId,
                ScryerTvJournalState.Pending,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            if (begin is ScryerTvJournalBeginResult.Pending or ScryerTvJournalBeginResult.Completed)
            {
                return;
            }

            if (begin == ScryerTvJournalBeginResult.Unavailable)
            {
                await RollbackFavoriteAsync(work.UserId, work.ItemId, cancellationToken).ConfigureAwait(false);
                await SendMessageAsync(work.UserId, "Scryer could not save this action. Try again.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var result = await _scryer.ResolveDefaultTvActionAndExecuteAsync(
            jellyfinUserId,
            work.TargetKey,
            work.TargetKind,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value is not null)
        {
            if (!await _journal.CompleteAsync(jellyfinUserId, work.TargetKey, cancellationToken).ConfigureAwait(false))
            {
                await RollbackFavoriteAsync(work.UserId, work.ItemId, cancellationToken).ConfigureAwait(false);
                await SendMessageAsync(work.UserId, "Scryer completed the action, but could not save its status. Try again later.", cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendMessageAsync(work.UserId, SuccessMessage(result.Value), cancellationToken).ConfigureAwait(false);
            return;
        }

        await _journal.AbandonAsync(jellyfinUserId, work.TargetKey, cancellationToken).ConfigureAwait(false);
        await RollbackFavoriteAsync(work.UserId, work.ItemId, cancellationToken).ConfigureAwait(false);
        await SendMessageAsync(work.UserId, FailureMessage(result.Failure), cancellationToken).ConfigureAwait(false);
    }

    private Task RollbackFavoriteAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Task.CompletedTask;
        }

        var userData = _userDataManager.GetUserData(user, item);
        if (userData is null || !userData.IsFavorite)
        {
            return Task.CompletedTask;
        }

        userData.IsFavorite = false;
        var key = SuppressionKey(userId, itemId);
        _suppressedRollbacks[key] = 0;
        try
        {
            _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserRating, cancellationToken);
        }
        finally
        {
            _suppressedRollbacks.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private async Task SendMessageAsync(Guid userId, string text, CancellationToken cancellationToken)
    {
        var bounded = text.Length <= MaximumMessageLength ? text : text[..MaximumMessageLength];
        var command = new GeneralCommand(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Header"] = "Scryer",
            ["Text"] = bounded,
            ["TimeoutMs"] = "5000"
        })
        {
            Name = GeneralCommandType.DisplayMessage,
            ControllingUserId = userId
        };
        await _sessionManager.SendMessageToUserSessions(
            new List<Guid> { userId },
            SessionMessageType.GeneralCommand,
            command,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetTarget(BaseItem item, out string targetKey, out string targetKind)
    {
        targetKey = string.Empty;
        targetKind = string.Empty;
        if (item.ChannelId == Guid.Empty ||
            !item.ProviderIds.TryGetValue(ScryerDiscoveryChannel.TargetProviderId, out var rawTargetKey) ||
            !item.ProviderIds.TryGetValue(ScryerDiscoveryChannel.KindProviderId, out var rawTargetKind))
        {
            return false;
        }

        targetKey = rawTargetKey?.Trim() ?? string.Empty;
        targetKind = rawTargetKind?.Trim().ToUpperInvariant() ?? string.Empty;
        return targetKey.Length is > 0 and <= 256 && targetKind is "MOVIE" or "SERIES" or "ANIME";
    }

    private static string SuccessMessage(ScryerTvActionResult result) => result.Kind switch
    {
        ScryerTvActionKind.Added => $"Added to {result.LibraryName}.",
        ScryerTvActionKind.AlreadyPresent => $"Already in {result.LibraryName}.",
        _ => "Request submitted."
    };

    private static string FailureMessage(ScryerFailure? failure)
    {
        if (failure is null)
        {
            return "Scryer could not complete this action. Try again.";
        }

        if (failure.Message is
            "This discovery item is no longer valid." or
            "Configure exactly one default Scryer library for this media type." or
            "Configure a default quality profile on the default Scryer library." or
            "This title has no supported external identifier." or
            "Your Scryer account cannot add or request this kind of title.")
        {
            return failure.Message;
        }

        return failure.Code switch
        {
            ScryerFailureCode.NotConnected or ScryerFailureCode.AuthorizationExpired => "Reconnect Scryer in Jellyfin Web and try again.",
            ScryerFailureCode.PermissionDenied => "Your Scryer account cannot add or request this title.",
            ScryerFailureCode.RateLimited => "Scryer is busy. Try again shortly.",
            ScryerFailureCode.ScryerOffline => "Scryer is unreachable. Try again later.",
            _ => "Scryer could not complete this action. Try again."
        };
    }

    private static string SuppressionKey(Guid userId, Guid itemId) => userId.ToString("N") + "\u001f" + itemId.ToString("N");

    private sealed record FavoriteWorkItem(
        Guid UserId,
        Guid ItemId,
        string TargetKey,
        string TargetKind,
        bool IsFavorite,
        bool IsRecovery);
}
