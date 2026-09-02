using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Scryer.OAuth;

public interface IScryerOAuthFlowService
{
    Task<ScryerResult<ScryerOAuthStartResult>> StartAsync(string jellyfinUserId, string? returnPage, CancellationToken cancellationToken);
    Task<ScryerOAuthCallbackStageResult> StageCallbackAsync(string? state, string? protectedCookie, string? authorizationCode, string? authorizationError, CancellationToken cancellationToken);
    Task<ScryerResult<bool>> FinalizeAsync(string jellyfinUserId, string? protectedCookie, CancellationToken cancellationToken);
    Task<ScryerAuthStatusDto> GetStatusAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<bool>> DisconnectAsync(string jellyfinUserId, CancellationToken cancellationToken);
    bool TryGetCallbackCookie(string? state, out ScryerOAuthCallbackCookie cookie);
}

/// <summary>
/// Owns the browser-bound Authorization Code + PKCE flow. The only browser-held flow state is
/// an opaque data-protected, host-only HttpOnly cookie scoped to the Jellyfin origin.
/// </summary>
public sealed class ScryerOAuthFlowService : IScryerOAuthFlowService
{
    private const string CookiePrefix = "scryer_oauth_flow_";
    private const string FinalizeCookiePrefix = "scryer_oauth_finalize_";
    private const int MaximumUserIdLength = 128;
    private const int MaximumStateLength = 128;
    private const int MaximumCodeLength = 2048;
    private const int MaximumErrorLength = 128;
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> ReturnPages = new(StringComparer.Ordinal)
    {
        "#/scryer-discovery",
        "#/scryer-calendar",
        "#/scryer-requests",
        "#/scryer-download"
    };

    private readonly IScryerOAuthConfigurationProvider _configurationProvider;
    private readonly ScryerOAuthMetadataClient _metadataClient;
    private readonly IScryerUserSessionService _sessionService;
    private readonly ScryerOAuthFlowStore _flowStore;
    private readonly IDataProtector _cookieProtector;

    public ScryerOAuthFlowService(
        IScryerOAuthConfigurationProvider configurationProvider,
        ScryerOAuthMetadataClient metadataClient,
        IScryerUserSessionService sessionService,
        ScryerOAuthFlowStore flowStore,
        IDataProtectionProvider dataProtectionProvider)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _metadataClient = metadataClient ?? throw new ArgumentNullException(nameof(metadataClient));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _flowStore = flowStore ?? throw new ArgumentNullException(nameof(flowStore));
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _cookieProtector = dataProtectionProvider.CreateProtector("Jellyfin.Plugin.Scryer", "OAuthFlowCookie", "v1");
    }

    public async Task<ScryerResult<ScryerOAuthStartResult>> StartAsync(
        string jellyfinUserId,
        string? returnPage,
        CancellationToken cancellationToken)
    {
        if (!IsSafeUserId(jellyfinUserId) || !TryNormalizeReturnPage(returnPage, out var normalizedReturnPage))
        {
            return ScryerResult<ScryerOAuthStartResult>.Fail(ScryerFailure.InvalidResponse);
        }

        var configured = _configurationProvider.GetConfiguration();
        if (!configured.IsSuccess)
        {
            return ScryerResult<ScryerOAuthStartResult>.Fail(configured.Failure!);
        }

        var configuration = configured.Value!;
        var pkce = ScryerPkce.Create();
        var state = CreateRandomValue(32);
        var flowId = CreateRandomValue(16);
        var browserBinding = CreateRandomValue(32);
        var now = _flowStore.GetUtcNow();
        var transaction = new ScryerOAuthFlowTransaction(
            flowId,
            state,
            browserBinding,
            jellyfinUserId,
            pkce.Verifier,
            configuration.RedirectUri.AbsoluteUri,
            CreateConfigurationFingerprint(configuration),
            normalizedReturnPage,
            now,
            now.Add(TransactionLifetime));

        if (!_flowStore.TryCreate(transaction, out var rateLimited))
        {
            return ScryerResult<ScryerOAuthStartResult>.Fail(rateLimited
                ? new ScryerFailure(ScryerFailureCode.RateLimited, "Too many connection attempts. Try again shortly.")
                : ScryerFailure.Internal);
        }

        var started = false;
        try
        {
            var metadata = await _metadataClient.DiscoverAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (!metadata.IsSuccess)
            {
                return ScryerResult<ScryerOAuthStartResult>.Fail(metadata.Failure!);
            }

            var cookie = _cookieProtector.Protect(flowId + ":" + browserBinding);
            var authorizationUri = BuildAuthorizationUri(metadata.Value!.AuthorizationEndpoint, configuration, state, pkce.Challenge);
            var result = ScryerResult<ScryerOAuthStartResult>.Success(new ScryerOAuthStartResult(
                authorizationUri,
                CookiePrefix + flowId,
                cookie,
                "/",
                configuration.RedirectUri.Scheme == Uri.UriSchemeHttps,
                transaction.ExpiresAt));
            started = true;
            return result;
        }
        finally
        {
            // A start reservation protects discovery from amplification, but it must not leave
            // a usable transaction behind when metadata discovery or cookie protection fails.
            if (!started)
            {
                _flowStore.Remove(transaction.State);
            }
        }
    }

    public Task<ScryerOAuthCallbackStageResult> StageCallbackAsync(
        string? state,
        string? protectedCookie,
        string? authorizationCode,
        string? authorizationError,
        CancellationToken cancellationToken) => Task.FromResult(StageCallback(state, protectedCookie, authorizationCode, authorizationError));

    public async Task<ScryerAuthStatusDto> GetStatusAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        if (!IsSafeUserId(jellyfinUserId))
        {
            return ScryerAuthStatusDto.Failed(ScryerFailure.NotConnected);
        }

        var configured = _configurationProvider.GetConfiguration();
        if (!configured.IsSuccess)
        {
            return ScryerAuthStatusDto.Failed(configured.Failure!);
        }

        var connection = await _sessionService.GetGrantStatusAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return connection.IsSuccess
            ? new ScryerAuthStatusDto(true, connection.Value!.Connected, connection.Value.AccountLinked, null)
            : new ScryerAuthStatusDto(true, false, false, ScryerAuthFailureDto.From(connection.Failure!));
    }

    public async Task<ScryerResult<bool>> DisconnectAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        if (!IsSafeUserId(jellyfinUserId)) return ScryerResult<bool>.Fail(ScryerFailure.NotConnected);
        _flowStore.InvalidateUser(jellyfinUserId);
        return await _sessionService.DisconnectAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetCallbackCookie(string? state, out ScryerOAuthCallbackCookie cookie)
    {
        cookie = null!;
        if (!IsWithinBound(state, MaximumStateLength) || string.IsNullOrWhiteSpace(state) ||
            !_flowStore.TryGet(state, out var transaction))
        {
            return false;
        }

        if (!Uri.TryCreate(transaction.RedirectUri, UriKind.Absolute, out _))
        {
            return false;
        }

        cookie = new ScryerOAuthCallbackCookie(CookiePrefix + transaction.FlowId, "/");
        return true;
    }

    private static bool TryBuildCallbackRedirect(Uri callback, string returnPage, out Uri redirect)
    {
        redirect = null!;
        if (!TryNormalizeReturnPage(returnPage, out var normalizedReturnPage)) return false;
        var callbackPath = callback.AbsolutePath;
        const string callbackSuffix = "/Scryer/Auth/Callback";
        if (!callbackPath.EndsWith(callbackSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var basePath = callbackPath[..^callbackSuffix.Length].TrimEnd('/');
        var builder = new UriBuilder(callback)
        {
            Path = (basePath.Length == 0 ? string.Empty : basePath) + "/web/index.html",
            Query = string.Empty,
            Fragment = normalizedReturnPage[1..]
        };
        redirect = builder.Uri;
        return true;
    }

    private ScryerOAuthCallbackStageResult StageCallback(string? state, string? protectedCookie, string? code, string? error)
    {
        if (!IsWithinBound(state, MaximumStateLength) || string.IsNullOrWhiteSpace(state) ||
            string.IsNullOrWhiteSpace(protectedCookie) || protectedCookie.Length > 4096 ||
            !TryUnprotectCookie(protectedCookie, out var flowId, out var browserBinding)) return ScryerOAuthCallbackStageResult.Failed(ScryerFailure.AuthorizationExpired);
        if (!IsWithinBound(code, MaximumCodeLength) || !IsWithinBound(error, MaximumErrorLength) ||
            !HasExactlyOneCallbackOutcome(code, error))
        {
            _flowStore.RejectCallback(state, flowId, browserBinding);
            return ScryerOAuthCallbackStageResult.Failed(ScryerFailure.AuthorizationExpired);
        }
        var binding = CreateRandomValue(32);
        if (!_flowStore.TryStageCallback(state, flowId, browserBinding, code, error, binding, out var transaction)) return ScryerOAuthCallbackStageResult.Failed(ScryerFailure.AuthorizationExpired);
        var configured = _configurationProvider.GetConfiguration();
        if (!configured.IsSuccess || !ConfigurationMatches(transaction, configured.Value!))
        {
            _flowStore.Remove(transaction.State);
            return ScryerOAuthCallbackStageResult.Failed(ScryerFailure.AuthorizationExpired);
        }
        var configuration = configured.Value!;
        if (!TryBuildCallbackRedirect(configuration.RedirectUri, transaction.ReturnPage, out var redirect))
        {
            _flowStore.Remove(transaction.State);
            return ScryerOAuthCallbackStageResult.Failed(ScryerFailure.AuthorizationExpired);
        }
        return new ScryerOAuthCallbackStageResult(true, transaction.ReturnPage, null, FinalizeCookiePrefix + transaction.FlowId,
            _cookieProtector.Protect(flowId + ":" + binding), "/", configuration.RedirectUri.Scheme == Uri.UriSchemeHttps,
            transaction.ExpiresAt, redirect);
    }

    public async Task<ScryerResult<bool>> FinalizeAsync(string jellyfinUserId, string? protectedCookie, CancellationToken cancellationToken)
    {
        if (!IsSafeUserId(jellyfinUserId) || string.IsNullOrWhiteSpace(protectedCookie) || protectedCookie.Length > 4096 ||
            !TryUnprotectCookie(protectedCookie, out var flowId, out var binding) || !_flowStore.TryBeginFinalize(flowId, binding, jellyfinUserId, out var transaction)) return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
        try
        {
            var configured = _configurationProvider.GetConfiguration();
            if (!configured.IsSuccess || !ConfigurationMatches(transaction, configured.Value!) || !_flowStore.IsFinalizeCurrent(transaction)) return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            if (!string.IsNullOrWhiteSpace(transaction.CallbackError) || string.IsNullOrWhiteSpace(transaction.CallbackCode)) return ScryerResult<bool>.Fail(ScryerFailure.NotConnected);
            var configuration = configured.Value!;
            var metadata = await _metadataClient.DiscoverAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (!metadata.IsSuccess || !_flowStore.IsFinalizeCurrent(transaction)) return ScryerResult<bool>.Fail(metadata.IsSuccess ? ScryerFailure.AuthorizationExpired : metadata.Failure!);
            var tokens = await _metadataClient.ExchangeAuthorizationCodeAsync(metadata.Value!, configuration, transaction.CallbackCode, transaction.CodeVerifier, CancellationToken.None).ConfigureAwait(false);
            if (!tokens.IsSuccess) return ScryerResult<bool>.Fail(tokens.Failure!);
            if (!_flowStore.IsFinalizeCurrent(transaction))
            {
                await _sessionService.RetireIssuedRefreshTokenAsync(
                    jellyfinUserId,
                    configuration,
                    tokens.Value!.RefreshToken,
                    CancellationToken.None).ConfigureAwait(false);
                return ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired);
            }
            var connected = await _sessionService.ConnectAsync(jellyfinUserId, configuration, tokens.Value!, CancellationToken.None).ConfigureAwait(false);
            if (!connected.IsSuccess)
            {
                await _sessionService.RetireIssuedRefreshTokenAsync(
                    jellyfinUserId,
                    configuration,
                    tokens.Value!.RefreshToken,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else if (!_flowStore.IsFinalizeCurrent(transaction))
            {
                // Disconnect may have invalidated the flow between the last generation check
                // and session persistence. Revoke/delete the just-written grant before return.
                var rolledBack = await _sessionService.DisconnectAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
                return rolledBack.IsSuccess
                    ? ScryerResult<bool>.Fail(ScryerFailure.AuthorizationExpired)
                    : ScryerResult<bool>.Fail(rolledBack.Failure!);
            }
            return connected;
        }
        finally { _flowStore.CompleteFinalize(transaction); }
    }

    private static bool ConfigurationMatches(ScryerOAuthFlowTransaction transaction, ScryerOAuthConfiguration configuration) =>
        FixedTimeEquals(transaction.ConfigurationFingerprint, CreateConfigurationFingerprint(configuration)) && FixedTimeEquals(transaction.RedirectUri, configuration.RedirectUri.AbsoluteUri);

    private bool TryUnprotectCookie(string protectedCookie, out string flowId, out string browserBinding)
    {
        flowId = string.Empty;
        browserBinding = string.Empty;
        try
        {
            var parts = _cookieProtector.Unprotect(protectedCookie).Split(':');
            if (parts.Length != 2 || !IsBase64UrlValue(parts[0], 16) || !IsBase64UrlValue(parts[1], 32))
            {
                return false;
            }

            flowId = parts[0];
            browserBinding = parts[1];
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static Uri BuildAuthorizationUri(Uri endpoint, ScryerOAuthConfiguration configuration, string state, string challenge)
    {
        var values = new[]
        {
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("client_id", configuration.ClientId),
            new KeyValuePair<string, string>("redirect_uri", configuration.RedirectUri.AbsoluteUri),
            new KeyValuePair<string, string>("scope", "library jellyfin-link"),
            new KeyValuePair<string, string>("state", state),
            new KeyValuePair<string, string>("code_challenge", challenge),
            new KeyValuePair<string, string>("code_challenge_method", "S256")
        };
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join("&", values.Select(static pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        return builder.Uri;
    }

    private static string CreateConfigurationFingerprint(ScryerOAuthConfiguration configuration)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\u001f", new[]
        {
            configuration.InternalAuthority.AbsoluteUri,
            configuration.PublicAuthority.AbsoluteUri,
            configuration.RedirectUri.AbsoluteUri,
            configuration.ClientId
        }));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string CreateRandomValue(int bytes)
    {
        var value = new byte[bytes];
        RandomNumberGenerator.Fill(value);
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryNormalizeReturnPage(string? value, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? "#/scryer-discovery" : value;
        return normalized.Length <= 64 && ReturnPages.Contains(normalized);
    }

    private static bool IsSafeUserId(string value) => IsWithinBound(value, MaximumUserIdLength) &&
        !string.IsNullOrWhiteSpace(value) && value.AsSpan().IndexOfAny('\r', '\n', '\0') < 0;

    private static bool IsWithinBound(string? value, int maximum) => value is null || value.Length <= maximum;

    private static bool HasExactlyOneCallbackOutcome(string? code, string? error) =>
        string.IsNullOrWhiteSpace(code) != string.IsNullOrWhiteSpace(error);

    private static bool IsBase64UrlValue(string value, int byteLength) => value.Length == ((byteLength * 4 + 2) / 3) &&
        value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>Internal controller result; it contains no OAuth credential material.</summary>
public sealed record ScryerOAuthStartResult(
    Uri AuthorizationUri,
    [property: JsonIgnore] string CookieName,
    [property: JsonIgnore] string CookieValue,
    [property: JsonIgnore] string CookiePath,
    [property: JsonIgnore] bool CookieSecure,
    [property: JsonIgnore] DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(ScryerOAuthStartResult) + " [redacted]";
}

/// <summary>Only value returned to the browser after a successful authenticated start POST.</summary>
public sealed record ScryerOAuthStartDto(string AuthorizationUrl);

/// <summary>Callback cookie location derived from server-held transaction state.</summary>
public sealed record ScryerOAuthCallbackCookie(string Name, string Path);

/// <summary>Server-only callback staging result; cookie fields are never returned as JSON.</summary>
public sealed record ScryerOAuthCallbackStageResult(
    bool Success,
    string ReturnPage,
    [property: JsonIgnore] ScryerFailure? Failure,
    [property: JsonIgnore] string? FinalizeCookieName,
    [property: JsonIgnore] string? FinalizeCookieValue,
    [property: JsonIgnore] string? FinalizeCookiePath,
    [property: JsonIgnore] bool FinalizeCookieSecure,
    [property: JsonIgnore] DateTimeOffset? ExpiresAt,
    [property: JsonIgnore] Uri? RedirectUri)
{
    public static ScryerOAuthCallbackStageResult Failed(ScryerFailure failure) =>
        new(false, "#/scryer-discovery", failure, null, null, null, false, null, null);

    public override string ToString() => nameof(ScryerOAuthCallbackStageResult) + " [redacted]";
}

public sealed record ScryerOAuthCallbackResult(bool Success, string ReturnPage, [property: JsonIgnore] ScryerFailure? Failure)
{
    public static ScryerOAuthCallbackResult Succeeded(string returnPage) => new(true, returnPage, null);
    public static ScryerOAuthCallbackResult Failed(ScryerFailure failure, string returnPage = "#/scryer-discovery") => new(false, returnPage, failure);
}

/// <summary>Browser-safe status: configuration and connection flags plus a stable failure code.</summary>
public sealed record ScryerAuthStatusDto(bool Configured, bool Connected, bool AccountLinked, ScryerAuthFailureDto? Failure)
{
    public static ScryerAuthStatusDto Failed(ScryerFailure failure) => new(false, false, false, ScryerAuthFailureDto.From(failure));
}

public sealed record ScryerAuthFailureDto(string Code)
{
    public static ScryerAuthFailureDto From(ScryerFailure failure) => new(failure.WireCode);
}
