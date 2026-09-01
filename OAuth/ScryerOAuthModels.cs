using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Scryer.Configuration;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>Stable, browser-safe error vocabulary from RFC 153.</summary>
public enum ScryerFailureCode
{
    NotConfigured,
    NotConnected,
    AuthorizationExpired,
    PermissionDenied,
    ScryerOffline,
    ScryerIncompatible,
    RateLimited,
    InvalidResponse,
    RequestConflict,
    InternalError
}

public sealed record ScryerFailure(ScryerFailureCode Code, string Message)
{
    public string WireCode => Code switch
    {
        ScryerFailureCode.NotConfigured => "not_configured",
        ScryerFailureCode.NotConnected => "not_connected",
        ScryerFailureCode.AuthorizationExpired => "authorization_expired",
        ScryerFailureCode.PermissionDenied => "permission_denied",
        ScryerFailureCode.ScryerOffline => "scryer_offline",
        ScryerFailureCode.ScryerIncompatible => "scryer_incompatible",
        ScryerFailureCode.RateLimited => "rate_limited",
        ScryerFailureCode.InvalidResponse => "invalid_response",
        ScryerFailureCode.RequestConflict => "request_conflict",
        _ => "internal_error"
    };

    public static ScryerFailure NotConfigured { get; } = new(ScryerFailureCode.NotConfigured, "Scryer has not been configured.");
    public static ScryerFailure NotConnected { get; } = new(ScryerFailureCode.NotConnected, "Connect a Scryer account to continue.");
    public static ScryerFailure AuthorizationExpired { get; } = new(ScryerFailureCode.AuthorizationExpired, "Your Scryer connection has expired. Reconnect to continue.");
    public static ScryerFailure Offline { get; } = new(ScryerFailureCode.ScryerOffline, "Scryer is currently unreachable.");
    public static ScryerFailure Incompatible { get; } = new(ScryerFailureCode.ScryerIncompatible, "The configured Scryer server is incompatible with this plugin.");
    public static ScryerFailure InvalidResponse { get; } = new(ScryerFailureCode.InvalidResponse, "Scryer returned an invalid response.");
    public static ScryerFailure Internal { get; } = new(ScryerFailureCode.InternalError, "The connection could not be completed.");
}

public sealed class ScryerResult<T>
{
    private ScryerResult(T? value, ScryerFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }
    public ScryerFailure? Failure { get; }
    public bool IsSuccess => Failure is null;

    public static ScryerResult<T> Success(T value) => new(value, null);
    public static ScryerResult<T> Fail(ScryerFailure failure) => new(default, failure);
}

public sealed record ScryerOAuthConfiguration(
    Uri InternalAuthority,
    Uri PublicAuthority,
    Uri RedirectUri,
    string ClientId)
{
    public static ScryerResult<ScryerOAuthConfiguration> FromPluginConfiguration(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = ScryerConfigurationValidator.Validate(configuration);
        if (!validation.IsValid || validation.InternalBaseUrl is null || validation.PublicBaseUrl is null || validation.CallbackUri is null ||
            !Uri.TryCreate(validation.InternalBaseUrl + "/", UriKind.Absolute, out var internalAuthority) ||
            !Uri.TryCreate(validation.PublicBaseUrl + "/", UriKind.Absolute, out var publicAuthority) ||
            !Uri.TryCreate(validation.CallbackUri, UriKind.Absolute, out var redirectUri))
        {
            return ScryerResult<ScryerOAuthConfiguration>.Fail(ScryerFailure.NotConfigured);
        }

        return ScryerResult<ScryerOAuthConfiguration>.Success(new ScryerOAuthConfiguration(
            internalAuthority,
            publicAuthority,
            redirectUri,
            configuration.OAuthClientId));
    }
}

public interface IScryerOAuthConfigurationProvider
{
    ScryerResult<ScryerOAuthConfiguration> GetConfiguration();
}

/// <summary>Production configuration adapter; tests can supply a fixed provider instead.</summary>
public sealed class PluginScryerOAuthConfigurationProvider : IScryerOAuthConfigurationProvider
{
    public ScryerResult<ScryerOAuthConfiguration> GetConfiguration()
    {
        return Plugin.Instance is null
            ? ScryerResult<ScryerOAuthConfiguration>.Fail(ScryerFailure.NotConfigured)
            : ScryerOAuthConfiguration.FromPluginConfiguration(Plugin.Instance.Configuration);
    }
}

public sealed record ScryerOAuthMetadata(
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri RevocationEndpoint,
    string Issuer);

/// <summary>
/// This carries credentials only inside server-side OAuth/session services. It is deliberately
/// not a plugin DTO and must never be logged or serialized into an API response.
/// </summary>
public sealed class ScryerOAuthTokenSet
{
    public ScryerOAuthTokenSet(string accessToken, string refreshToken, DateTimeOffset accessTokenExpiresAt, string scope)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        Scope = scope;
    }

    [JsonIgnore]
    public string AccessToken { get; }

    [JsonIgnore]
    public string RefreshToken { get; }
    public DateTimeOffset AccessTokenExpiresAt { get; }
    public string Scope { get; }

    public override string ToString() => nameof(ScryerOAuthTokenSet) + " [redacted]";
}

public sealed record ScryerGrantKey(string JellyfinUserId, string Authority, string ClientId)
{
    public static ScryerGrantKey Create(string jellyfinUserId, ScryerOAuthConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jellyfinUserId);
        ArgumentNullException.ThrowIfNull(configuration);
        return new ScryerGrantKey(
            jellyfinUserId,
            configuration.InternalAuthority.AbsoluteUri.TrimEnd('/'),
            configuration.ClientId);
    }

    public string CacheIdentity => string.Join("\u001f", JellyfinUserId, Authority, ClientId);
}

public sealed class ScryerRefreshGrant
{
    public ScryerRefreshGrant(
        ScryerGrantKey key,
        string refreshToken,
        DateTimeOffset updatedAt,
        ScryerGrantLinkState linkState = ScryerGrantLinkState.Active,
        string? linkIdempotencyKey = null,
        int linkAttempts = 0)
    {
        Key = key;
        RefreshToken = refreshToken;
        UpdatedAt = updatedAt;
        LinkState = linkState;
        LinkIdempotencyKey = linkIdempotencyKey;
        LinkAttempts = linkAttempts;
    }

    public ScryerGrantKey Key { get; }
    [JsonIgnore]
    public string RefreshToken { get; }
    public DateTimeOffset UpdatedAt { get; }
    public ScryerGrantLinkState LinkState { get; }
    [JsonIgnore]
    public string? LinkIdempotencyKey { get; }
    public int LinkAttempts { get; }

    public override string ToString() => nameof(ScryerRefreshGrant) + " [redacted]";
}

/// <summary>Only an active grant may be used by normal Scryer feature operations.</summary>
public enum ScryerGrantLinkState
{
    PendingLink,
    /// <summary>
    /// The refresh-token family is locally quarantined while bounded revocation cleanup retries.
    /// It is never eligible for linking, refresh, or feature access.
    /// </summary>
    PendingRevoke,
    Active
}

public enum ScryerGrantReadState
{
    Missing,
    Found,
    Legacy,
    Corrupt,
    Unavailable
}

public sealed record ScryerGrantReadResult(ScryerGrantReadState State, ScryerRefreshGrant? Grant, bool RequiresInvalidation = false)
{
    public static ScryerGrantReadResult Missing { get; } = new(ScryerGrantReadState.Missing, null);
}

public sealed class ScryerAccessTokenLease
{
    public ScryerAccessTokenLease(string accessToken, DateTimeOffset expiresAt)
    {
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
    }

    [JsonIgnore]
    public string AccessToken { get; }
    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() => nameof(ScryerAccessTokenLease) + " [redacted]";
}
