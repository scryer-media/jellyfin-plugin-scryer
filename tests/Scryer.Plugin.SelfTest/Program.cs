using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Scryer.Api;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.OAuth;

var tests = new (string Name, Func<Task> Run)[]
{
    ("trusted Jellyfin actor canonicalizes and rejects ambiguity", TrustedActorAsync),
    ("configuration derives only the exact callback", ConfigurationAsync),
    ("OAuth metadata enforces the fixed contract", MetadataAsync),
    ("token exchange preserves exact required scopes", TokenExchangeAsync),
    ("PKCE is RFC 7636 S256", PkceAsync),
    ("Jellyfin link GraphQL request and response are fixed", JellyfinLinkAsync),
    ("stable failure mapping is browser-safe", FailureMappingAsync),
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures.Add($"FAIL {name}: {error.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static Task TrustedActorAsync()
{
    var userId = "01234567-89AB-CDEF-0123-456789ABCDEF";
    var trusted = Principal(userId, "false");
    Assert.True(TrustedJellyfinActor.TryGetUserId(trusted, out var canonical));
    Assert.Equal("0123456789abcdef0123456789abcdef", canonical);

    Assert.False(TrustedJellyfinActor.TryGetUserId(Principal(userId, "true"), out _));
    Assert.False(TrustedJellyfinActor.TryGetUserId(Principal(Guid.Empty.ToString("D"), "false"), out _));
    Assert.False(TrustedJellyfinActor.TryGetUserId(Principal("not-a-guid", "false"), out _));

    var ambiguous = new ClaimsPrincipal(new[]
    {
        Identity(userId, "false"),
        Identity("11111111-1111-1111-1111-111111111111", "false"),
    });
    Assert.False(TrustedJellyfinActor.TryGetUserId(ambiguous, out _));

    var splitClaims = new ClaimsPrincipal(new[]
    {
        new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userId) }, "test"),
        new ClaimsIdentity(new[] { new Claim("Jellyfin-IsApiKey", "false") }, "test"),
    });
    Assert.False(TrustedJellyfinActor.TryGetUserId(splitClaims, out _));
    return Task.CompletedTask;
}

static Task ConfigurationAsync()
{
    var configuration = ValidPluginConfiguration();
    var validation = ScryerConfigurationValidator.Validate(configuration);
    Assert.True(validation.IsValid);
    Assert.Equal("https://jellyfin.example.test/base/Scryer/Auth/Callback", validation.CallbackUri);

    configuration.JellyfinPublicBaseUrl = "https://jellyfin.example.test/base?bad=true";
    Assert.False(ScryerConfigurationValidator.Validate(configuration).IsValid);

    configuration = ValidPluginConfiguration();
    configuration.ScryerPublicBaseUrl = "http://scryer.example.test";
    Assert.False(ScryerConfigurationValidator.Validate(configuration).IsValid);
    return Task.CompletedTask;
}

static async Task MetadataAsync()
{
    var configuration = ValidOAuthConfiguration();
    var handler = new RecordingHandler(_ => JsonResponse(ValidMetadataJson()));
    var client = new ScryerOAuthMetadataClient(handler);
    var result = await client.DiscoverAsync(configuration, CancellationToken.None);
    Assert.True(result.IsSuccess, result.Failure?.Code.ToString());
    Assert.Equal("https://scryer.example.test/oauth/authorize", result.Value!.AuthorizationEndpoint.AbsoluteUri.TrimEnd('/'));
    Assert.Equal("http://127.0.0.1:41111/base/oauth/token", result.Value.TokenEndpoint.AbsoluteUri.TrimEnd('/'));
    Assert.Equal("/base/.well-known/oauth-authorization-server", handler.Requests.Single().RequestUri!.AbsolutePath);

    var badHandler = new RecordingHandler(_ => JsonResponse(ValidMetadataJson(tokenEndpoint: "https://evil.example.test/oauth/token")));
    var incompatible = await new ScryerOAuthMetadataClient(badHandler).DiscoverAsync(configuration, CancellationToken.None);
    Assert.False(incompatible.IsSuccess);
    Assert.Equal(ScryerFailureCode.ScryerIncompatible, incompatible.Failure!.Code);
}

static async Task TokenExchangeAsync()
{
    var configuration = ValidOAuthConfiguration();
    var handler = new RecordingHandler(_ => JsonResponse(TokenJson("library jellyfin-link")));
    var client = new ScryerOAuthMetadataClient(handler);
    var metadata = FixedMetadata(configuration);
    var exchange = await client.ExchangeAuthorizationCodeAsync(metadata, configuration, "code", "verifier", CancellationToken.None);
    Assert.True(exchange.IsSuccess, exchange.Failure?.Code.ToString());
    Assert.Equal("library jellyfin-link", exchange.Value!.Scope);
    var form = handler.Requests.Single().Content!;
    Assert.Contains("grant_type=authorization_code", form);
    Assert.Contains("redirect_uri=https%3A%2F%2Fjellyfin.example.test%2Fbase%2FScryer%2FAuth%2FCallback", form);
    Assert.Contains("code_verifier=verifier", form);

    var wrongScope = await new ScryerOAuthMetadataClient(new RecordingHandler(_ => JsonResponse(TokenJson("library"))))
        .ExchangeAuthorizationCodeAsync(metadata, configuration, "code", "verifier", CancellationToken.None);
    Assert.False(wrongScope.IsSuccess);
    Assert.Equal(ScryerFailureCode.InvalidResponse, wrongScope.Failure!.Code);
}

static Task PkceAsync()
{
    var pair = ScryerPkce.Create();
    Assert.True(pair.Verifier.Length is >= 43 and <= 128);
    Assert.True(pair.Verifier.All(IsPkceCharacter));
    var expected = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)));
    Assert.Equal(expected, pair.Challenge);
    Assert.False(pair.ToString().Contains(pair.Verifier, StringComparison.Ordinal));
    return Task.CompletedTask;
}

static async Task JellyfinLinkAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var handler = new RecordingHandler(_ => JsonResponse(JsonSerializer.Serialize(new
    {
        data = new
        {
            linkCurrentOAuthJellyfinAccount = new
            {
                id = "link",
                userId = "scryer-user",
                provider = "JELLYFIN",
                externalUserId = jellyfinUserId,
                status = "ACTIVE",
            },
        },
    })));
    var service = new ScryerJellyfinLinkService(new SingleClientFactory(handler));
    var linked = await service.LinkAsync(ValidOAuthConfiguration(), jellyfinUserId, new ScryerAccessTokenLease("access-token", DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);
    Assert.True(linked.IsSuccess, linked.Failure?.Code.ToString());
    var request = handler.Requests.Single();
    Assert.Equal("/base/graphql", request.RequestUri!.AbsolutePath);
    Assert.Equal("Bearer", request.Authorization!.Scheme);
    Assert.Equal("access-token", request.Authorization.Parameter);
    using var body = JsonDocument.Parse(request.Content!);
    Assert.Equal("ScryerLinkCurrentOAuthJellyfinAccount", body.RootElement.GetProperty("operationName").GetString());
    Assert.Equal(jellyfinUserId, body.RootElement.GetProperty("variables").GetProperty("jellyfinUserId").GetString());
    Assert.Contains("linkCurrentOAuthJellyfinAccount(jellyfinUserId: $jellyfinUserId)", body.RootElement.GetProperty("query").GetString()!);
    Assert.False(body.RootElement.GetProperty("query").GetString()!.Contains("idempotency", StringComparison.OrdinalIgnoreCase));

    var malformed = new ScryerJellyfinLinkService(new SingleClientFactory(new RecordingHandler(_ => JsonResponse("{\"data\":{\"linkCurrentOAuthJellyfinAccount\":{\"id\":\"link\",\"userId\":\"scryer-user\",\"provider\":\"JELLYFIN\",\"externalUserId\":\"wrong\",\"status\":\"ACTIVE\"}}}"))));
    var rejected = await malformed.LinkAsync(ValidOAuthConfiguration(), jellyfinUserId, new ScryerAccessTokenLease("access-token", DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);
    Assert.False(rejected.IsSuccess);
    Assert.Equal(ScryerFailureCode.InvalidResponse, rejected.Failure!.Code);
}

static Task FailureMappingAsync()
{
    var result = ScryerFailureHttpMapper.ToActionResult(new ScryerFailure(ScryerFailureCode.RequestConflict, "sensitive upstream detail"));
    Assert.Equal(409, result.StatusCode);
    var response = Assert.IsType<ScryerFailureResponse>(result.Value);
    Assert.Equal("request_conflict", response.Code);
    Assert.Equal("The request conflicts with the current Scryer state.", response.Message);
    Assert.False(response.Message.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static ClaimsPrincipal Principal(string userId, string apiKey) => new(Identity(userId, apiKey));
static ClaimsIdentity Identity(string userId, string apiKey) => new(new[]
{
    new Claim("Jellyfin-UserId", userId),
    new Claim("Jellyfin-IsApiKey", apiKey),
}, "test");

static PluginConfiguration ValidPluginConfiguration() => new()
{
    ScryerInternalBaseUrl = "http://127.0.0.1:41111/base",
    AllowInsecureInternalScryerHttp = true,
    ScryerPublicBaseUrl = "https://scryer.example.test",
    JellyfinPublicBaseUrl = "https://jellyfin.example.test/base",
    OAuthClientId = "jellyfin-plugin",
};

static ScryerOAuthConfiguration ValidOAuthConfiguration() => ScryerOAuthConfiguration.FromPluginConfiguration(ValidPluginConfiguration()).Value!;

static ScryerOAuthMetadata FixedMetadata(ScryerOAuthConfiguration configuration) => new(
    new Uri(configuration.PublicAuthority, "oauth/authorize"),
    new Uri(configuration.InternalAuthority, "oauth/token"),
    new Uri(configuration.InternalAuthority, "oauth/revoke"),
    configuration.PublicAuthority.AbsoluteUri.TrimEnd('/'));

static string ValidMetadataJson(string? tokenEndpoint = null) => $$"""
    {"issuer":"https://scryer.example.test/","authorization_endpoint":"https://scryer.example.test/oauth/authorize","token_endpoint":"{{tokenEndpoint ?? "https://scryer.example.test/oauth/token"}}","revocation_endpoint":"https://scryer.example.test/oauth/revoke","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token"],"scopes_supported":["library","jellyfin-link"],"code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["none"],"revocation_endpoint_auth_methods_supported":["none"]}
    """;

static string TokenJson(string scope) => $$"""{"access_token":"access","refresh_token":"refresh","token_type":"Bearer","expires_in":300,"scope":"{{scope}}"}""";
static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static bool IsPkceCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '.' or '_' or '~';
static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

sealed record RecordedRequest(Uri? RequestUri, AuthenticationHeaderValue? Authorization, string? Content);

sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var authorization = request.Headers.Authorization is null
            ? null
            : new AuthenticationHeaderValue(request.Headers.Authorization.Scheme, request.Headers.Authorization.Parameter);
        Requests.Add(new RecordedRequest(request.RequestUri, authorization, content));
        return responder(request);
    }
}

sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly HttpClient _client = new(handler, disposeHandler: false);
    public HttpClient CreateClient(string name) => _client;
}

static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
    }

    public static T IsType<T>(object? value) where T : class => value as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
