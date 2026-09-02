using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Scryer.Api;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.OAuth;
using Jellyfin.Plugin.Scryer.Services;
using Jellyfin.Plugin.Scryer.WebInjection;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

var tests = new (string Name, Func<Task> Run)[]
{
    ("trusted Jellyfin actor canonicalizes and rejects ambiguity", TrustedActorAsync),
    ("configuration derives only the exact callback", ConfigurationAsync),
    ("OAuth metadata enforces the fixed contract", MetadataAsync),
    ("web injection preserves original response bytes", WebInjectionPreservesBytesAsync),
    ("token exchange accepts only exact library scope sets", TokenExchangeAsync),
    ("PKCE is RFC 7636 S256", PkceAsync),
    ("Jellyfin link GraphQL request and response are fixed", JellyfinLinkAsync),
    ("stable failure mapping is browser-safe", FailureMappingAsync),
    ("controllers reject untrusted actors and report unlinked accounts", ControllerActorAndUnlinkedAsync),
    ("controllers reject bounded invalid input before GraphQL", ControllerInputBoundsAsync),
    ("disabled feature filters reject direct endpoints", DisabledFeatureAsync),
    ("GraphQL normalizes upstream protocol failures", GraphqlProtocolFailuresAsync),
    ("GraphQL maps bounded Scryer capabilities", CapabilityMappingAsync),
    ("detached revoke journal survives restart discovery, promotion, and cleanup", DetachedRevokeJournalAsync),
    ("retiring a detached issued family preserves the current family", DetachedRetirementPreservesCurrentAsync),
    ("detached revoke deletion failure retains its tombstone", DetachedDeletionFailureAsync),
    ("disconnect fails closed when detached state cannot be read", DisconnectDetachedReadFailureAsync),
    ("disconnect fails closed on a corrupt encrypted detached record", DisconnectCorruptDetachedRecordAsync),
    ("OAuth flow binds callback and finalize to one browser and initiating user", OAuthFlowBindingAsync),
    ("OAuth flow rejects expired and malformed callbacks while consuming callback errors", OAuthFlowExpiryAndCallbackHandlingAsync),
    ("OAuth browser DTOs and failures redact credential material", OAuthRedactionAsync),
    ("link persistence is pending before link and retires on activation failure", PendingLinkOrderingAsync),
    ("library-only grants activate anonymously without linking", AnonymousActivationAsync),
    ("encrypted token store isolates two Jellyfin users", TokenStoreUserIsolationAsync),
    ("version 2 grants migrate to version 3 with linked scope", TokenStoreVersionMigrationAsync),
    ("per-user refresh is single-flight and persists rotation before lease publication", SingleFlightRefreshAsync),
    ("refresh preserves the grant's exact scope", RefreshScopePreservationAsync),
    ("failed rotated-token persistence quarantines the issued family without releasing a lease", FailedRefreshPersistenceAsync),
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

    var copiedPageConfiguration = ValidPluginConfiguration();
    copiedPageConfiguration.ScryerPublicBaseUrl = "https://scryer.example.test/system/users";
    var copiedPageResult = await client.DiscoverAsync(
        ScryerOAuthConfiguration.FromPluginConfiguration(copiedPageConfiguration).Value!,
        CancellationToken.None);
    Assert.True(copiedPageResult.IsSuccess, copiedPageResult.Failure?.Code.ToString());
    Assert.Equal("https://scryer.example.test/oauth/authorize", copiedPageResult.Value!.AuthorizationEndpoint.AbsoluteUri.TrimEnd('/'));

    var badHandler = new RecordingHandler(_ => JsonResponse(ValidMetadataJson(tokenEndpoint: "https://evil.example.test/oauth/token")));
    var incompatible = await new ScryerOAuthMetadataClient(badHandler).DiscoverAsync(configuration, CancellationToken.None);
    Assert.False(incompatible.IsSuccess);
    Assert.Equal(ScryerFailureCode.ScryerIncompatible, incompatible.Failure!.Code);
}

static Task WebInjectionPreservesBytesAsync()
{
    var prefix = new byte[] { 0xff, 0xfe, 0x80 };
    var shell = Encoding.ASCII.GetBytes("<HTML><body>Jellyfin</BODY>");
    var suffix = new byte[] { 0x81, 0x00 };
    var original = prefix.Concat(shell).Concat(suffix).ToArray();

    var result = HtmlScriptInjector.Inject(original);
    Assert.True(result.Injected);
    Assert.True(result.Content.AsSpan(0, prefix.Length).SequenceEqual(prefix));
    Assert.True(result.Content.AsSpan(result.Content.Length - suffix.Length).SequenceEqual(suffix));
    Assert.Contains("data-scryer-loader=\"153.9\"", Encoding.ASCII.GetString(result.Content));

    var secondPass = HtmlScriptInjector.Inject(result.Content);
    Assert.True(secondPass.AlreadyPresent);
    Assert.True(secondPass.Content.SequenceEqual(result.Content));
    return Task.CompletedTask;
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

    var libraryOnly = await new ScryerOAuthMetadataClient(new RecordingHandler(_ => JsonResponse(TokenJson("library"))))
        .ExchangeAuthorizationCodeAsync(metadata, configuration, "code", "verifier", CancellationToken.None);
    Assert.True(libraryOnly.IsSuccess, libraryOnly.Failure?.Code.ToString());
    Assert.Equal("library", libraryOnly.Value!.Scope);

    foreach (var scope in new[] { "jellyfin-link", "library jellyfin-link admin", "library library" })
    {
        var rejected = await new ScryerOAuthMetadataClient(new RecordingHandler(_ => JsonResponse(TokenJson(scope))))
            .ExchangeAuthorizationCodeAsync(metadata, configuration, "code", "verifier", CancellationToken.None);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(ScryerFailureCode.InvalidResponse, rejected.Failure!.Code);
    }
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

static async Task ControllerActorAndUnlinkedAsync()
{
    var service = new ScryerGraphqlService(new FixedConfigurationProvider(ValidOAuthConfiguration()), new RecordingSession(), new RecordingHandler(_ => throw new InvalidOperationException("Untrusted actor reached GraphQL.")));
    foreach (var actor in new[]
    {
        new ClaimsPrincipal(),
        Principal("01234567-89ab-cdef-0123-456789abcdef", "true"),
        new ClaimsPrincipal(new[]
        {
            Identity("01234567-89ab-cdef-0123-456789abcdef", "false"),
            Identity("11111111-1111-1111-1111-111111111111", "false"),
        }),
    })
    {
        var result = await Controller(new DiscoveryController(service), actor).GetTrending(CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    var unlinked = await Controller(new DiscoveryController(service), Principal("01234567-89ab-cdef-0123-456789abcdef", "false")).GetTrending(CancellationToken.None);
    var failure = Assert.IsType<ObjectResult>(unlinked.Result);
    Assert.Equal(401, failure.StatusCode);
    Assert.Equal("not_connected", Assert.IsType<ScryerFailureResponse>(failure.Value).Code);
}

static async Task ControllerInputBoundsAsync()
{
    var handler = new RecordingHandler(_ => throw new InvalidOperationException("Invalid input reached GraphQL."));
    var service = new ScryerGraphqlService(new FixedConfigurationProvider(ValidOAuthConfiguration()), new TokenSession(), handler);
    var actor = Principal("01234567-89ab-cdef-0123-456789abcdef", "false");

    var search = await Controller(new DiscoveryController(service), actor).Search(new string('x', 257), 51, CancellationToken.None);
    var calendarTooShort = await Controller(new CalendarController(service), actor).GetUpcoming(0, CancellationToken.None);
    var calendarTooLong = await Controller(new CalendarController(service), actor).GetUpcoming(63, CancellationToken.None);
    var downloadsTooEarly = await Controller(new DownloadsController(service), actor).GetQueue(-1, CancellationToken.None);
    var downloadsTooLate = await Controller(new DownloadsController(service), actor).GetHistory(10_001, CancellationToken.None);
    var request = await Controller(new RequestsController(service), actor).Create(new SubmitRequestDto
    {
        LibraryId = "library",
        Title = "Title",
        ExternalIds = [new ExternalIdDto { Source = "tmdb", Value = "1" }],
        Year = 1799,
    }, CancellationToken.None);

    Assert.Equal(400, Assert.IsType<ObjectResult>(search.Result).StatusCode);
    Assert.Equal(400, Assert.IsType<ObjectResult>(calendarTooShort.Result).StatusCode);
    Assert.Equal(400, Assert.IsType<ObjectResult>(calendarTooLong.Result).StatusCode);
    Assert.Equal(400, Assert.IsType<ObjectResult>(downloadsTooEarly.Result).StatusCode);
    Assert.Equal(400, Assert.IsType<ObjectResult>(downloadsTooLate.Result).StatusCode);
    Assert.Equal(400, Assert.IsType<ObjectResult>(request.Result).StatusCode);
    Assert.Equal(0, handler.Requests.Count);
}

static Task DisabledFeatureAsync()
{
    var context = new ActionExecutingContext(
        new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), new ModelStateDictionary()),
        [],
        new Dictionary<string, object?>(),
        new object());
    new ScryerFeatureAttribute(ScryerFeature.Discovery).OnActionExecuting(context);
    var rejected = Assert.IsType<NotFoundObjectResult>(context.Result);
    using var payload = JsonDocument.Parse(JsonSerializer.Serialize(rejected.Value));
    Assert.Equal("feature_disabled", payload.RootElement.GetProperty("code").GetString());
    return Task.CompletedTask;
}

static async Task GraphqlProtocolFailuresAsync()
{
    await AssertGraphqlFailureAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized), ScryerFailureCode.AuthorizationExpired);
    await AssertGraphqlFailureAsync(new HttpResponseMessage(HttpStatusCode.Forbidden), ScryerFailureCode.PermissionDenied);
    await AssertGraphqlFailureAsync(new HttpResponseMessage(HttpStatusCode.TooManyRequests), ScryerFailureCode.RateLimited);
    await AssertGraphqlFailureAsync(new HttpResponseMessage(HttpStatusCode.BadGateway), ScryerFailureCode.ScryerOffline);
    await AssertGraphqlFailureAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json", Encoding.UTF8, "application/json") }, ScryerFailureCode.InvalidResponse);
    await AssertGraphqlFailureAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>upstream error</html>", Encoding.UTF8, "text/html") }, ScryerFailureCode.InvalidResponse);
}

static async Task AssertGraphqlFailureAsync(HttpResponseMessage response, ScryerFailureCode expected)
{
    var service = new ScryerGraphqlService(new FixedConfigurationProvider(ValidOAuthConfiguration()), new TokenSession(), new RecordingHandler(_ => response));
    var result = await service.GetDiscoveryHomeCardsAsync("0123456789abcdef0123456789abcdef", CancellationToken.None);
    Assert.False(result.IsSuccess);
    Assert.Equal(expected, result.Failure!.Code);
}

static async Task CapabilityMappingAsync()
{
    var service = new ScryerGraphqlService(
        new FixedConfigurationProvider(ValidOAuthConfiguration()),
        new TokenSession(),
        new RecordingHandler(_ => JsonResponse("""{"data":{"me":{"id":"scryer-user","username":"member","appPermissions":["REQUEST","REQUEST"],"libraryPermissions":[{"libraryId":"library-b","permissions":["VIEW"]},{"libraryId":"library-a","permissions":["REQUEST","AUTO_APPROVE_REQUESTS","MANAGE_TITLES"]},{"libraryId":"library-a","permissions":["VIEW"]}]}}}""")));
    var result = await service.GetCapabilitySnapshotAsync("0123456789abcdef0123456789abcdef", CancellationToken.None);
    Assert.True(result.IsSuccess, result.Failure?.Code.ToString());
    Assert.True(result.Value!.AppPermissions.SequenceEqual(new[] { "REQUEST" }, StringComparer.Ordinal));
    Assert.Equal("library-a", result.Value.Libraries[0].LibraryId);
    Assert.True(result.Value.Libraries[0].CanView);
    Assert.True(result.Value.Libraries[0].CanRequest);
    Assert.True(result.Value.Libraries[0].CanAutoApproveRequests);
    Assert.True(result.Value.Libraries[0].CanManageTitles);
    Assert.Equal("library-b", result.Value.Libraries[1].LibraryId);
    Assert.True(result.Value.Libraries[1].CanView);
    Assert.False(result.Value.Libraries[1].CanRequest);
}

static async Task DetachedRevokeJournalAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var dataPath = Path.Combine(Path.GetTempPath(), "scryer-plugin-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new ScryerTokenStore(
            DataProtectionProvider.Create(new DirectoryInfo(dataPath), builder => builder.SetApplicationName("Scryer.Plugin.SelfTest")),
            ApplicationPathsProxy.Create(dataPath));
        var grant = PendingRevoke(jellyfinUserId, "detached-refresh");
        Assert.True(await store.QuarantineDetachedAsync(grant, CancellationToken.None));

        var grantsPath = Path.Combine(dataPath, "plugins", "scryer", "oauth-grants");
        Assert.Equal(1, Directory.GetFiles(grantsPath, "*.revoke.dat.next").Length);
        var restartedStore = new ScryerTokenStore(
            DataProtectionProvider.Create(new DirectoryInfo(dataPath), builder => builder.SetApplicationName("Scryer.Plugin.SelfTest")),
            ApplicationPathsProxy.Create(dataPath));
        var pendingUsers = await restartedStore.GetPendingUserIdsAsync(4, null, CancellationToken.None);
        Assert.Equal(jellyfinUserId, pendingUsers.Single());
        var discovered = await restartedStore.ReadDetachedQuarantinesAsync(jellyfinUserId, CancellationToken.None);
        Assert.Equal(1, discovered.Count);
        Assert.Equal("detached-refresh", discovered[0].RefreshToken);
        Assert.Equal(ScryerGrantLinkState.PendingRevoke, discovered[0].LinkState);
        Assert.Equal(0, Directory.GetFiles(grantsPath, "*.revoke.dat.next").Length);
        Assert.Equal(1, Directory.GetFiles(grantsPath, "*.revoke.dat").Length);

        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var cleanup = SessionService(ValidOAuthConfiguration(), restartedStore, handler);
        var discarded = await cleanup.DiscardPendingLinkAsync(jellyfinUserId, CancellationToken.None);
        Assert.True(discarded.IsSuccess, discarded.Failure?.Code.ToString());
        Assert.False(discarded.Value == true);
        Assert.Equal(1, handler.Requests.Count);
        Assert.Equal("/oauth/revoke", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(0, Directory.GetFiles(grantsPath, "*.revoke.dat*").Length);
    }
    finally
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}

static async Task DetachedRetirementPreservesCurrentAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var current = ActiveGrant(jellyfinUserId, configuration, "current-refresh");
    var store = new InMemoryTokenStore { Current = current };
    var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
    var service = SessionService(configuration, store, handler);

    var retired = await service.RetireIssuedRefreshTokenAsync(jellyfinUserId, configuration, "issued-refresh", CancellationToken.None);

    Assert.True(retired.IsSuccess, retired.Failure?.Code.ToString());
    Assert.Equal("current-refresh", store.Current!.RefreshToken);
    Assert.Equal(ScryerGrantLinkState.Active, store.Current.LinkState);
    Assert.Equal(0, store.DeleteCurrentCalls);
    Assert.Equal(1, store.QuarantineDetachedCalls);
    Assert.Equal(1, store.DeleteDetachedCalls);
    Assert.Equal(0, store.Detached.Count);
    Assert.Equal(1, handler.Requests.Count);
    Assert.Equal("/base/oauth/revoke", handler.Requests[0].RequestUri!.AbsolutePath);
    Assert.Contains("token=issued-refresh", handler.Requests[0].Content!);
}

static async Task DetachedDeletionFailureAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var store = new InMemoryTokenStore
    {
        Current = ActiveGrant(jellyfinUserId, configuration, "current-refresh"),
        DeleteDetachedResult = false,
    };
    var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
    var service = SessionService(configuration, store, handler);

    var retired = await service.RetireIssuedRefreshTokenAsync(jellyfinUserId, configuration, "issued-refresh", CancellationToken.None);

    Assert.False(retired.IsSuccess);
    Assert.Equal(ScryerFailureCode.AuthorizationExpired, retired.Failure!.Code);
    Assert.Equal("current-refresh", store.Current!.RefreshToken);
    Assert.Equal(0, store.DeleteCurrentCalls);
    Assert.Equal(1, store.DeleteDetachedCalls);
    Assert.Equal(1, store.Detached.Count);
    Assert.Equal("issued-refresh", store.Detached[0].RefreshToken);
    Assert.Equal(ScryerGrantLinkState.PendingRevoke, store.Detached[0].LinkState);
    Assert.Equal(1, handler.Requests.Count);
}

static async Task DisconnectDetachedReadFailureAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var store = new InMemoryTokenStore { Current = ActiveGrant(jellyfinUserId, configuration, "current-refresh") };
    var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
        ? JsonResponse(ValidMetadataJson())
        : JsonResponse(TokenJson("library jellyfin-link")));
    var service = SessionService(configuration, store, handler);

    var lease = await service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
    Assert.True(lease.IsSuccess, lease.Failure?.Code.ToString());
    Assert.Equal("refresh", store.Current!.RefreshToken);
    Assert.Equal(2, handler.Requests.Count);
    store.ResetOperationCounts();
    store.ThrowOnReadDetached = true;

    var disconnected = await service.DisconnectAsync(jellyfinUserId, CancellationToken.None);

    Assert.False(disconnected.IsSuccess);
    Assert.Equal(ScryerFailureCode.InternalError, disconnected.Failure!.Code);
    Assert.Equal(0, store.ReadCurrentCalls);
    Assert.Equal(0, store.DeleteCurrentCalls);
    Assert.Equal("refresh", store.Current!.RefreshToken);
    Assert.Equal(ScryerGrantLinkState.Active, store.Current.LinkState);
    Assert.Equal(2, handler.Requests.Count);

    var afterFailure = await service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
    Assert.False(afterFailure.IsSuccess);
    Assert.Equal(ScryerFailureCode.AuthorizationExpired, afterFailure.Failure!.Code);
    Assert.Equal(2, handler.Requests.Count);
}

static async Task DisconnectCorruptDetachedRecordAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var dataPath = Path.Combine(Path.GetTempPath(), "scryer-plugin-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var configuration = ValidOAuthConfiguration();
        var store = new ScryerTokenStore(DataProtectionProvider.Create(dataPath), ApplicationPathsProxy.Create(dataPath));
        Assert.True(await store.SaveAsync(ActiveGrant(jellyfinUserId, configuration, "current-refresh"), CancellationToken.None));
        var grantsPath = Path.Combine(dataPath, "plugins", "scryer", "oauth-grants");
        var corruptJournalPath = Path.Combine(grantsPath, "corrupt.revoke.dat.next");
        await File.WriteAllBytesAsync(corruptJournalPath, [0x01, 0x02, 0x03], CancellationToken.None);

        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Disconnect must not contact Scryer after a detached-store failure."));
        var service = SessionService(configuration, store, handler);
        var disconnected = await service.DisconnectAsync(jellyfinUserId, CancellationToken.None);

        Assert.False(disconnected.IsSuccess);
        Assert.Equal(ScryerFailureCode.InternalError, disconnected.Failure!.Code);
        Assert.Equal(0, handler.Requests.Count);
        var current = await store.ReadCurrentAsync(jellyfinUserId, CancellationToken.None);
        Assert.Equal(ScryerGrantReadState.Found, current.State);
        Assert.Equal("current-refresh", current.Grant!.RefreshToken);
        Assert.Equal(1, Directory.GetFiles(grantsPath, "*.revoke.dat").Length);

        var afterFailure = await service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
        Assert.False(afterFailure.IsSuccess);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, afterFailure.Failure!.Code);
        Assert.Equal(0, handler.Requests.Count);
    }
    finally
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}

static async Task OAuthFlowBindingAsync()
{
    var dataPath = Path.Combine(Path.GetTempPath(), "scryer-plugin-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var configuration = ValidOAuthConfiguration();
        var session = new RecordingSession();
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(ValidMetadataJson())
            : JsonResponse(TokenJson("library jellyfin-link")));
        var flow = new ScryerOAuthFlowService(
            new FixedConfigurationProvider(configuration),
            new ScryerOAuthMetadataClient(handler),
            session,
            new ScryerOAuthFlowStore(),
            DataProtectionProvider.Create(dataPath));

        var first = await flow.StartAsync("user-a", "#/scryer-calendar", CancellationToken.None);
        var second = await flow.StartAsync("user-b", null, CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        var firstState = QueryValue(first.Value!.AuthorizationUri, "state");
        var secondState = QueryValue(second.Value!.AuthorizationUri, "state");
        Assert.True(flow.TryGetCallbackCookie(firstState, out var callbackCookie));
        Assert.Equal(first.Value.CookieName, callbackCookie.Name);
        Assert.Equal("/", first.Value.CookiePath);
        Assert.Equal("/", callbackCookie.Path);

        var wrongBrowser = await flow.StageCallbackAsync(firstState, second.Value!.CookieValue, "first-code", null, CancellationToken.None);
        Assert.False(wrongBrowser.Success);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, wrongBrowser.Failure!.Code);
        Assert.True(flow.TryGetCallbackCookie(firstState, out _));
        var firstStage = await flow.StageCallbackAsync(firstState, first.Value.CookieValue, "first-code", null, CancellationToken.None);
        Assert.True(firstStage.Success, firstStage.Failure?.Code.ToString());
        Assert.Equal("/", firstStage.FinalizeCookiePath);

        var staged = await flow.StageCallbackAsync(secondState, second.Value.CookieValue, "second-code", null, CancellationToken.None);
        Assert.True(staged.Success, staged.Failure?.Code.ToString());
        var replayedCallback = await flow.StageCallbackAsync(secondState, second.Value.CookieValue, "second-code", null, CancellationToken.None);
        Assert.False(replayedCallback.Success);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, replayedCallback.Failure!.Code);
        var wrongUser = await flow.FinalizeAsync("user-other", staged.FinalizeCookieValue, CancellationToken.None);
        Assert.False(wrongUser.IsSuccess);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, wrongUser.Failure!.Code);
        Assert.Equal(0, session.ConnectCalls);
        var consumedByWrongUser = await flow.FinalizeAsync("user-b", staged.FinalizeCookieValue, CancellationToken.None);
        Assert.False(consumedByWrongUser.IsSuccess);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, consumedByWrongUser.Failure!.Code);

        var successful = await flow.StartAsync("user-c", "#/scryer-requests", CancellationToken.None);
        var successfulState = QueryValue(successful.Value!.AuthorizationUri, "state");
        var successfulStage = await flow.StageCallbackAsync(successfulState, successful.Value.CookieValue, "third-code", null, CancellationToken.None);
        Assert.True(successfulStage.Success, successfulStage.Failure?.Code.ToString());
        var finalized = await flow.FinalizeAsync("user-c", successfulStage.FinalizeCookieValue, CancellationToken.None);
        Assert.True(finalized.IsSuccess, finalized.Failure?.Code.ToString());
        Assert.Equal(1, session.ConnectCalls);
        Assert.Equal("user-c", session.ConnectedUsers.Single());
        var replayedFinalize = await flow.FinalizeAsync("user-c", successfulStage.FinalizeCookieValue, CancellationToken.None);
        Assert.False(replayedFinalize.IsSuccess);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, replayedFinalize.Failure!.Code);
        Assert.Equal(1, session.ConnectCalls);
    }
    finally
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}

static async Task OAuthFlowExpiryAndCallbackHandlingAsync()
{
    var dataPath = Path.Combine(Path.GetTempPath(), "scryer-plugin-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var configuration = ValidOAuthConfiguration();
        var session = new RecordingSession();
        var flow = new ScryerOAuthFlowService(
            new FixedConfigurationProvider(configuration),
            new ScryerOAuthMetadataClient(new RecordingHandler(request => request.Method == HttpMethod.Get
                ? JsonResponse(ValidMetadataJson())
                : JsonResponse(TokenJson("library jellyfin-link")))),
            session,
            new ScryerOAuthFlowStore(clock),
            DataProtectionProvider.Create(dataPath));

        var expired = await flow.StartAsync("expired-user", null, CancellationToken.None);
        Assert.True(expired.IsSuccess);
        var expiredState = QueryValue(expired.Value!.AuthorizationUri, "state");
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(flow.TryGetCallbackCookie(expiredState, out _));
        var expiredStage = await flow.StageCallbackAsync(expiredState, expired.Value.CookieValue, "oauth-code", null, CancellationToken.None);
        Assert.False(expiredStage.Success);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, expiredStage.Failure!.Code);

        var missing = await flow.StartAsync("missing-user", null, CancellationToken.None);
        Assert.True(missing.IsSuccess);
        var missingState = QueryValue(missing.Value!.AuthorizationUri, "state");
        var missingStage = await flow.StageCallbackAsync(missingState, missing.Value.CookieValue, null, null, CancellationToken.None);
        Assert.False(missingStage.Success);
        Assert.False(flow.TryGetCallbackCookie(missingState, out _));

        var contradictory = await flow.StartAsync("contradictory-user", null, CancellationToken.None);
        Assert.True(contradictory.IsSuccess);
        var contradictoryState = QueryValue(contradictory.Value!.AuthorizationUri, "state");
        var contradictoryStage = await flow.StageCallbackAsync(contradictoryState, contradictory.Value.CookieValue, "oauth-code", "access_denied", CancellationToken.None);
        Assert.False(contradictoryStage.Success);
        Assert.False(flow.TryGetCallbackCookie(contradictoryState, out _));

        var oversizedCode = await flow.StartAsync("oversized-code-user", null, CancellationToken.None);
        Assert.True(oversizedCode.IsSuccess);
        var oversizedCodeState = QueryValue(oversizedCode.Value!.AuthorizationUri, "state");
        var oversizedCodeStage = await flow.StageCallbackAsync(oversizedCodeState, oversizedCode.Value.CookieValue, new string('c', 2049), null, CancellationToken.None);
        Assert.False(oversizedCodeStage.Success);
        Assert.False(flow.TryGetCallbackCookie(oversizedCodeState, out _));

        var oversizedError = await flow.StartAsync("oversized-error-user", null, CancellationToken.None);
        Assert.True(oversizedError.IsSuccess);
        var oversizedErrorState = QueryValue(oversizedError.Value!.AuthorizationUri, "state");
        var oversizedErrorStage = await flow.StageCallbackAsync(oversizedErrorState, oversizedError.Value.CookieValue, null, new string('e', 129), CancellationToken.None);
        Assert.False(oversizedErrorStage.Success);
        Assert.False(flow.TryGetCallbackCookie(oversizedErrorState, out _));

        var protectedTarget = await flow.StartAsync("protected-target-user", null, CancellationToken.None);
        var wrongCookie = await flow.StartAsync("wrong-cookie-user", null, CancellationToken.None);
        Assert.True(protectedTarget.IsSuccess);
        Assert.True(wrongCookie.IsSuccess);
        var protectedTargetState = QueryValue(protectedTarget.Value!.AuthorizationUri, "state");
        var wrongCookieStage = await flow.StageCallbackAsync(protectedTargetState, wrongCookie.Value!.CookieValue, null, null, CancellationToken.None);
        Assert.False(wrongCookieStage.Success);
        Assert.True(flow.TryGetCallbackCookie(protectedTargetState, out _));

        var callbackError = await flow.StartAsync("error-user", null, CancellationToken.None);
        Assert.True(callbackError.IsSuccess);
        var callbackErrorState = QueryValue(callbackError.Value!.AuthorizationUri, "state");
        var errorStage = await flow.StageCallbackAsync(callbackErrorState, callbackError.Value.CookieValue, null, "access_denied", CancellationToken.None);
        Assert.True(errorStage.Success, errorStage.Failure?.Code.ToString());
        var errorFinalize = await flow.FinalizeAsync("error-user", errorStage.FinalizeCookieValue, CancellationToken.None);
        Assert.False(errorFinalize.IsSuccess);
        Assert.Equal(ScryerFailureCode.NotConnected, errorFinalize.Failure!.Code);
        var replay = await flow.FinalizeAsync("error-user", errorStage.FinalizeCookieValue, CancellationToken.None);
        Assert.False(replay.IsSuccess);
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, replay.Failure!.Code);
        Assert.Equal(0, session.ConnectCalls);
    }
    finally
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}

static Task OAuthRedactionAsync()
{
    const string accessToken = "access-token-secret";
    const string refreshToken = "refresh-token-secret";
    const string authorizationCode = "authorization-code-secret";
    const string verifier = "pkce-verifier-secret";
    const string authorizationHeader = "Bearer authorization-header-secret";
    var secretFailure = new ScryerFailure(
        ScryerFailureCode.InternalError,
        string.Join(" ", accessToken, refreshToken, authorizationCode, verifier, authorizationHeader));
    var values = new[]
    {
        JsonSerializer.Serialize(new ScryerOAuthTokenSet(accessToken, refreshToken, DateTimeOffset.UtcNow.AddMinutes(5), "library jellyfin-link")),
        JsonSerializer.Serialize(new ScryerPkcePair(verifier, "public-challenge")),
        JsonSerializer.Serialize(ScryerOAuthCallbackStageResult.Failed(secretFailure)),
        JsonSerializer.Serialize(ScryerOAuthCallbackResult.Failed(secretFailure)),
        JsonSerializer.Serialize(secretFailure),
        JsonSerializer.Serialize(ScryerAuthStatusDto.Failed(secretFailure)),
        JsonSerializer.Serialize(ScryerFailureResponse.From(secretFailure)),
        secretFailure.ToString(),
        new ScryerOAuthTokenSet(accessToken, refreshToken, DateTimeOffset.UtcNow.AddMinutes(5), "library jellyfin-link").ToString(),
        new ScryerPkcePair(verifier, "public-challenge").ToString(),
        new ScryerOAuthCallbackStageResult(true, "#/scryer-discovery", null, "finalize-cookie", authorizationCode, "/", true, DateTimeOffset.UtcNow.AddMinutes(1), new Uri("https://jellyfin.example.test/web/index.html")).ToString(),
    };
    foreach (var value in values)
    {
        Assert.False(value.Contains(accessToken, StringComparison.Ordinal));
        Assert.False(value.Contains(refreshToken, StringComparison.Ordinal));
        Assert.False(value.Contains(authorizationCode, StringComparison.Ordinal));
        Assert.False(value.Contains(verifier, StringComparison.Ordinal));
        Assert.False(value.Contains(authorizationHeader, StringComparison.Ordinal));
    }

    return Task.CompletedTask;
}

static async Task PendingLinkOrderingAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var store = new InMemoryTokenStore { PromotePendingResult = false };
    var events = store.Events;
    var handler = new RecordingHandler(request =>
    {
        events.Add(request.RequestUri!.AbsolutePath == "/base/oauth/revoke" ? "revoke" : "unexpected-request");
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    });
    var link = new PendingObservingLinkService(store, events);
    var service = new ScryerUserSessionService(new FixedConfigurationProvider(configuration), new ScryerOAuthMetadataClient(handler), store, link);
    var tokenSet = new ScryerOAuthTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5), "library jellyfin-link");

    var connected = await service.ConnectAsync(jellyfinUserId, configuration, tokenSet, CancellationToken.None);

    Assert.False(connected.IsSuccess);
    Assert.Equal(ScryerFailureCode.AuthorizationExpired, connected.Failure!.Code);
    Assert.True(link.SawPendingLink);
    Assert.Equal("save:PendingLink", events[0]);
    Assert.Equal("link", events[1]);
    Assert.Equal("promote", events[2]);
    Assert.Equal("quarantine:PendingRevoke", events[3]);
    Assert.Equal("revoke", events[4]);
    Assert.Equal("delete-current", events[5]);
    Assert.Equal(0, store.ActiveSaveCalls);
    Assert.True(store.Current is null);
}

static async Task AnonymousActivationAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var store = new InMemoryTokenStore();
    var link = new CountingLinkService();
    var service = new ScryerUserSessionService(
        new FixedConfigurationProvider(configuration),
        new ScryerOAuthMetadataClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))),
        store,
        link);
    var tokenSet = new ScryerOAuthTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5), "library");

    var connected = await service.ConnectAsync(jellyfinUserId, configuration, tokenSet, CancellationToken.None);
    var status = await service.GetGrantStatusAsync(jellyfinUserId, CancellationToken.None);

    Assert.True(connected.IsSuccess, connected.Failure?.Code.ToString());
    Assert.Equal(0, link.Calls);
    Assert.Equal(ScryerGrantLinkState.Active, store.Current!.LinkState);
    Assert.Equal(ScryerOAuthScopes.Library, store.Current.GrantedScope);
    Assert.True(status.IsSuccess, status.Failure?.Code.ToString());
    Assert.True(status.Value!.Connected);
    Assert.False(status.Value.AccountLinked);
}

static async Task TokenStoreUserIsolationAsync()
{
    const string firstUserId = "0123456789abcdef0123456789abcdef";
    const string secondUserId = "11111111111111111111111111111111";
    var dataPath = Path.Combine(Path.GetTempPath(), "scryer-plugin-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var configuration = ValidOAuthConfiguration();
        var store = new ScryerTokenStore(DataProtectionProvider.Create(dataPath), ApplicationPathsProxy.Create(dataPath));
        var first = ActiveGrant(firstUserId, configuration, "first-refresh");
        var second = ActiveGrant(secondUserId, configuration, "second-refresh", ScryerOAuthScopes.Library);
        Assert.True(await store.SaveAsync(first, CancellationToken.None));
        Assert.True(await store.SaveAsync(second, CancellationToken.None));
        var firstRead = await store.ReadCurrentAsync(firstUserId, CancellationToken.None);
        var secondRead = await store.ReadCurrentAsync(secondUserId, CancellationToken.None);
        Assert.Equal("first-refresh", firstRead.Grant!.RefreshToken);
        Assert.Equal("second-refresh", secondRead.Grant!.RefreshToken);
        Assert.Equal(ScryerOAuthScopes.Linked, firstRead.Grant.GrantedScope);
        Assert.Equal(ScryerOAuthScopes.Library, secondRead.Grant.GrantedScope);
        var wrongBinding = await store.ReadAsync(new ScryerGrantKey(firstUserId, first.Key.Authority, "other-client"), CancellationToken.None);
        Assert.Equal(ScryerGrantReadState.Missing, wrongBinding.State);
        Assert.True(await store.DeleteCurrentAsync(firstUserId, CancellationToken.None));
        Assert.Equal(ScryerGrantReadState.Missing, (await store.ReadCurrentAsync(firstUserId, CancellationToken.None)).State);
        Assert.Equal("second-refresh", (await store.ReadCurrentAsync(secondUserId, CancellationToken.None)).Grant!.RefreshToken);
    }
    finally
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}

static async Task TokenStoreVersionMigrationAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var dataPath = Path.Combine(Path.GetTempPath(), "scryer-plugin-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var configuration = ValidOAuthConfiguration();
        var provider = DataProtectionProvider.Create(dataPath);
        var protector = provider.CreateProtector("Jellyfin.Plugin.Scryer", "OAuthRefreshGrant", "v1");
        var directory = Path.Combine(dataPath, "plugins", "scryer", "oauth-grants");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jellyfinUserId))).ToLowerInvariant() + ".dat");
        var versionTwo = JsonSerializer.Serialize(new
        {
            Version = 2,
            JellyfinUserId = jellyfinUserId,
            Authority = configuration.InternalAuthority.AbsoluteUri.TrimEnd('/'),
            ClientId = configuration.ClientId,
            RefreshToken = "version-two-refresh",
            UpdatedAt = DateTimeOffset.UtcNow,
            LinkState = ScryerGrantLinkState.Active.ToString(),
            LinkIdempotencyKey = (string?)null,
            LinkAttempts = 0,
        });
        await File.WriteAllBytesAsync(path, protector.Protect(Encoding.UTF8.GetBytes(versionTwo)));

        var store = new ScryerTokenStore(provider, ApplicationPathsProxy.Create(dataPath));
        var read = await store.ReadCurrentAsync(jellyfinUserId, CancellationToken.None);
        Assert.Equal(ScryerGrantReadState.Found, read.State);
        Assert.Equal(ScryerOAuthScopes.Linked, read.Grant!.GrantedScope);
        Assert.True(await store.SaveAsync(read.Grant, CancellationToken.None));

        using var migrated = JsonDocument.Parse(Encoding.UTF8.GetString(protector.Unprotect(await File.ReadAllBytesAsync(path))));
        Assert.Equal(3, migrated.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(ScryerOAuthScopes.Linked, migrated.RootElement.GetProperty("GrantedScope").GetString());
    }
    finally
    {
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}

static async Task SingleFlightRefreshAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new InMemoryTokenStore
    {
        Current = ActiveGrant(jellyfinUserId, configuration, "current-refresh"),
        SaveStarted = saveStarted,
        AllowSave = allowSave,
    };
    var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
        ? JsonResponse(ValidMetadataJson())
        : JsonResponse(TokenJson("library jellyfin-link")));
    var service = SessionService(configuration, store, handler);

    var first = service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
    await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var second = service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
    Assert.False(first.IsCompleted);
    Assert.False(second.IsCompleted);
    Assert.Equal("current-refresh", store.Current!.RefreshToken);
    Assert.Equal(2, handler.Requests.Count);
    allowSave.SetResult(true);

    var results = await Task.WhenAll(first, second);
    Assert.True(results.All(result => result.IsSuccess));
    Assert.True(results.All(result => result.Value!.AccessToken == "access"));
    Assert.Equal(1, store.SaveCalls);
    Assert.Equal(1, store.ActiveSaveCalls);
    Assert.Equal("refresh", store.Current!.RefreshToken);
    Assert.Equal(2, handler.Requests.Count);
}

static async Task RefreshScopePreservationAsync()
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var changedStore = new InMemoryTokenStore
    {
        Current = ActiveGrant(jellyfinUserId, configuration, "current-refresh", ScryerOAuthScopes.Library),
    };
    var changedHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
        ? JsonResponse(ValidMetadataJson())
        : request.RequestUri!.AbsolutePath == "/base/oauth/token"
            ? JsonResponse(TokenJson(ScryerOAuthScopes.Linked))
            : new HttpResponseMessage(HttpStatusCode.NoContent));
    var changed = await SessionService(configuration, changedStore, changedHandler)
        .GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);

    Assert.False(changed.IsSuccess);
    Assert.Equal(ScryerFailureCode.AuthorizationExpired, changed.Failure!.Code);
    Assert.True(changedStore.Current is null);

    var exactStore = new InMemoryTokenStore
    {
        Current = ActiveGrant(jellyfinUserId, configuration, "current-refresh", ScryerOAuthScopes.Library),
    };
    var exactHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
        ? JsonResponse(ValidMetadataJson())
        : JsonResponse(TokenJson(ScryerOAuthScopes.Library)));
    var exact = await SessionService(configuration, exactStore, exactHandler)
        .GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);

    Assert.True(exact.IsSuccess, exact.Failure?.Code.ToString());
    Assert.Equal(ScryerOAuthScopes.Library, exactStore.Current!.GrantedScope);
}

static async Task FailedRefreshPersistenceAsync()
{
    await AssertFailedRefreshPersistenceAsync(saveResult: false, saveException: null, quarantineResult: true);
    await AssertFailedRefreshPersistenceAsync(saveResult: true, saveException: new IOException("test persistence failure"), quarantineResult: true);
    await AssertFailedRefreshPersistenceAsync(saveResult: false, saveException: null, quarantineResult: false);
}

static async Task AssertFailedRefreshPersistenceAsync(bool saveResult, Exception? saveException, bool quarantineResult)
{
    const string jellyfinUserId = "0123456789abcdef0123456789abcdef";
    var configuration = ValidOAuthConfiguration();
    var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new InMemoryTokenStore
    {
        Current = ActiveGrant(jellyfinUserId, configuration, "current-refresh"),
        SaveResult = saveResult,
        SaveException = saveException,
        QuarantineResult = quarantineResult,
        SaveStarted = saveStarted,
        AllowSave = allowSave,
    };
    var sawCurrentTombstoneAtRevoke = false;
    var handler = new RecordingHandler(request =>
    {
        if (request.Method == HttpMethod.Get) return JsonResponse(ValidMetadataJson());
        if (request.RequestUri!.AbsolutePath == "/base/oauth/token") return JsonResponse(TokenJson("library jellyfin-link"));
        sawCurrentTombstoneAtRevoke = store.Current?.RefreshToken == "refresh" && store.Current.LinkState == ScryerGrantLinkState.PendingRevoke;
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    });
    var service = SessionService(configuration, store, handler);

    var first = service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
    await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var second = service.GetAccessTokenAsync(jellyfinUserId, CancellationToken.None);
    Assert.False(first.IsCompleted);
    Assert.False(second.IsCompleted);
    allowSave.SetResult(true);

    var results = await Task.WhenAll(first, second);
    Assert.True(results.All(result => !result.IsSuccess));
    Assert.Equal(1, store.SaveCalls);
    Assert.Equal(1, store.ActiveSaveCalls);
    Assert.Equal(0, store.QuarantineDetachedCalls);
    Assert.Equal(0, store.DeleteDetachedCalls);
    if (quarantineResult)
    {
        Assert.Equal(ScryerFailureCode.AuthorizationExpired, results[0].Failure!.Code);
        Assert.Equal(ScryerFailureCode.NotConnected, results[1].Failure!.Code);
        Assert.True(sawCurrentTombstoneAtRevoke);
        Assert.Equal(1, store.DeleteCurrentCalls);
        Assert.True(store.Current is null);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/base/oauth/revoke", handler.Requests.Last().RequestUri!.AbsolutePath);
    }
    else
    {
        Assert.True(results.All(result => result.Failure!.Code == ScryerFailureCode.AuthorizationExpired));
        Assert.False(sawCurrentTombstoneAtRevoke);
        Assert.Equal(0, store.DeleteCurrentCalls);
        Assert.Equal("current-refresh", store.Current!.RefreshToken);
        Assert.Equal(ScryerGrantLinkState.Active, store.Current.LinkState);
        Assert.Equal(2, handler.Requests.Count);
    }
}

static ScryerRefreshGrant ActiveGrant(
    string jellyfinUserId,
    ScryerOAuthConfiguration configuration,
    string refreshToken,
    string grantedScope = ScryerOAuthScopes.Linked) =>
    new(
        ScryerGrantKey.Create(jellyfinUserId, configuration),
        refreshToken,
        DateTimeOffset.UtcNow,
        ScryerGrantLinkState.Active,
        grantedScope: grantedScope);

static ScryerRefreshGrant PendingRevoke(string jellyfinUserId, string refreshToken) =>
    new(new ScryerGrantKey(jellyfinUserId, "https://scryer.example.test", "jellyfin-plugin"), refreshToken, DateTimeOffset.UtcNow, ScryerGrantLinkState.PendingRevoke);

static ScryerUserSessionService SessionService(ScryerOAuthConfiguration configuration, IScryerTokenStore store, HttpMessageHandler handler) =>
    new(new FixedConfigurationProvider(configuration), new ScryerOAuthMetadataClient(handler), store, new NeverLinkService());

static string QueryValue(Uri uri, string key) => uri.Query.TrimStart('?').Split('&')
    .Select(value => value.Split('=', 2))
    .Where(parts => parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.Ordinal))
    .Select(parts => Uri.UnescapeDataString(parts[1]))
    .Single();

static T Controller<T>(T controller, ClaimsPrincipal user) where T : ControllerBase
{
    controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
    return controller;
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

sealed class FixedConfigurationProvider(ScryerOAuthConfiguration configuration) : IScryerOAuthConfigurationProvider
{
    public ScryerResult<ScryerOAuthConfiguration> GetConfiguration() => ScryerResult<ScryerOAuthConfiguration>.Success(configuration);
}

sealed class NeverLinkService : IScryerJellyfinLinkService
{
    public Task<ScryerResult<bool>> LinkAsync(ScryerOAuthConfiguration configuration, string jellyfinUserId, ScryerAccessTokenLease lease, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Fail(ScryerFailure.Internal));
}

sealed class CountingLinkService : IScryerJellyfinLinkService
{
    public int Calls { get; private set; }

    public Task<ScryerResult<bool>> LinkAsync(ScryerOAuthConfiguration configuration, string jellyfinUserId, ScryerAccessTokenLease lease, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(ScryerResult<bool>.Success(true));
    }
}

sealed class PendingObservingLinkService(InMemoryTokenStore store, List<string> events) : IScryerJellyfinLinkService
{
    public bool SawPendingLink { get; private set; }

    public Task<ScryerResult<bool>> LinkAsync(ScryerOAuthConfiguration configuration, string jellyfinUserId, ScryerAccessTokenLease lease, CancellationToken cancellationToken)
    {
        events.Add("link");
        SawPendingLink = store.Current?.Key.JellyfinUserId == jellyfinUserId && store.Current.LinkState == ScryerGrantLinkState.PendingLink;
        return Task.FromResult(ScryerResult<bool>.Success(true));
    }
}

sealed class RecordingSession : IScryerUserSessionService
{
    public int ConnectCalls { get; private set; }
    public List<string> ConnectedUsers { get; } = [];

    public Task<ScryerResult<ScryerAccessTokenLease>> GetAccessTokenAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<ScryerAccessTokenLease>.Fail(ScryerFailure.NotConnected));

    public Task<ScryerResult<bool>> ConnectAsync(string jellyfinUserId, ScryerOAuthConfiguration expectedConfiguration, ScryerOAuthTokenSet tokenSet, CancellationToken cancellationToken)
    {
        ConnectCalls++;
        ConnectedUsers.Add(jellyfinUserId);
        return Task.FromResult(ScryerResult<bool>.Success(true));
    }

    public Task<ScryerResult<bool>> RetireIssuedRefreshTokenAsync(string jellyfinUserId, ScryerOAuthConfiguration configuration, string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));

    public Task<ScryerResult<bool>> DisconnectAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));

    public Task<ScryerResult<bool>> HasGrantAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(false));

    public Task<ScryerResult<ScryerGrantStatus>> GetGrantStatusAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<ScryerGrantStatus>.Success(new ScryerGrantStatus(false, false)));

    public Task<ScryerResult<bool>> DiscardPendingLinkAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));
}

sealed class TokenSession : IScryerUserSessionService
{
    public Task<ScryerResult<ScryerAccessTokenLease>> GetAccessTokenAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<ScryerAccessTokenLease>.Success(new ScryerAccessTokenLease("access-token", DateTimeOffset.UtcNow.AddMinutes(5))));

    public Task<ScryerResult<bool>> ConnectAsync(string jellyfinUserId, ScryerOAuthConfiguration expectedConfiguration, ScryerOAuthTokenSet tokenSet, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));

    public Task<ScryerResult<bool>> RetireIssuedRefreshTokenAsync(string jellyfinUserId, ScryerOAuthConfiguration configuration, string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));

    public Task<ScryerResult<bool>> DisconnectAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));

    public Task<ScryerResult<bool>> HasGrantAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));

    public Task<ScryerResult<ScryerGrantStatus>> GetGrantStatusAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<ScryerGrantStatus>.Success(new ScryerGrantStatus(true, true)));

    public Task<ScryerResult<bool>> DiscardPendingLinkAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ScryerResult<bool>.Success(true));
}

sealed class InMemoryTokenStore : IScryerTokenStore
{
    public ScryerRefreshGrant? Current { get; set; }
    public List<ScryerRefreshGrant> Detached { get; } = [];
    public List<string> Events { get; } = [];
    public bool DeleteDetachedResult { get; set; } = true;
    public bool PromotePendingResult { get; set; } = true;
    public bool SaveResult { get; set; } = true;
    public bool QuarantineResult { get; set; } = true;
    public bool ThrowOnReadDetached { get; set; }
    public Exception? SaveException { get; set; }
    public TaskCompletionSource<bool>? SaveStarted { get; set; }
    public TaskCompletionSource<bool>? AllowSave { get; set; }
    public int ReadCurrentCalls { get; private set; }
    public int DeleteCurrentCalls { get; private set; }
    public int QuarantineDetachedCalls { get; private set; }
    public int DeleteDetachedCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public int ActiveSaveCalls { get; private set; }

    public Task<ScryerGrantReadResult> ReadAsync(ScryerGrantKey key, CancellationToken cancellationToken) =>
        Task.FromResult(Current is not null && SameBinding(Current.Key, key)
            ? new ScryerGrantReadResult(ScryerGrantReadState.Found, Current)
            : ScryerGrantReadResult.Missing);

    public Task<ScryerGrantReadResult> ReadCurrentAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        ReadCurrentCalls++;
        return Task.FromResult(Current is not null && Current.Key.JellyfinUserId == jellyfinUserId
            ? new ScryerGrantReadResult(ScryerGrantReadState.Found, Current)
            : ScryerGrantReadResult.Missing);
    }

    public async Task<bool> SaveAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        SaveCalls++;
        if (grant.LinkState == ScryerGrantLinkState.Active) ActiveSaveCalls++;
        Events.Add("save:" + grant.LinkState);
        SaveStarted?.TrySetResult(true);
        if (AllowSave is not null) await AllowSave.Task.ConfigureAwait(false);
        if (SaveException is not null) throw SaveException;
        if (!SaveResult) return false;
        Current = grant;
        return true;
    }

    public Task<bool> QuarantineAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        Events.Add("quarantine:" + grant.LinkState);
        if (!QuarantineResult) return Task.FromResult(false);
        Current = grant;
        return Task.FromResult(true);
    }

    public Task<bool> QuarantineDetachedAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        QuarantineDetachedCalls++;
        Detached.RemoveAll(existing => SameBinding(existing.Key, grant.Key) && existing.RefreshToken == grant.RefreshToken);
        Detached.Add(grant);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ScryerRefreshGrant>> ReadDetachedQuarantinesAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        if (ThrowOnReadDetached) throw new IOException("test detached read failure");
        return Task.FromResult<IReadOnlyList<ScryerRefreshGrant>>(Detached.Where(grant => grant.Key.JellyfinUserId == jellyfinUserId).ToArray());
    }

    public Task<bool> DeleteDetachedQuarantineAsync(ScryerRefreshGrant grant, CancellationToken cancellationToken)
    {
        DeleteDetachedCalls++;
        if (!DeleteDetachedResult) return Task.FromResult(false);
        Detached.RemoveAll(existing => SameBinding(existing.Key, grant.Key) && existing.RefreshToken == grant.RefreshToken);
        return Task.FromResult(true);
    }

    public Task<bool> PromotePendingAsync(ScryerRefreshGrant pendingGrant, CancellationToken cancellationToken)
    {
        Events.Add("promote");
        if (!PromotePendingResult) return Task.FromResult(false);
        Current = new ScryerRefreshGrant(
            pendingGrant.Key,
            pendingGrant.RefreshToken,
            DateTimeOffset.UtcNow,
            ScryerGrantLinkState.Active,
            grantedScope: pendingGrant.GrantedScope);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> GetPendingUserIdsAsync(int maximumCount, string? afterUserId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<ScryerLinkedGrantCount> GetActiveLinkedGrantCountAsync(int maximumEntries, CancellationToken cancellationToken) =>
        Task.FromResult(new ScryerLinkedGrantCount(0, false));

    public Task<bool> DeleteAsync(ScryerGrantKey key, CancellationToken cancellationToken)
    {
        if (Current is not null && SameBinding(Current.Key, key)) Current = null;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteCurrentAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        DeleteCurrentCalls++;
        Events.Add("delete-current");
        if (Current is not null && Current.Key.JellyfinUserId == jellyfinUserId) Current = null;
        return Task.FromResult(true);
    }

    public void ResetOperationCounts()
    {
        ReadCurrentCalls = 0;
        DeleteCurrentCalls = 0;
        QuarantineDetachedCalls = 0;
        DeleteDetachedCalls = 0;
    }

    private static bool SameBinding(ScryerGrantKey left, ScryerGrantKey right) =>
        left.JellyfinUserId == right.JellyfinUserId && left.Authority == right.Authority && left.ClientId == right.ClientId;
}

class ApplicationPathsProxy : DispatchProxy
{
    public string DataPath { get; private set; } = string.Empty;

    public static IApplicationPaths Create(string dataPath)
    {
        var paths = DispatchProxy.Create<IApplicationPaths, ApplicationPathsProxy>();
        ((ApplicationPathsProxy)(object)paths).DataPath = dataPath;
        return paths;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "get_DataPath") return DataPath;
        return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}

sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now = _now.Add(amount);
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
