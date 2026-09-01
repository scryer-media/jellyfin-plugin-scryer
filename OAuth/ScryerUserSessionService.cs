using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Scryer.OAuth;

public interface IScryerUserSessionService
{
    Task<ScryerResult<ScryerAccessTokenLease>> GetAccessTokenAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<bool>> ConnectAsync(
        string jellyfinUserId,
        ScryerOAuthConfiguration expectedConfiguration,
        ScryerOAuthTokenSet tokenSet,
        CancellationToken cancellationToken);
    Task<ScryerResult<bool>> RetireIssuedRefreshTokenAsync(
        string jellyfinUserId,
        ScryerOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken);
    Task<ScryerResult<bool>> DisconnectAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<bool>> HasGrantAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<bool>> DiscardPendingLinkAsync(string jellyfinUserId, CancellationToken cancellationToken);
}

/// <summary>
/// Holds access tokens in process memory only. Refresh-token rotation and grant replacement are
/// serialized by canonical Jellyfin user, while cache entries retain full authority/client scope.
/// </summary>
public sealed class ScryerUserSessionService : IScryerUserSessionService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(45);
    private const int MaximumRevocationCleanupAttempts = 3;
    private readonly IScryerOAuthConfigurationProvider _configurationProvider;
    private readonly ScryerOAuthMetadataClient _oauthClient;
    private readonly IScryerTokenStore _tokenStore;
    private readonly IScryerJellyfinLinkService _linkService;
    private readonly ConcurrentDictionary<string, ScryerAccessTokenLease> _accessTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _invalidatedUsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new(StringComparer.Ordinal);

    public ScryerUserSessionService(
        IScryerOAuthConfigurationProvider configurationProvider,
        ScryerOAuthMetadataClient oauthClient,
        IScryerTokenStore tokenStore,
        IScryerJellyfinLinkService linkService)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _oauthClient = oauthClient ?? throw new ArgumentNullException(nameof(oauthClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _linkService = linkService ?? throw new ArgumentNullException(nameof(linkService));
    }

    public async Task<ScryerResult<ScryerAccessTokenLease>> GetAccessTokenAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        var resolved = ResolveKey(jellyfinUserId);
        if (!resolved.IsSuccess)
        {
            return ScryerResult<ScryerAccessTokenLease>.Fail(resolved.Failure!);
        }

        var (configuration, key) = resolved.Value!;
        var gate = GetUserGate(jellyfinUserId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentConfiguration = _configurationProvider.GetConfiguration();
            if (!currentConfiguration.IsSuccess)
            {
                return ScryerResult<ScryerAccessTokenLease>.Fail(currentConfiguration.Failure!);
            }

            if (!ConfigurationsMatch(configuration, currentConfiguration.Value!))
            {
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (IsInvalidated(jellyfinUserId))
            {
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (TryGetFreshLease(key, out var lease))
            {
                return ScryerResult<ScryerAccessTokenLease>.Success(lease);
            }

            var stored = await _tokenStore.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (stored.RequiresInvalidation)
            {
                await ClearUnusableGrantAsync(key, CancellationToken.None).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Legacy)
            {
                await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Missing)
            {
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.NotConnected);
            }

            if (stored.State == ScryerGrantReadState.Corrupt)
            {
                await ClearUnusableGrantAsync(key, cancellationToken).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Found && stored.Grant?.LinkState == ScryerGrantLinkState.PendingRevoke)
            {
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Found && stored.Grant?.LinkState == ScryerGrantLinkState.PendingLink)
            {
                await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State != ScryerGrantReadState.Found || stored.Grant is null || stored.Grant.LinkState != ScryerGrantLinkState.Active)
            {
                _accessTokens.TryRemove(key.CacheIdentity, out _);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.Internal);
            }

            var metadata = await _oauthClient.DiscoverAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (!metadata.IsSuccess)
            {
                return ScryerResult<ScryerAccessTokenLease>.Fail(metadata.Failure!);
            }

            // Once a refresh is sent it must reach a durable terminal state independent of this
            // particular HTTP caller; otherwise a cancelled caller could cause token-family reuse.
            var refreshed = await _oauthClient.RefreshAsync(metadata.Value!, configuration, stored.Grant.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                if (MayHaveSpentRefreshToken(refreshed.Failure!))
                {
                    await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                    return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
                }

                return ScryerResult<ScryerAccessTokenLease>.Fail(refreshed.Failure!);
            }

            var tokenSet = refreshed.Value!;
            if (!HasRequiredScopes(tokenSet.Scope))
            {
                await RetireRotatedGrantAsync(key, tokenSet.RefreshToken).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }
            bool persisted;
            try
            {
                persisted = await _tokenStore.SaveAsync(
                new ScryerRefreshGrant(key, tokenSet.RefreshToken, DateTimeOffset.UtcNow, ScryerGrantLinkState.Active),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await RetireRotatedGrantAsync(key, tokenSet.RefreshToken).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (!persisted)
            {
                // The previous token may already be spent. Never retry it after a failed rotation write.
                await RetireRotatedGrantAsync(key, tokenSet.RefreshToken).ConfigureAwait(false);
                return ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.AuthorizationExpired);
            }

            lease = new ScryerAccessTokenLease(tokenSet.AccessToken, tokenSet.AccessTokenExpiresAt);
            _accessTokens[key.CacheIdentity] = lease;
            return ScryerResult<ScryerAccessTokenLease>.Success(lease);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ScryerResult<bool>> ConnectAsync(
        string jellyfinUserId,
        ScryerOAuthConfiguration expectedConfiguration,
        ScryerOAuthTokenSet tokenSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedConfiguration);
        ArgumentNullException.ThrowIfNull(tokenSet);
        if (string.IsNullOrWhiteSpace(tokenSet.AccessToken) || string.IsNullOrWhiteSpace(tokenSet.RefreshToken) ||
            tokenSet.AccessTokenExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(tokenSet.Scope) ||
            !HasRequiredScopes(tokenSet.Scope))
        {
            return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse);
        }

        if (string.IsNullOrWhiteSpace(jellyfinUserId))
        {
            return ScryerResult<bool>.Fail(ScryerFailure.NotConnected);
        }

        var key = ScryerGrantKey.Create(jellyfinUserId, expectedConfiguration);
        var gate = GetUserGate(jellyfinUserId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentConfiguration = _configurationProvider.GetConfiguration();
            if (!currentConfiguration.IsSuccess || !ConfigurationsMatch(expectedConfiguration, currentConfiguration.Value!))
            {
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            var existing = await _tokenStore.ReadCurrentAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
            if (existing.State == ScryerGrantReadState.Legacy)
            {
                await RetireGrantAsync(existing.Grant, jellyfinUserId).ConfigureAwait(false);
                existing = ScryerGrantReadResult.Missing;
            }
            if (existing.State == ScryerGrantReadState.Unavailable)
            {
                if (existing.RequiresInvalidation)
                {
                    InvalidateUser(jellyfinUserId);
                }

                return ScryerResult<bool>.Fail(ScryerFailure.Internal);
            }

            if (existing.State == ScryerGrantReadState.Corrupt)
            {
                InvalidateUser(jellyfinUserId);
                await _tokenStore.DeleteCurrentAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
            }
            else if (existing.State == ScryerGrantReadState.Found && existing.Grant?.LinkState == ScryerGrantLinkState.PendingRevoke)
            {
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }
            else if (existing.State == ScryerGrantReadState.Found && existing.Grant?.LinkState == ScryerGrantLinkState.PendingLink)
            {
                await RetireGrantAsync(existing.Grant, jellyfinUserId).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }
            else if (existing.State == ScryerGrantReadState.Found && existing.Grant is not null)
            {
                var sameBinding = SameBinding(existing.Grant.Key, key);
                var sameToken = SameSecret(existing.Grant.RefreshToken, tokenSet.RefreshToken);
                if (!sameBinding && sameToken)
                {
                    return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse);
                }

                // Never overwrite the only handle to an older token family. Revoke it first,
                // including a reconnect under the same authority/client with a new grant.
                // The caller can revoke its newly issued token if this operation fails.
                if (!sameToken)
                {
                    if (!await RetireGrantAsync(existing.Grant, jellyfinUserId).ConfigureAwait(false))
                    {
                        return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
                    }
                }
            }

            var pending = new ScryerRefreshGrant(key, tokenSet.RefreshToken, DateTimeOffset.UtcNow, ScryerGrantLinkState.PendingLink);
            bool persisted;
            try
            {
                // The authorization code has already been exchanged. Finish persistence under
                // server ownership even if the callback request is abandoned by the browser.
                persisted = await _tokenStore.SaveAsync(
                    pending,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await RetireRotatedGrantAsync(key, tokenSet.RefreshToken).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (!persisted)
            {
                await RetireRotatedGrantAsync(key, tokenSet.RefreshToken).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            ClearUserCache(jellyfinUserId);
            _invalidatedUsers.TryRemove(jellyfinUserId, out _);
            var linked = await LinkPendingAsync(expectedConfiguration, pending, new ScryerAccessTokenLease(tokenSet.AccessToken, tokenSet.AccessTokenExpiresAt), CancellationToken.None).ConfigureAwait(false);
            return linked;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ScryerResult<bool>> RetireIssuedRefreshTokenAsync(
        string jellyfinUserId,
        ScryerOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(jellyfinUserId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
        }

        var key = ScryerGrantKey.Create(jellyfinUserId, configuration);
        var gate = GetUserGate(jellyfinUserId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RetireDetachedGrantAsync(
                new ScryerRefreshGrant(key, refreshToken, DateTimeOffset.UtcNow),
                jellyfinUserId).ConfigureAwait(false)
                ? ScryerResult<bool>.Success(true)
                : ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ScryerResult<bool>> DisconnectAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jellyfinUserId))
        {
            return ScryerResult<bool>.Fail(ScryerFailure.NotConnected);
        }

        var gate = GetUserGate(jellyfinUserId);
        // Disconnect must reach a terminal local state even after its browser request aborts.
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        ScryerFailure? unexpectedFailure = null;
        var localDeletionVerified = false;
        var retainedRevocableGrant = false;
        var currentReadProvesNoRevocableGrant = false;
        try
        {
            // After the disconnect operation owns the per-user gate, finish one bounded
            // revocation attempt independently of the caller's RequestAborted token.
            var detachedRetired = true;
            foreach (var detached in await _tokenStore.ReadDetachedQuarantinesAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false))
            {
                detachedRetired &= await RetireDetachedGrantAsync(detached, jellyfinUserId).ConfigureAwait(false);
            }
            if (!detachedRetired)
            {
                unexpectedFailure = ScryerFailure.AuthorizationExpired;
            }
            var stored = await _tokenStore.ReadCurrentAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
            currentReadProvesNoRevocableGrant = stored.State == ScryerGrantReadState.Missing;
            if (stored.State == ScryerGrantReadState.Found && stored.Grant is not null)
            {
                retainedRevocableGrant = true;
                localDeletionVerified = await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                if (!localDeletionVerified)
                {
                    unexpectedFailure = ScryerFailure.AuthorizationExpired;
                }
            }
            else if (stored.RequiresInvalidation ||
                stored.State is ScryerGrantReadState.Corrupt or ScryerGrantReadState.Unavailable)
            {
                // Local deletion still proceeds, but unreadable credential material prevents
                // a trustworthy remote revocation attempt and must not be reported as success.
                unexpectedFailure = ScryerFailure.Internal;
            }
        }
        catch (Exception)
        {
            unexpectedFailure = ScryerFailure.Internal;
        }
        finally
        {
            ClearUserCache(jellyfinUserId);
            if (!retainedRevocableGrant && currentReadProvesNoRevocableGrant)
            {
                try
                {
                    localDeletionVerified = await _tokenStore.DeleteCurrentAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    localDeletionVerified = false;
                }
            }

            if (localDeletionVerified)
            {
                _invalidatedUsers.TryRemove(jellyfinUserId, out _);
            }
            else
            {
                InvalidateUser(jellyfinUserId);
            }

            gate.Release();
        }

        return !localDeletionVerified
            ? ScryerResult<bool>.Fail(ScryerFailure.Internal)
            : unexpectedFailure is not null
                ? ScryerResult<bool>.Fail(unexpectedFailure)
                : ScryerResult<bool>.Success(true);
    }

    public async Task<ScryerResult<bool>> HasGrantAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        var resolved = ResolveKey(jellyfinUserId);
        if (!resolved.IsSuccess)
        {
            return ScryerResult<bool>.Fail(resolved.Failure!);
        }

        var (_, key) = resolved.Value!;
        var gate = GetUserGate(jellyfinUserId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentConfiguration = _configurationProvider.GetConfiguration();
            if (!currentConfiguration.IsSuccess)
            {
                return ScryerResult<bool>.Fail(currentConfiguration.Failure!);
            }

            if (!ConfigurationsMatch(resolved.Value!.Configuration, currentConfiguration.Value!))
            {
                return ScryerResult<bool>.Success(false);
            }

            if (IsInvalidated(jellyfinUserId))
            {
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            var stored = await _tokenStore.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (stored.RequiresInvalidation)
            {
                await ClearUnusableGrantAsync(key, CancellationToken.None).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Legacy)
            {
                await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Found && stored.Grant?.LinkState == ScryerGrantLinkState.Active)
            {
                return ScryerResult<bool>.Success(true);
            }

            if (stored.State == ScryerGrantReadState.Found && stored.Grant?.LinkState == ScryerGrantLinkState.PendingRevoke)
            {
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Found && stored.Grant?.LinkState == ScryerGrantLinkState.PendingLink)
            {
                await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Corrupt)
            {
                await ClearUnusableGrantAsync(key, cancellationToken).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }

            if (stored.State == ScryerGrantReadState.Unavailable)
            {
                ClearUserCache(jellyfinUserId);
                return ScryerResult<bool>.Fail(ScryerFailure.Internal);
            }

            return ScryerResult<bool>.Success(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ScryerResult<bool>> DiscardPendingLinkAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jellyfinUserId)) return ScryerResult<bool>.Fail(ScryerFailure.NotConnected);
        var gate = GetUserGate(jellyfinUserId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Deliberately bypass current configuration: a protected pending grant can belong to
            // an authority/client configuration that has since changed, but it still must be retired.
            var stored = await _tokenStore.ReadCurrentAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
            var detachedRetired = true;
            foreach (var detached in await _tokenStore.ReadDetachedQuarantinesAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false))
            {
                detachedRetired &= await RetireDetachedGrantAsync(detached, jellyfinUserId).ConfigureAwait(false);
            }
            if (stored.State == ScryerGrantReadState.Found && stored.Grant is not null &&
                stored.Grant.LinkState is ScryerGrantLinkState.PendingLink or ScryerGrantLinkState.PendingRevoke)
            {
                await RetireGrantAsync(stored.Grant, jellyfinUserId).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }
            if (!detachedRetired) return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            return stored.State == ScryerGrantReadState.Missing ? ScryerResult<bool>.Success(false) : ScryerResult<bool>.Success(true);
        }
        finally { gate.Release(); }
    }

    private ScryerResult<(ScryerOAuthConfiguration Configuration, ScryerGrantKey Key)> ResolveKey(string jellyfinUserId)
    {
        if (string.IsNullOrWhiteSpace(jellyfinUserId))
        {
            return ScryerResult<(ScryerOAuthConfiguration, ScryerGrantKey)>.Fail(ScryerFailure.NotConnected);
        }

        var configuration = _configurationProvider.GetConfiguration();
        if (!configuration.IsSuccess)
        {
            return ScryerResult<(ScryerOAuthConfiguration, ScryerGrantKey)>.Fail(configuration.Failure!);
        }

        var value = configuration.Value!;
        return ScryerResult<(ScryerOAuthConfiguration, ScryerGrantKey)>.Success((value, ScryerGrantKey.Create(jellyfinUserId, value)));
    }

    private bool TryGetFreshLease(ScryerGrantKey key, out ScryerAccessTokenLease lease)
    {
        return _accessTokens.TryGetValue(key.CacheIdentity, out lease!) && lease.ExpiresAt > DateTimeOffset.UtcNow.Add(ExpirySkew);
    }

    private async Task<bool> ClearUnusableGrantAsync(ScryerGrantKey key, CancellationToken cancellationToken)
    {
        InvalidateUser(key.JellyfinUserId);
        try
        {
            var deleted = await _tokenStore.DeleteCurrentAsync(key.JellyfinUserId, cancellationToken).ConfigureAwait(false);
            if (deleted)
            {
                _invalidatedUsers.TryRemove(key.JellyfinUserId, out _);
            }

            return deleted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<ScryerResult<bool>> LinkPendingAsync(ScryerOAuthConfiguration configuration, ScryerRefreshGrant pending, ScryerAccessTokenLease lease, CancellationToken cancellationToken)
    {
        var linked = await _linkService.LinkAsync(configuration, pending.Key.JellyfinUserId, lease, cancellationToken).ConfigureAwait(false);
        if (linked.IsSuccess)
        {
            try
            {
                if (!await _tokenStore.PromotePendingAsync(pending, CancellationToken.None).ConfigureAwait(false))
                {
                    var retired = await RetireGrantAsync(pending, pending.Key.JellyfinUserId).ConfigureAwait(false);
                    return retired
                        ? ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired)
                        : ScryerResult<bool>.Fail(ScryerFailure.Internal);
                }
            }
            catch (Exception)
            {
                var retired = await RetireGrantAsync(pending, pending.Key.JellyfinUserId).ConfigureAwait(false);
                return retired
                    ? ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired)
                    : ScryerResult<bool>.Fail(ScryerFailure.Internal);
            }
            _accessTokens[pending.Key.CacheIdentity] = lease;
            return ScryerResult<bool>.Success(true);
        }
        // Finalize is the sole authorized link attempt. Any unsuccessful or ambiguous result
        // retires the local family before returning, while a remotely completed durable link is
        // deliberately forward-only and is never undone here.
        var removed = await RetireGrantAsync(pending, pending.Key.JellyfinUserId).ConfigureAwait(false);
        return removed ? linked : ScryerResult<bool>.Fail(ScryerFailure.Internal);
    }

    private async Task<bool> RetireGrantAsync(ScryerRefreshGrant? grant, string jellyfinUserId)
    {
        ClearUserCache(jellyfinUserId);
        if (grant is null)
        {
            return await DeleteGrantAsync(jellyfinUserId).ConfigureAwait(false);
        }

        // Commit the non-serving state before touching the remote family. The token store writes
        // through a recoverable journal, so a crash at any point after this succeeds cannot revive
        // an Active handle or lose a newly rotated refresh token before revocation is retried.
        InvalidateUser(jellyfinUserId);
        var attempts = Math.Min(MaximumRevocationCleanupAttempts, grant.LinkAttempts + 1);
        try
        {
            var quarantined = await _tokenStore.QuarantineAsync(
                new ScryerRefreshGrant(
                    grant.Key,
                    grant.RefreshToken,
                    DateTimeOffset.UtcNow,
                    ScryerGrantLinkState.PendingRevoke,
                    linkAttempts: attempts),
                CancellationToken.None).ConfigureAwait(false);
            if (!quarantined)
            {
                // Do not revoke or delete a handle unless its unusable state has reached durable
                // storage. The original record remains recoverable and no remote family changed.
                return false;
            }
        }
        catch (Exception)
        {
            // As above, retain the only encrypted record if durable quarantine is unavailable.
            return false;
        }

        ScryerResult<bool> revoked;
        try
        {
            revoked = await _oauthClient.RevokeStoredGrantAsync(
                grant.Key,
                grant.RefreshToken,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            revoked = ScryerResult<bool>.Fail(ScryerFailure.Offline);
        }

        if (revoked.IsSuccess)
        {
            return await DeleteGrantAsync(jellyfinUserId).ConfigureAwait(false);
        }

        // The durable tombstone remains for a bounded retry on a later startup.
        return false;
    }

    private async Task<bool> RetireDetachedGrantAsync(ScryerRefreshGrant grant, string jellyfinUserId)
    {
        var tombstone = new ScryerRefreshGrant(
            grant.Key,
            grant.RefreshToken,
            DateTimeOffset.UtcNow,
            ScryerGrantLinkState.PendingRevoke,
            linkAttempts: Math.Min(MaximumRevocationCleanupAttempts, grant.LinkAttempts + 1));
        try
        {
            if (!await _tokenStore.QuarantineDetachedAsync(tombstone, CancellationToken.None).ConfigureAwait(false))
            {
                return false;
            }
            var revoked = await _oauthClient.RevokeStoredGrantAsync(
                tombstone.Key,
                tombstone.RefreshToken,
                CancellationToken.None).ConfigureAwait(false);
            if (!revoked.IsSuccess) return false;
            return await _tokenStore.DeleteDetachedQuarantineAsync(tombstone, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> DeleteGrantAsync(string jellyfinUserId)
    {
        try
        {
            if (await _tokenStore.DeleteCurrentAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false))
            {
                _invalidatedUsers.TryRemove(jellyfinUserId, out _);
                return true;
            }
        }
        catch (Exception) { }

        InvalidateUser(jellyfinUserId);
        return false;
    }

    private Task RetireRotatedGrantAsync(ScryerGrantKey key, string refreshToken) =>
        RetireGrantAsync(
            new ScryerRefreshGrant(key, refreshToken, DateTimeOffset.UtcNow, ScryerGrantLinkState.Active),
            key.JellyfinUserId);

    private SemaphoreSlim GetUserGate(string jellyfinUserId) =>
        _userLocks.GetOrAdd(jellyfinUserId, static _ => new SemaphoreSlim(1, 1));

    private void ClearUserCache(string jellyfinUserId)
    {
        var prefix = jellyfinUserId + "\u001f";
        foreach (var cacheKey in _accessTokens.Keys)
        {
            if (cacheKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                _accessTokens.TryRemove(cacheKey, out _);
            }
        }
    }

    private void InvalidateUser(string jellyfinUserId)
    {
        ClearUserCache(jellyfinUserId);
        _invalidatedUsers.TryAdd(jellyfinUserId, 0);
    }

    private bool IsInvalidated(string jellyfinUserId) => _invalidatedUsers.ContainsKey(jellyfinUserId);

    private static bool MayHaveSpentRefreshToken(ScryerFailure failure) =>
        failure.Code is ScryerFailureCode.AuthorizationExpired or ScryerFailureCode.ScryerOffline or ScryerFailureCode.InvalidResponse or ScryerFailureCode.InternalError;

    private static bool HasRequiredScopes(string scope)
    {
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return scopes.Length == 2 && scopes.Contains("library", StringComparer.Ordinal) &&
            scopes.Contains("jellyfin-link", StringComparer.Ordinal);
    }


    private static bool SameBinding(ScryerGrantKey left, ScryerGrantKey right) =>
        string.Equals(left.JellyfinUserId, right.JellyfinUserId, StringComparison.Ordinal) &&
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        string.Equals(left.ClientId, right.ClientId, StringComparison.Ordinal);

    private static bool SameSecret(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool ConfigurationsMatch(ScryerOAuthConfiguration expected, ScryerOAuthConfiguration current) =>
        string.Equals(expected.InternalAuthority.AbsoluteUri, current.InternalAuthority.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(expected.PublicAuthority.AbsoluteUri, current.PublicAuthority.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(expected.RedirectUri.AbsoluteUri, current.RedirectUri.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(expected.ClientId, current.ClientId, StringComparison.Ordinal);
}
