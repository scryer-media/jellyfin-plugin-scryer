using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>Discovers Scryer's fixed public-client OAuth contract and performs server-only grants.</summary>
public sealed class ScryerOAuthMetadataClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _httpClient;

    public ScryerOAuthMetadataClient()
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true))
    {
    }

    /// <summary>Inject a handler for isolated protocol tests.</summary>
    public ScryerOAuthMetadataClient(HttpMessageHandler messageHandler)
        : this(new HttpClient(messageHandler ?? throw new ArgumentNullException(nameof(messageHandler)), disposeHandler: false))
    {
    }

    private ScryerOAuthMetadataClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ScryerResult<ScryerOAuthMetadata>> DiscoverAsync(
        ScryerOAuthConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var probe = await DiscoverWithObservationAsync(configuration, cancellationToken).ConfigureAwait(false);
        return probe.Result;
    }

    /// <summary>
    /// Performs discovery and additionally reports the transport-level facts an administrator
    /// needs to tell an unreachable server apart from a reverse proxy that answered the
    /// well-known path with something that is not the metadata document. The parse itself is
    /// unchanged: the observed content type is reported, never used to reject a response.
    /// </summary>
    public async Task<ScryerOAuthMetadataProbe> DiscoverWithObservationAsync(
        ScryerOAuthConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var documentUri = new Uri(configuration.InternalAuthority, ".well-known/oauth-authorization-server");
        int? httpStatus = null;
        bool? responseIsJson = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, documentUri);
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            responseIsJson = IsJsonMediaType(response.Content.Headers.ContentType?.MediaType);
            if (!response.IsSuccessStatusCode)
            {
                return new ScryerOAuthMetadataProbe(
                    ScryerResult<ScryerOAuthMetadata>.Fail(MapStatus(response.StatusCode)), httpStatus, responseIsJson);
            }

            using var document = await ReadJsonAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return new ScryerOAuthMetadataProbe(ParseMetadata(document.RootElement, configuration), httpStatus, responseIsJson);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ScryerOAuthMetadataProbe(ScryerResult<ScryerOAuthMetadata>.Fail(ScryerFailure.Offline), httpStatus, responseIsJson);
        }
        catch (HttpRequestException)
        {
            return new ScryerOAuthMetadataProbe(ScryerResult<ScryerOAuthMetadata>.Fail(ScryerFailure.Offline), httpStatus, responseIsJson);
        }
        catch (JsonException)
        {
            return new ScryerOAuthMetadataProbe(ScryerResult<ScryerOAuthMetadata>.Fail(ScryerFailure.InvalidResponse), httpStatus, responseIsJson);
        }
        catch (InvalidDataException)
        {
            return new ScryerOAuthMetadataProbe(ScryerResult<ScryerOAuthMetadata>.Fail(ScryerFailure.InvalidResponse), httpStatus, responseIsJson);
        }
        catch (UriFormatException)
        {
            return new ScryerOAuthMetadataProbe(ScryerResult<ScryerOAuthMetadata>.Fail(ScryerFailure.InvalidResponse), httpStatus, responseIsJson);
        }
    }

    private static bool IsJsonMediaType(string? mediaType) => mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    public Task<ScryerResult<ScryerOAuthTokenSet>> ExchangeAuthorizationCodeAsync(
        ScryerOAuthMetadata metadata,
        ScryerOAuthConfiguration configuration,
        string authorizationCode,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        return RequestTokenAsync(metadata.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = configuration.ClientId,
            ["code"] = authorizationCode,
            ["redirect_uri"] = configuration.RedirectUri.AbsoluteUri,
            ["code_verifier"] = codeVerifier
        }, cancellationToken);
    }

    public Task<ScryerResult<ScryerOAuthTokenSet>> RefreshAsync(
        ScryerOAuthMetadata metadata,
        ScryerOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return RequestTokenAsync(metadata.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = configuration.ClientId,
            ["refresh_token"] = refreshToken
        }, cancellationToken);
    }

    public async Task<ScryerResult<bool>> RevokeAsync(
        ScryerOAuthMetadata metadata,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        return await RevokeAtEndpointAsync(metadata.RevocationEndpoint, refreshToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes a protected stored grant without performing metadata discovery. This is used for
    /// disconnect and authority replacement so a changed or offline discovery document cannot
    /// strand the old refresh-token family.
    /// </summary>
    public Task<ScryerResult<bool>> RevokeStoredGrantAsync(
        ScryerGrantKey storedBinding,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storedBinding);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        if (!TryCreateStoredAuthority(storedBinding.Authority, out var authority))
        {
            return Task.FromResult(ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse));
        }

        return RevokeAtEndpointAsync(BuildEndpoint(authority, "oauth/revoke"), refreshToken, cancellationToken);
    }

    private async Task<ScryerResult<bool>> RevokeAtEndpointAsync(
        Uri endpoint,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = refreshToken,
                    ["token_type_hint"] = "refresh_token"
                })
            };
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? ScryerResult<bool>.Success(true)
                : ScryerResult<bool>.Fail(MapStatus(response.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ScryerResult<bool>.Fail(ScryerFailure.Offline);
        }
        catch (HttpRequestException)
        {
            return ScryerResult<bool>.Fail(ScryerFailure.Offline);
        }
    }

    private async Task<ScryerResult<ScryerOAuthTokenSet>> RequestTokenAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ScryerResult<ScryerOAuthTokenSet>.Fail(MapStatus(response.StatusCode));
            }

            using var document = await ReadJsonAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return ParseTokenSet(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ScryerResult<ScryerOAuthTokenSet>.Fail(ScryerFailure.Offline);
        }
        catch (HttpRequestException)
        {
            return ScryerResult<ScryerOAuthTokenSet>.Fail(ScryerFailure.Offline);
        }
        catch (JsonException)
        {
            return ScryerResult<ScryerOAuthTokenSet>.Fail(ScryerFailure.InvalidResponse);
        }
        catch (InvalidDataException)
        {
            return ScryerResult<ScryerOAuthTokenSet>.Fail(ScryerFailure.InvalidResponse);
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return timeout;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                throw new InvalidDataException("OAuth response exceeded the maximum size.");
            }

            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonDocument.Parse(buffered.ToArray());
    }

    private static ScryerResult<ScryerOAuthMetadata> ParseMetadata(JsonElement root, ScryerOAuthConfiguration configuration)
    {
        var internalAuthorization = BuildEndpoint(configuration.InternalAuthority, "oauth/authorize");
        var internalToken = BuildEndpoint(configuration.InternalAuthority, "oauth/token");
        var internalRevocation = BuildEndpoint(configuration.InternalAuthority, "oauth/revoke");
        var publicAuthorization = BuildEndpoint(configuration.PublicAuthority, "oauth/authorize");
        var publicToken = BuildEndpoint(configuration.PublicAuthority, "oauth/token");
        var publicRevocation = BuildEndpoint(configuration.PublicAuthority, "oauth/revoke");
        if (!HasString(root, "response_types_supported", "code") ||
            !HasString(root, "grant_types_supported", "authorization_code") ||
            !HasString(root, "grant_types_supported", "refresh_token") ||
            !HasString(root, "scopes_supported", "library") ||
            !HasString(root, "scopes_supported", "jellyfin-link") ||
            !HasString(root, "code_challenge_methods_supported", "S256") ||
            !HasString(root, "token_endpoint_auth_methods_supported", "none") ||
            !HasString(root, "revocation_endpoint_auth_methods_supported", "none") ||
            !TryReadEndpoint(root, "authorization_endpoint", out var advertisedAuthorization) ||
            !TryReadEndpoint(root, "token_endpoint", out var advertisedToken) ||
            !TryReadEndpoint(root, "revocation_endpoint", out var advertisedRevocation) ||
            !TryReadEndpoint(root, "issuer", out var advertisedIssuer) ||
            advertisedIssuer.AbsolutePath != "/" ||
            !(MatchesMetadataSet(
                    advertisedIssuer,
                    advertisedAuthorization,
                    advertisedToken,
                    advertisedRevocation,
                    configuration.InternalAuthority,
                    internalAuthorization,
                    internalToken,
                    internalRevocation) ||
                MatchesMetadataSet(
                    advertisedIssuer,
                    advertisedAuthorization,
                    advertisedToken,
                    advertisedRevocation,
                    configuration.PublicAuthority,
                    publicAuthorization,
                    publicToken,
                    publicRevocation) ||
                (SameOrigin(advertisedIssuer, configuration.PublicAuthority) &&
                    MatchesMetadataSet(
                        advertisedIssuer,
                        advertisedAuthorization,
                        advertisedToken,
                        advertisedRevocation,
                        advertisedIssuer,
                        BuildEndpoint(advertisedIssuer, "oauth/authorize"),
                        BuildEndpoint(advertisedIssuer, "oauth/token"),
                        BuildEndpoint(advertisedIssuer, "oauth/revoke")))))
        {
            return ScryerResult<ScryerOAuthMetadata>.Fail(ScryerFailure.Incompatible);
        }

        // Credential-bearing requests use fixed endpoint paths on configured authorities.
        // The browser-facing authorization endpoint may use the advertised path when it is
        // on the configured public origin, so a copied SPA path cannot leak into the OAuth URL.
        var browserAuthorization = SameOrigin(advertisedAuthorization, configuration.PublicAuthority)
            ? advertisedAuthorization
            : publicAuthorization;
        return ScryerResult<ScryerOAuthMetadata>.Success(new ScryerOAuthMetadata(
            browserAuthorization,
            internalToken,
            internalRevocation,
            advertisedIssuer.AbsoluteUri.TrimEnd('/')));
    }

    private static ScryerResult<ScryerOAuthTokenSet> ParseTokenSet(JsonElement root)
    {
        if (!TryReadNonEmptyString(root, "access_token", out var accessToken) ||
            !TryReadNonEmptyString(root, "refresh_token", out var refreshToken) ||
            !TryReadNonEmptyString(root, "token_type", out var tokenType) ||
            !string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("expires_in", out var expiresInProperty) ||
            !expiresInProperty.TryGetInt64(out var expiresIn) || expiresIn <= 0 || expiresIn > 31_536_000 ||
            !TryReadNonEmptyString(root, "scope", out var scope) || !ScryerOAuthScopes.TryNormalizeExact(scope, out var normalizedScope))
        {
            return ScryerResult<ScryerOAuthTokenSet>.Fail(ScryerFailure.InvalidResponse);
        }

        return ScryerResult<ScryerOAuthTokenSet>.Success(new ScryerOAuthTokenSet(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            normalizedScope));
    }

    private static bool TryReadEndpoint(JsonElement root, string propertyName, out Uri endpoint)
    {
        endpoint = null!;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString() ?? string.Empty;
        if (raw.Contains('\\') ||
            raw.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool TryReadNonEmptyString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static bool HasString(JsonElement root, string propertyName, string expected) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array &&
        property.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), expected, StringComparison.Ordinal));

    private static Uri BuildEndpoint(Uri authority, string relativePath)
    {
        var basePath = authority.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(authority)
        {
            Path = (basePath == "/" ? string.Empty : basePath) + "/" + relativePath.TrimStart('/'),
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static bool TryCreateStoredAuthority(string value, out Uri authority)
    {
        authority = null!;
        if (!Uri.TryCreate(value + "/", UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        authority = parsed;
        return true;
    }

    private static bool MatchesExpectedEndpoint(Uri advertised, Uri expected) =>
        SameOrigin(advertised, expected) && string.Equals(advertised.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal);

    private static bool MatchesMetadataSet(
        Uri issuer,
        Uri authorization,
        Uri token,
        Uri revocation,
        Uri configuredAuthority,
        Uri expectedAuthorization,
        Uri expectedToken,
        Uri expectedRevocation) =>
        SameOrigin(issuer, configuredAuthority) &&
        MatchesExpectedEndpoint(authorization, expectedAuthorization) &&
        MatchesExpectedEndpoint(token, expectedToken) &&
        MatchesExpectedEndpoint(revocation, expectedRevocation);

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static ScryerFailure MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest => ScryerFailure.AuthorizationExpired,
        HttpStatusCode.TooManyRequests => new ScryerFailure(ScryerFailureCode.RateLimited, "Scryer is rate limiting requests. Try again shortly."),
        _ when (int)statusCode >= 500 => ScryerFailure.Offline,
        _ => ScryerFailure.InvalidResponse
    };
}

/// <summary>
/// Discovery outcome plus the transport facts an administrator diagnostic needs. It carries no
/// response body, endpoint, or credential material.
/// </summary>
public sealed record ScryerOAuthMetadataProbe(
    ScryerResult<ScryerOAuthMetadata> Result,
    int? HttpStatus,
    bool? ResponseIsJson);
