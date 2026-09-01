using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>Best-effort bounded cleanup for an interrupted link request; it never replays a link.</summary>
public sealed class ScryerPendingLinkCleanupService : IHostedService
{
    private const int MaximumStartupRecoveries = 32;
    private const int MaximumStartupRecoveryBatches = 4;
    private readonly IScryerTokenStore _tokenStore;
    private readonly IScryerUserSessionService _sessions;
    private CancellationTokenSource? _stopping;
    private Task? _recoveryTask;

    public ScryerPendingLinkCleanupService(IScryerTokenStore tokenStore, IScryerUserSessionService sessions)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _recoveryTask = RecoverAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping?.Cancel();
        var recovery = _recoveryTask;
        if (recovery is not null)
        {
            await recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        _recoveryTask = null;
        _stopping?.Dispose();
        _stopping = null;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? afterUserId = null;
            for (var batch = 0; batch < MaximumStartupRecoveryBatches; batch++)
            {
                var users = await _tokenStore
                    .GetPendingUserIdsAsync(MaximumStartupRecoveries, afterUserId, cancellationToken)
                    .ConfigureAwait(false);
                if (users.Count == 0)
                {
                    break;
                }

                foreach (var userId in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _sessions.DiscardPendingLinkAsync(userId, cancellationToken).ConfigureAwait(false);
                }

                afterUserId = users[users.Count - 1];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { }
    }
}
