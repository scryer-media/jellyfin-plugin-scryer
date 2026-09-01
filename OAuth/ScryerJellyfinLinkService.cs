using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Scryer.OAuth;

/// <summary>Fixed bearer-only boundary for the OAuth-to-Jellyfin account link mutation.</summary>
public interface IScryerJellyfinLinkService
{
    Task<ScryerResult<bool>> LinkAsync(ScryerOAuthConfiguration configuration, string jellyfinUserId, ScryerAccessTokenLease lease, CancellationToken cancellationToken);
}

public sealed class ScryerJellyfinLinkService : IScryerJellyfinLinkService
{
    private const int MaximumRequestBytes = 8 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const string Mutation = """mutation ScryerLinkCurrentOAuthJellyfinAccount($jellyfinUserId: String!) { linkCurrentOAuthJellyfinAccount(jellyfinUserId: $jellyfinUserId) { id userId provider externalUserId status } }""";
    private readonly HttpClient _httpClient;

    public ScryerJellyfinLinkService(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient(nameof(Jellyfin.Plugin.Scryer.Services.ScryerGraphqlService));
    }

    public async Task<ScryerResult<bool>> LinkAsync(ScryerOAuthConfiguration configuration, string jellyfinUserId, ScryerAccessTokenLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsCanonicalUserId(jellyfinUserId) || string.IsNullOrWhiteSpace(lease.AccessToken))
            return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            operationName = "ScryerLinkCurrentOAuthJellyfinAccount",
            query = Mutation,
            variables = new { jellyfinUserId }
        });
        if (payload.Length > MaximumRequestBytes) return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(configuration.InternalAuthority)) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var statusFailure = MapStatus(response.StatusCode);
            if (statusFailure is not null) return ScryerResult<bool>.Fail(statusFailure);
            if (!IsJsonResponse(response.Content.Headers.ContentType)) return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse);
            using var body = await ReadJsonAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var graphFailure = MapGraphQlFailure(body.RootElement);
            if (graphFailure is not null) return ScryerResult<bool>.Fail(graphFailure);
            return IsValidLinkPayload(body.RootElement, jellyfinUserId)
                ? ScryerResult<bool>.Success(true)
                : ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ScryerResult<bool>.Fail(ScryerFailure.Offline); }
        catch (HttpRequestException) { return ScryerResult<bool>.Fail(ScryerFailure.Offline); }
        catch (JsonException) { return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse); }
        catch (InvalidDataException) { return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse); }
        catch (UriFormatException) { return ScryerResult<bool>.Fail(ScryerFailure.InvalidResponse); }
    }

    private static bool IsValidLinkPayload(JsonElement root, string jellyfinUserId)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("linkCurrentOAuthJellyfinAccount", out var link) || link.ValueKind != JsonValueKind.Object) return false;
        return TryBoundedString(link, "id", out _) && TryBoundedString(link, "userId", out _) &&
            TryExactString(link, "provider", "JELLYFIN") && TryExactString(link, "status", "ACTIVE") &&
            TryExactString(link, "externalUserId", jellyfinUserId);
    }

    private static ScryerFailure? MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ScryerFailure.AuthorizationExpired,
        HttpStatusCode.Forbidden => new ScryerFailure(ScryerFailureCode.PermissionDenied, "Your Scryer account does not have permission to link Jellyfin."),
        HttpStatusCode.Conflict => new ScryerFailure(ScryerFailureCode.RequestConflict, "The Jellyfin account link conflicts with the current Scryer state."),
        HttpStatusCode.TooManyRequests => new ScryerFailure(ScryerFailureCode.RateLimited, "Scryer is rate limiting link requests. Try again shortly."),
        _ when (int)status >= 500 => ScryerFailure.Offline,
        _ when (int)status is >= 200 and <= 299 => null,
        _ => ScryerFailure.InvalidResponse
    };

    private static ScryerFailure? MapGraphQlFailure(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return null;
        foreach (var error in errors.EnumerateArray())
        {
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object &&
                ext.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString()?.Trim().ToUpperInvariant() : null;
            switch (code)
            {
                case "UNAUTHORIZED": return ScryerFailure.AuthorizationExpired;
                case "FORBIDDEN": case "PERMISSION_DENIED": return new ScryerFailure(ScryerFailureCode.PermissionDenied, "Your Scryer account does not have permission to link Jellyfin.");
                case "CONFLICT": case "REQUEST_CONFLICT": return new ScryerFailure(ScryerFailureCode.RequestConflict, "The Jellyfin account link conflicts with the current Scryer state.");
                case "RATE_LIMITED": case "TEMPORARY_UNAVAILABLE": return new ScryerFailure(ScryerFailureCode.RateLimited, "Scryer is rate limiting link requests. Try again shortly.");
                case "VALIDATION_ERROR": case "GRAPHQL_VALIDATION_FAILED": return ScryerFailure.InvalidResponse;
            }
        }
        return ScryerFailure.Internal;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumResponseBytes) throw new InvalidDataException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static bool TryBoundedString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 256;
    }
    private static bool TryExactString(JsonElement owner, string name, string expected) => TryBoundedString(owner, name, out var value) && string.Equals(value, expected, StringComparison.Ordinal);
    private static bool IsCanonicalUserId(string value) => Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty && string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);
    private static bool IsJsonResponse(MediaTypeHeaderValue? type) => type?.MediaType is not null && (type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.MediaType.Equals("application/graphql-response+json", StringComparison.OrdinalIgnoreCase));
    private static Uri BuildEndpoint(Uri authority) => new UriBuilder(authority) { Path = authority.AbsolutePath.TrimEnd('/') + "/graphql", Query = string.Empty, Fragment = string.Empty }.Uri;
}
