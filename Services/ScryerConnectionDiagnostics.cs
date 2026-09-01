using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.OAuth;
using Jellyfin.Plugin.Scryer.WebInjection;

namespace Jellyfin.Plugin.Scryer.Services;

/// <summary>
/// Performs bounded, read-only administrator diagnostics. Results intentionally exclude
/// credentials, response bodies, OAuth codes, and user identity data.
/// </summary>
public sealed class ScryerConnectionDiagnostics : IDisposable
{
    public const string MinimumScryerVersion = "0.19.6";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumGraphQlProbeRequestBytes = 8 * 1024;
    private const int MaximumGraphQlProbeResponseBytes = 128 * 1024;
    private const int MaximumLinkedGrantScanEntries = 256;
    private const string GraphQlCompatibilityProbe = """query ScryerAlphaCompatibilityProbe { __schema { queryType { fields { name } } mutationType { fields { name } } } }""";
    private static readonly HashSet<string> RequiredQueryFields = new(StringComparer.Ordinal)
    {
        "me", "libraries", "qualityProfileSettings", "discoveryHomeCards", "searchMetadataMulti",
        "discoveryItemDetail", "myMediaRequests", "mediaRequests", "calendarEpisodes",
        "downloadQueuePage", "downloadHistory"
    };
    private static readonly HashSet<string> RequiredMutationFields = new(StringComparer.Ordinal)
    {
        "submitMediaRequest", "approveMediaRequest", "dismissMediaRequest", "updateMyMediaRequest",
        "cancelMyMediaRequest", "linkCurrentOAuthJellyfinAccount"
    };
    private readonly HttpClient _graphQlClient;
    private readonly ScryerOAuthMetadataClient _oauthMetadataClient;
    private readonly ScryerInjectionStatus _injectionStatus;
    private readonly IScryerTokenStore _tokenStore;

    public ScryerConnectionDiagnostics(
        ScryerOAuthMetadataClient oauthMetadataClient,
        ScryerInjectionStatus injectionStatus,
        IScryerTokenStore tokenStore)
    {
        _oauthMetadataClient = oauthMetadataClient ?? throw new ArgumentNullException(nameof(oauthMetadataClient));
        _graphQlClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true);
        _injectionStatus = injectionStatus ?? throw new ArgumentNullException(nameof(injectionStatus));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    public async Task<ScryerDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var validation = ScryerConfigurationValidator.Validate(configuration);
        var diagnosticsEnabled = configuration.DiagnosticVerbosity != ScryerDiagnosticVerbosity.Off;
        var metadata = diagnosticsEnabled && validation.IsValid && validation.InternalBaseUrl is not null
            ? await CheckMetadataAsync(configuration, cancellationToken).ConfigureAwait(false)
            : ScryerOAuthMetadataDiagnostic.NotChecked;
        var graphQl = diagnosticsEnabled && validation.IsValid && validation.InternalBaseUrl is not null
            ? await CheckGraphQlAsync(validation.InternalBaseUrl, cancellationToken).ConfigureAwait(false)
            : ScryerReachabilityDiagnostic.NotChecked;
        var linkedGrants = await _tokenStore.GetActiveLinkedGrantCountAsync(MaximumLinkedGrantScanEntries, cancellationToken).ConfigureAwait(false);

        var compatibilityErrors = new List<string>();
        if (!diagnosticsEnabled)
        {
            compatibilityErrors.Add("Diagnostics are disabled by configuration.");
        }
        else if (!validation.IsValid)
        {
            compatibilityErrors.Add("Configuration must be valid before compatibility can be checked.");
        }
        else
        {
            if (!metadata.Reachable)
            {
                compatibilityErrors.Add("Scryer OAuth metadata is unreachable.");
            }
            else if (!metadata.SupportsAlphaContract)
            {
                compatibilityErrors.Add("Scryer OAuth metadata does not advertise the RFC 153 Alpha contract.");
            }

            if (!graphQl.Reachable)
            {
                compatibilityErrors.Add("The Scryer GraphQL endpoint is unreachable.");
            }
            else if (!graphQl.ContractVerified)
            {
                compatibilityErrors.Add(graphQl.FailureCode switch
                {
                    "authentication_required" => "The Scryer GraphQL endpoint requires authentication for its compatibility probe.",
                    "introspection_denied" => "The Scryer GraphQL endpoint denies the compatibility probe.",
                    _ => "The Scryer GraphQL endpoint does not prove the RFC 153 Alpha operation contract."
                });
            }
        }

        var preflightCompatible = validation.IsValid
            && diagnosticsEnabled
            && metadata.SupportsAlphaContract
            && graphQl.ContractVerified;

        metadata = metadata with
        {
            AuthorizationEndpoint = null,
            TokenEndpoint = null
        };

        if (configuration.DiagnosticVerbosity != ScryerDiagnosticVerbosity.Detailed)
        {
            metadata = metadata with { HttpStatus = null };
            graphQl = graphQl with { HttpStatus = null };
        }

        return new ScryerDiagnosticsSnapshot(
            validation.IsValid,
            validation.Errors,
            preflightCompatible,
            compatibilityErrors,
            MinimumScryerVersion,
            validation.InternalBaseUrl,
            validation.PublicBaseUrl,
            validation.CallbackUri,
            configuration.OAuthClientId,
            new ScryerFeatureDiagnostic(
                configuration.EnableDiscovery,
                configuration.EnableRequests,
                configuration.EnableCalendar,
                configuration.EnableDownloads),
            configuration.DiagnosticVerbosity,
            metadata,
            graphQl,
            _injectionStatus.GetSnapshot(),
            ObservedJellyfinVersion: typeof(Plugin).Assembly.GetReferencedAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.Name, "Jellyfin.Controller", StringComparison.Ordinal))
                ?.Version?.ToString(),
            LinkedUserCount: linkedGrants.Count,
            LinkedUserCountTruncated: linkedGrants.IsTruncated);
    }

    private async Task<ScryerOAuthMetadataDiagnostic> CheckMetadataAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var resolved = ScryerOAuthConfiguration.FromPluginConfiguration(configuration);
        if (!resolved.IsSuccess)
        {
            return ScryerOAuthMetadataDiagnostic.NotChecked;
        }

        var result = await _oauthMetadataClient.DiscoverAsync(resolved.Value!, cancellationToken).ConfigureAwait(false);
        var reachable = result.IsSuccess || result.Failure?.Code != ScryerFailureCode.ScryerOffline;
        return new ScryerOAuthMetadataDiagnostic(
            true,
            reachable,
            null,
            null,
            null,
            result.IsSuccess,
            result.Failure?.WireCode);
    }

    private async Task<ScryerReachabilityDiagnostic> CheckGraphQlAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(baseUrl + "/", UriKind.Absolute), "graphql");
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            operationName = "ScryerAlphaCompatibilityProbe",
            query = GraphQlCompatibilityProbe,
            variables = new { }
        }));
        if (payload.Length > MaximumGraphQlProbeRequestBytes)
        {
            return new ScryerReachabilityDiagnostic(true, false, false, null, "probe_too_large");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(payload)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ScryerReachabilityDiagnostic(true, true, false, statusCode, "authentication_required");
            }
            if (!IsJsonResponse(response.Content.Headers.ContentType))
            {
                return new ScryerReachabilityDiagnostic(true, (int)response.StatusCode < 500, false, statusCode, "non_json_response");
            }

            using var document = await ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var failureCode = ProbeFailureCode(document.RootElement);
            if (failureCode is not null)
            {
                return new ScryerReachabilityDiagnostic(true, true, false, statusCode, failureCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ScryerReachabilityDiagnostic(true, true, false, statusCode, "unexpected_status");
            }

            var contractVerified = HasRequiredRootFields(document.RootElement);
            return new ScryerReachabilityDiagnostic(
                true,
                true,
                contractVerified,
                statusCode,
                contractVerified ? null : "missing_alpha_fields");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ScryerReachabilityDiagnostic(true, false, false, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new ScryerReachabilityDiagnostic(true, false, false, null, "unreachable");
        }
        catch (UriFormatException)
        {
            return new ScryerReachabilityDiagnostic(true, false, false, null, "invalid_url");
        }
        catch (JsonException) { return new ScryerReachabilityDiagnostic(true, true, false, null, "invalid_json"); }
        catch (InvalidDataException) { return new ScryerReachabilityDiagnostic(true, true, false, null, "invalid_response"); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return await _graphQlClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }

    private static bool HasRequiredRootFields(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("__schema", out var schema) || schema.ValueKind != JsonValueKind.Object ||
            !TryReadFieldNames(schema, "queryType", out var queryFields) ||
            !TryReadFieldNames(schema, "mutationType", out var mutationFields))
        {
            return false;
        }

        return RequiredQueryFields.IsSubsetOf(queryFields) && RequiredMutationFields.IsSubsetOf(mutationFields);
    }

    private static bool TryReadFieldNames(JsonElement schema, string rootName, out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty(rootName, out var root) || root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() > 512)
        {
            return false;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 128)
            {
                return false;
            }

            names.Add(name.GetString()!);
        }

        return true;
    }

    private static string? ProbeFailureCode(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (var error in errors.EnumerateArray())
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageValue) &&
                messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() : null;
            if (message?.IndexOf("introspection", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "introspection_denied";
            }
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("extensions", out var extensions) &&
                extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("code", out var codeValue) &&
                codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString()?.Trim().ToUpperInvariant() : null;
            if (code is "UNAUTHORIZED" or "FORBIDDEN" or "PERMISSION_DENIED")
            {
                return "authentication_required";
            }

            if (code is "INTROSPECTION_DISABLED" or "INTROSPECTION_DENIED")
            {
                return "introspection_denied";
            }
        }

        return "probe_rejected";
    }

    private static bool IsJsonResponse(MediaTypeHeaderValue? contentType) => contentType?.MediaType is not null &&
        (contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         contentType.MediaType.Equals("application/graphql-response+json", StringComparison.OrdinalIgnoreCase));

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumGraphQlProbeResponseBytes) throw new InvalidDataException("GraphQL compatibility probe response exceeded the maximum size.");
            await buffered.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonDocument.Parse(buffered.ToArray());
    }

    public void Dispose() => _graphQlClient.Dispose();
}

public sealed record ScryerDiagnosticsSnapshot(
    bool ConfigurationValid,
    IReadOnlyList<string> ConfigurationErrors,
    bool PreflightCompatible,
    IReadOnlyList<string> CompatibilityErrors,
    string MinimumScryerVersion,
    string? InternalScryerBaseUrl,
    string? PublicScryerBaseUrl,
    string? CallbackUri,
    string OAuthClientId,
    ScryerFeatureDiagnostic Features,
    ScryerDiagnosticVerbosity DiagnosticVerbosity,
    ScryerOAuthMetadataDiagnostic OAuthMetadata,
    ScryerReachabilityDiagnostic GraphQl,
    ScryerInjectionSnapshot Injection,
    string? ObservedJellyfinVersion,
    int LinkedUserCount,
    bool LinkedUserCountTruncated);

public sealed record ScryerFeatureDiagnostic(bool Discovery, bool Requests, bool Calendar, bool Downloads);

public sealed record ScryerOAuthMetadataDiagnostic(
    bool Checked,
    bool Reachable,
    int? HttpStatus,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    bool SupportsAlphaContract,
    string? FailureCode)
{
    public static ScryerOAuthMetadataDiagnostic NotChecked { get; } = new(false, false, null, null, null, false, "not_checked");
}

public sealed record ScryerReachabilityDiagnostic(bool Checked, bool Reachable, bool ContractVerified, int? HttpStatus, string? FailureCode)
{
    public static ScryerReachabilityDiagnostic NotChecked { get; } = new(false, false, false, null, "not_checked");
}
