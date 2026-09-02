using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.OAuth;

namespace Jellyfin.Plugin.Scryer.Services;

/// <summary>
/// Fixed-operation, per-user GraphQL boundary. Browser callers select plugin endpoints, never
/// GraphQL documents. Returned JsonElements are detached clones of narrow selected payloads.
/// </summary>
public interface IScryerGraphqlService
{
    Task<ScryerResult<ScryerCapabilitySnapshot>> GetCapabilitySnapshotAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetRequestLibrariesAsync(string jellyfinUserId, string? facet, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetManageableLibrariesAsync(string jellyfinUserId, string? facet, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetQualityProfilesAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetDiscoveryHomeCardsAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetTitleRecommendationsAsync(string jellyfinUserId, string source, string value, int limit, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> SearchMetadataMultiAsync(string jellyfinUserId, string query, int limit, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetDiscoveryItemDetailAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetMyMediaRequestsAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetManageableMediaRequestsAsync(string jellyfinUserId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> SubmitMediaRequestAsync(string jellyfinUserId, JsonElement input, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> AddTitleAsync(string jellyfinUserId, JsonElement input, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> ApproveMediaRequestAsync(string jellyfinUserId, string requestId, string qualityProfileId, string? monitorType, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> DismissMediaRequestAsync(string jellyfinUserId, string requestId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> UpdateMyMediaRequestAsync(string jellyfinUserId, JsonElement input, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> CancelMyMediaRequestAsync(string jellyfinUserId, string requestId, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetCalendarEpisodesAsync(string jellyfinUserId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken);
    Task<ScryerResult<IReadOnlyDictionary<string, string?>>> GetTitlePostersAsync(string jellyfinUserId, IReadOnlyList<string> titleIds, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetDownloadQueuePageAsync(string jellyfinUserId, int offset, CancellationToken cancellationToken);
    Task<ScryerResult<JsonElement>> GetDownloadHistoryPageAsync(string jellyfinUserId, int offset, CancellationToken cancellationToken);
    Task<ScryerResult<IReadOnlyList<ScryerTvDiscoveryRail>>> GetAndroidTvDiscoveryAsync(string jellyfinUserId, IReadOnlyList<ScryerRecommendationSeed> recentSeeds, CancellationToken cancellationToken);
    Task<ScryerResult<ScryerTvActionResult>> ResolveDefaultTvActionAndExecuteAsync(string jellyfinUserId, string targetKey, string targetKind, CancellationToken cancellationToken);
}

public sealed class ScryerGraphqlService : IScryerGraphqlService
{
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumResponseBytes = 512 * 1024;
    private const int MaximumAppPermissions = 16;
    private const int MaximumLibraryGrants = 128;
    private const int MaximumIdentifierLength = 256;
    private const int MaximumSearchLength = 256;
    private const int MaximumPageOffset = 10_000;
    private const int MaximumCalendarRangeDays = 62;
    private const int TitlePosterBatchSize = 16;
    private const int MaximumCalendarTitles = 64;
    private static readonly HashSet<string> RecommendationExternalIdSources = new(StringComparer.Ordinal)
    {
        "imdb", "tmdb", "tmdb_movie", "tmdb_series", "tmdb_show", "tmdb_tv",
        "tvdb", "tvdb_movie", "tvdb_series", "tvdb_show",
    };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private const string CapabilityBootstrapQuery = """query ScryerCapabilityBootstrap { me { id username appPermissions libraryPermissions { libraryId permissions } } }""";
    private const string RequestLibrariesQuery = """query ScryerRequestLibraries($facet: MediaFacetValue) { libraries(facet: $facet, permission: REQUEST) { id facet name slug isDefault requestQualityProfileIds requestQualityProfileDefaultId roots { id path isDefault } } }""";
    private const string ManageableLibrariesQuery = """query ScryerManageableLibraries($facet: MediaFacetValue) { libraries(facet: $facet, permission: MANAGE_TITLES) { id facet name slug isDefault qualityProfileId roots { id path isDefault } } }""";
    private const string QualityProfilesQuery = """query ScryerQualityProfiles { qualityProfileSettings { profiles { id name } } }""";
    private const string DiscoveryHomeCardsQuery = """query ScryerDiscoveryHomeCards { discoveryHomeCards { canViewPersonalized heroItem { id targetKey targetKind displayTitle year posterUrl } publicSections { sectionId title items { id targetKey targetKind displayTitle year posterUrl } } personalizedSections { sectionId title items { id targetKey targetKind displayTitle year posterUrl } } } }""";
    private const string TitleRecommendationsQuery = """query ScryerTitleRecommendations($source: String!, $values: [String!]!, $limit: Int!) { titlesByExternalIds(source: $source, values: $values) { id name moreLikeThis(limit: $limit) { id targetKey targetKind displayTitle year posterUrl } } }""";
    private const string SearchMetadataMultiQuery = """query ScryerSearchMetadataMulti($query: String!, $limit: Int, $language: String! = "eng") { searchMetadataMulti(query: $query, limit: $limit, language: $language) { movies { tvdbId smgId tmdbId imdbId name slug type year status overview posterUrl language runtimeMinutes sortTitle externalIds { source value } } series { tvdbId smgId tmdbId imdbId name slug type year status overview posterUrl language runtimeMinutes sortTitle externalIds { source value } } anime { tvdbId smgId tmdbId imdbId name slug type year status overview posterUrl language runtimeMinutes sortTitle externalIds { source value } } } }""";
    private const string DiscoveryItemDetailQuery = """query ScryerDiscoveryItemDetail($input: DiscoveryItemDetailInput!) { discoveryItemDetail(input: $input) { targetKey targetKind displayTitle year posterUrl overview rating ratingSources externalRatings { source value score normalized votes url } externalIds { source id } } }""";
    private const string MyMediaRequestsQuery = """query ScryerMyMediaRequests { myMediaRequests { id libraryId facet status identityFingerprint title sortTitle slug posterUrl year overview runtimeMinutes language contentStatus requestedQualityProfileId requestedQualityProfileName requestedMonitorType resolvedByUserId resolvedAt createdTitleId approvedQualityProfileId approvedQualityProfileName externalIds { source value } requesters { userId username avatarUrl } } }""";
    private const string ManageableMediaRequestsQuery = """query ScryerManageableMediaRequests { mediaRequests { id libraryId facet status identityFingerprint title sortTitle slug posterUrl year overview runtimeMinutes language contentStatus requestedQualityProfileId requestedQualityProfileName requestedMonitorType resolvedByUserId resolvedAt createdTitleId approvedQualityProfileId approvedQualityProfileName externalIds { source value } requesters { userId username avatarUrl requestedAt } createdByUserId createdAt updatedAt } }""";
    private const string SubmitMediaRequestMutation = """mutation ScryerSubmitMediaRequest($input: SubmitMediaRequestInput!) { submitMediaRequest(input: $input) { requestId } }""";
    private const string AddTitleMutation = """mutation ScryerAddTitle($input: AddTitleInput!) { addTitle(input: $input) { title { id name libraryId facet monitored } metadataHydrationState reusedExistingTitle } }""";
    private const string ApproveMediaRequestMutation = """mutation ScryerApproveMediaRequest($input: ApproveMediaRequestInput!) { approveMediaRequest(input: $input) { titleId wantedSearch { queuedCount skippedInProgressCount } searchError } }""";
    private const string DismissMediaRequestMutation = """mutation ScryerDismissMediaRequest($requestId: ID!) { dismissMediaRequest(requestId: $requestId) { requestId } }""";
    private const string UpdateMyMediaRequestMutation = """mutation ScryerUpdateMyMediaRequest($input: UpdateMediaRequestInput!) { updateMyMediaRequest(input: $input) { id libraryId facet status identityFingerprint title requestedQualityProfileId requestedQualityProfileName requestedMonitorType updatedAt } }""";
    private const string CancelMyMediaRequestMutation = """mutation ScryerCancelMyMediaRequest($requestId: ID!) { cancelMyMediaRequest(requestId: $requestId) { requestId } }""";
    private const string CalendarEpisodesQuery = """query ScryerCalendarEpisodes($startDate: Date!, $endDate: Date!) { calendarEpisodes(startDate: $startDate, endDate: $endDate) { id titleId libraryId libraryName librarySlug titleName titleSlug titleFacet seasonNumber episodeNumber episodeTitle overview imageUrl airDate monitored mediaAvailability { state primaryQualityLabel } } }""";
    private const string TitlePostersQuery = """query ScryerTitlePosters($id0: ID!, $id1: ID!, $id2: ID!, $id3: ID!, $id4: ID!, $id5: ID!, $id6: ID!, $id7: ID!, $id8: ID!, $id9: ID!, $id10: ID!, $id11: ID!, $id12: ID!, $id13: ID!, $id14: ID!, $id15: ID!) { t0: title(id: $id0) { id posterUrl } t1: title(id: $id1) { id posterUrl } t2: title(id: $id2) { id posterUrl } t3: title(id: $id3) { id posterUrl } t4: title(id: $id4) { id posterUrl } t5: title(id: $id5) { id posterUrl } t6: title(id: $id6) { id posterUrl } t7: title(id: $id7) { id posterUrl } t8: title(id: $id8) { id posterUrl } t9: title(id: $id9) { id posterUrl } t10: title(id: $id10) { id posterUrl } t11: title(id: $id11) { id posterUrl } t12: title(id: $id12) { id posterUrl } t13: title(id: $id13) { id posterUrl } t14: title(id: $id14) { id posterUrl } t15: title(id: $id15) { id posterUrl } }""";
    private const string DownloadQueuePageQuery = """query ScryerDownloadQueuePage($offset: Int!) { downloadQueuePage(limit: 50, offset: $offset, scryerSubmittedOnly: true) { items { id titleId episodeId titleName facet clientId clientName clientType state displayState progressPercent sizeBytes remainingSeconds attentionRequired attentionReason importStatus importErrorMessage importedAt } hasMore totalCount revision updatedAt ready stale } }""";
    private const string DownloadHistoryPageQuery = """query ScryerDownloadHistoryPage($offset: Int!) { downloadHistory(limit: 50, offset: $offset, scryerSubmittedOnly: true) { items { id titleId episodeId titleName facet clientId clientName clientType state displayState progressPercent sizeBytes importStatus importErrorMessage importedAt } hasMore totalCount } }""";

    private readonly IScryerOAuthConfigurationProvider _configurationProvider;
    private readonly IScryerUserSessionService _sessionService;
    private readonly HttpClient _httpClient;

    public ScryerGraphqlService(IScryerOAuthConfigurationProvider configurationProvider, IScryerUserSessionService sessionService, IHttpClientFactory httpClientFactory)
        : this(configurationProvider, sessionService, (httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory))).CreateClient(nameof(ScryerGraphqlService)))
    {
    }

    /// <summary>Inject a handler for isolated protocol tests.</summary>
    public ScryerGraphqlService(IScryerOAuthConfigurationProvider configurationProvider, IScryerUserSessionService sessionService, HttpMessageHandler messageHandler)
        : this(configurationProvider, sessionService, new HttpClient(messageHandler ?? throw new ArgumentNullException(nameof(messageHandler)), disposeHandler: false))
    {
    }

    private ScryerGraphqlService(IScryerOAuthConfigurationProvider configurationProvider, IScryerUserSessionService sessionService, HttpClient httpClient)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ScryerResult<ScryerCapabilitySnapshot>> GetCapabilitySnapshotAsync(string jellyfinUserId, CancellationToken cancellationToken)
    {
        var result = await ExecuteOperationAsync(jellyfinUserId, "ScryerCapabilityBootstrap", CapabilityBootstrapQuery, new { }, "me", cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? ParseCapabilitySnapshot(result.Value!) : ScryerResult<ScryerCapabilitySnapshot>.Fail(result.Failure!);
    }

    public async Task<ScryerResult<JsonElement>> GetRequestLibrariesAsync(string jellyfinUserId, string? facet, CancellationToken cancellationToken)
    {
        var libraries = await ExecuteOperationAsync(jellyfinUserId, "ScryerRequestLibraries", RequestLibrariesQuery, new { facet = NormalizeFacet(facet) }, "libraries", cancellationToken).ConfigureAwait(false);
        return libraries.IsSuccess
            ? AddRequestQualityProfileCompatibility(libraries.Value!)
            : ScryerResult<JsonElement>.Fail(libraries.Failure!);
    }

    public Task<ScryerResult<JsonElement>> GetManageableLibrariesAsync(string jellyfinUserId, string? facet, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerManageableLibraries", ManageableLibrariesQuery, new { facet = NormalizeFacet(facet) }, "libraries", cancellationToken);

    public Task<ScryerResult<JsonElement>> GetQualityProfilesAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerQualityProfiles", QualityProfilesQuery, new { }, "qualityProfileSettings", cancellationToken);

    public Task<ScryerResult<JsonElement>> GetDiscoveryHomeCardsAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerDiscoveryHomeCards", DiscoveryHomeCardsQuery, new { }, "discoveryHomeCards", cancellationToken);

    public Task<ScryerResult<JsonElement>> GetTitleRecommendationsAsync(string jellyfinUserId, string source, string value, int limit, CancellationToken cancellationToken) =>
        RecommendationExternalIdSources.Contains(source)
        && IsBoundedIdentifier(value)
        && limit is >= 1 and <= 30
            ? ExecuteOperationAsync(jellyfinUserId, "ScryerTitleRecommendations", TitleRecommendationsQuery, new { source, values = new[] { value.Trim() }, limit }, "titlesByExternalIds", cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    public Task<ScryerResult<JsonElement>> SearchMetadataMultiAsync(string jellyfinUserId, string query, int limit, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(query) || query.Trim().Length > MaximumSearchLength
            ? Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse))
            : ExecuteOperationAsync(jellyfinUserId, "ScryerSearchMetadataMulti", SearchMetadataMultiQuery, new { query = query.Trim(), limit = Math.Clamp(limit, 1, 50), language = "eng" }, "searchMetadataMulti", cancellationToken);

    public Task<ScryerResult<JsonElement>> GetDiscoveryItemDetailAsync(string jellyfinUserId, string targetKey, CancellationToken cancellationToken) =>
        IsBoundedIdentifier(targetKey)
            ? ExecuteOperationAsync(jellyfinUserId, "ScryerDiscoveryItemDetail", DiscoveryItemDetailQuery, new { input = new { targetKey = targetKey.Trim() } }, "discoveryItemDetail", cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    public Task<ScryerResult<JsonElement>> GetMyMediaRequestsAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerMyMediaRequests", MyMediaRequestsQuery, new { }, "myMediaRequests", cancellationToken);

    public Task<ScryerResult<JsonElement>> GetManageableMediaRequestsAsync(string jellyfinUserId, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerManageableMediaRequests", ManageableMediaRequestsQuery, new { }, "mediaRequests", cancellationToken);

    public Task<ScryerResult<JsonElement>> SubmitMediaRequestAsync(string jellyfinUserId, JsonElement input, CancellationToken cancellationToken) =>
        input.ValueKind == JsonValueKind.Object
            ? ExecuteOperationAsync(jellyfinUserId, "ScryerSubmitMediaRequest", SubmitMediaRequestMutation, new { input }, "submitMediaRequest", cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    public Task<ScryerResult<JsonElement>> AddTitleAsync(string jellyfinUserId, JsonElement input, CancellationToken cancellationToken) =>
        input.ValueKind == JsonValueKind.Object
            ? ExecuteOperationAsync(jellyfinUserId, "ScryerAddTitle", AddTitleMutation, new { input }, "addTitle", cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    public Task<ScryerResult<JsonElement>> ApproveMediaRequestAsync(string jellyfinUserId, string requestId, string qualityProfileId, string? monitorType, CancellationToken cancellationToken) =>
        IsBoundedIdentifier(requestId) && IsBoundedIdentifier(qualityProfileId) && TryNormalizeMonitorType(monitorType, out var normalizedMonitorType)
            ? ExecuteOperationAsync(jellyfinUserId, "ScryerApproveMediaRequest", ApproveMediaRequestMutation, new { input = new { requestId = requestId.Trim(), qualityProfileId = qualityProfileId.Trim(), monitorType = normalizedMonitorType } }, "approveMediaRequest", cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    public Task<ScryerResult<JsonElement>> DismissMediaRequestAsync(string jellyfinUserId, string requestId, CancellationToken cancellationToken) =>
        ExecuteIdentifierMutationAsync(jellyfinUserId, "ScryerDismissMediaRequest", DismissMediaRequestMutation, "dismissMediaRequest", requestId, cancellationToken);

    public Task<ScryerResult<JsonElement>> UpdateMyMediaRequestAsync(string jellyfinUserId, JsonElement input, CancellationToken cancellationToken) =>
        input.ValueKind == JsonValueKind.Object
            ? ExecuteOperationAsync(jellyfinUserId, "ScryerUpdateMyMediaRequest", UpdateMyMediaRequestMutation, new { input }, "updateMyMediaRequest", cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    public Task<ScryerResult<JsonElement>> CancelMyMediaRequestAsync(string jellyfinUserId, string requestId, CancellationToken cancellationToken) =>
        ExecuteIdentifierMutationAsync(jellyfinUserId, "ScryerCancelMyMediaRequest", CancelMyMediaRequestMutation, "cancelMyMediaRequest", requestId, cancellationToken);

    public Task<ScryerResult<JsonElement>> GetCalendarEpisodesAsync(string jellyfinUserId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        if (endDate < startDate || endDate.DayNumber - startDate.DayNumber > MaximumCalendarRangeDays)
        {
            return Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));
        }

        return ExecuteOperationAsync(jellyfinUserId, "ScryerCalendarEpisodes", CalendarEpisodesQuery, new
        {
            startDate = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, "calendarEpisodes", cancellationToken);
    }

    public async Task<ScryerResult<IReadOnlyDictionary<string, string?>>> GetTitlePostersAsync(
        string jellyfinUserId,
        IReadOnlyList<string> titleIds,
        CancellationToken cancellationToken)
    {
        var uniqueIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawId in titleIds)
        {
            if (uniqueIds.Count == MaximumCalendarTitles) break;
            if (!IsBoundedIdentifier(rawId)) return ScryerResult<IReadOnlyDictionary<string, string?>>.Fail(ScryerFailure.InvalidResponse);
            var id = rawId.Trim();
            if (seen.Add(id)) uniqueIds.Add(id);
        }

        var posters = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var offset = 0; offset < uniqueIds.Count; offset += TitlePosterBatchSize)
        {
            var count = Math.Min(TitlePosterBatchSize, uniqueIds.Count - offset);
            var fallbackId = uniqueIds[offset];
            var variables = new Dictionary<string, string>(TitlePosterBatchSize, StringComparer.Ordinal);
            for (var index = 0; index < TitlePosterBatchSize; index++)
            {
                variables[$"id{index}"] = index < count ? uniqueIds[offset + index] : fallbackId;
            }

            var result = await ExecuteOperationAsync(jellyfinUserId, "ScryerTitlePosters", TitlePostersQuery, variables, null, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return ScryerResult<IReadOnlyDictionary<string, string?>>.Fail(result.Failure!);
            foreach (var title in result.Value!.EnumerateObject())
            {
                if (title.Value.ValueKind != JsonValueKind.Object || !TryReadBoundedString(title.Value, "id", out var id)) continue;
                var posterUrl = title.Value.TryGetProperty("posterUrl", out var poster) && poster.ValueKind == JsonValueKind.String
                    ? poster.GetString()?.Trim()
                    : null;
                posters[id] = string.IsNullOrEmpty(posterUrl) || posterUrl.Length > 4096 ? null : posterUrl;
            }
        }

        return ScryerResult<IReadOnlyDictionary<string, string?>>.Success(posters);
    }

    public Task<ScryerResult<JsonElement>> GetDownloadQueuePageAsync(string jellyfinUserId, int offset, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerDownloadQueuePage", DownloadQueuePageQuery, new { offset = Math.Clamp(offset, 0, MaximumPageOffset) }, "downloadQueuePage", cancellationToken);

    public Task<ScryerResult<JsonElement>> GetDownloadHistoryPageAsync(string jellyfinUserId, int offset, CancellationToken cancellationToken) =>
        ExecuteOperationAsync(jellyfinUserId, "ScryerDownloadHistoryPage", DownloadHistoryPageQuery, new { offset = Math.Clamp(offset, 0, MaximumPageOffset) }, "downloadHistory", cancellationToken);

    public async Task<ScryerResult<IReadOnlyList<ScryerTvDiscoveryRail>>> GetAndroidTvDiscoveryAsync(
        string jellyfinUserId,
        IReadOnlyList<ScryerRecommendationSeed> recentSeeds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recentSeeds);
        var configuration = _configurationProvider.GetConfiguration();
        if (!configuration.IsSuccess)
        {
            return ScryerResult<IReadOnlyList<ScryerTvDiscoveryRail>>.Fail(configuration.Failure!);
        }

        var publicAuthority = configuration.Value!.PublicAuthority;
        var rails = new List<ScryerTvDiscoveryRail>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in recentSeeds.Take(5))
        {
            foreach (var lookup in RecommendationLookups(seed))
            {
                var recommendation = await GetTitleRecommendationsAsync(jellyfinUserId, lookup.Source, lookup.Value, 20, cancellationToken).ConfigureAwait(false);
                if (!recommendation.IsSuccess || !TryParseRecommendationItems(recommendation.Value!, out var items))
                {
                    continue;
                }

                if (!TryResolvePosterUrls(items, publicAuthority, out var resolvedItems))
                {
                    continue;
                }

                var unique = Deduplicate(resolvedItems, seenTargets);
                if (unique.Count > 0)
                {
                    rails.Add(new ScryerTvDiscoveryRail($"recent:{rails.Count}:{seed.Title}", $"More like {seed.Title}", unique));
                    break;
                }
            }
        }

        var home = await GetDiscoveryHomeCardsAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return ScryerResult<IReadOnlyList<ScryerTvDiscoveryRail>>.Fail(home.Failure!);
        }

        if (home.Value!.ValueKind != JsonValueKind.Object ||
            !home.Value.TryGetProperty("canViewPersonalized", out var canViewPersonalized) ||
            canViewPersonalized.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            (canViewPersonalized.GetBoolean() && !TryAppendDiscoverySections(home.Value, "personalizedSections", publicAuthority, seenTargets, rails)) ||
            !TryAppendDiscoverySections(home.Value, "publicSections", publicAuthority, seenTargets, rails))
        {
            return ScryerResult<IReadOnlyList<ScryerTvDiscoveryRail>>.Fail(ScryerFailure.InvalidResponse);
        }

        return ScryerResult<IReadOnlyList<ScryerTvDiscoveryRail>>.Success(rails);
    }

    public async Task<ScryerResult<ScryerTvActionResult>> ResolveDefaultTvActionAndExecuteAsync(
        string jellyfinUserId,
        string targetKey,
        string targetKind,
        CancellationToken cancellationToken)
    {
        var facet = NormalizeFacet(targetKind);
        if (!IsBoundedIdentifier(targetKey) || facet is null)
        {
            return TvActionFailure("This discovery item is no longer valid.");
        }

        var manageable = await GetManageableLibrariesAsync(jellyfinUserId, facet, cancellationToken).ConfigureAwait(false);
        if (!manageable.IsSuccess)
        {
            return ScryerResult<ScryerTvActionResult>.Fail(manageable.Failure!);
        }

        if (!TryReadTvLibraries(manageable.Value!, request: false, facet, out var manageableLibraries))
        {
            return ScryerResult<ScryerTvActionResult>.Fail(ScryerFailure.InvalidResponse);
        }

        var useManage = manageableLibraries.Count > 0;
        IReadOnlyList<ScryerTvLibrary> libraries = manageableLibraries;
        if (!useManage)
        {
            var requestable = await GetRequestLibrariesAsync(jellyfinUserId, facet, cancellationToken).ConfigureAwait(false);
            if (!requestable.IsSuccess)
            {
                return ScryerResult<ScryerTvActionResult>.Fail(requestable.Failure!);
            }

            if (!TryReadTvLibraries(requestable.Value!, request: true, facet, out libraries))
            {
                return ScryerResult<ScryerTvActionResult>.Fail(ScryerFailure.InvalidResponse);
            }
        }

        if (libraries.Count == 0)
        {
            return ScryerResult<ScryerTvActionResult>.Fail(new ScryerFailure(
                ScryerFailureCode.PermissionDenied,
                "Your Scryer account cannot add or request this kind of title."));
        }

        var defaults = libraries.Where(library => library.IsDefault).ToArray();
        if (defaults.Length != 1)
        {
            return TvActionFailure("Configure exactly one default Scryer library for this media type.");
        }

        var destination = defaults[0];
        if (string.IsNullOrWhiteSpace(destination.QualityProfileId))
        {
            return TvActionFailure("Configure a default quality profile on the default Scryer library.");
        }

        var detail = await GetDiscoveryItemDetailAsync(jellyfinUserId, targetKey, cancellationToken).ConfigureAwait(false);
        if (!detail.IsSuccess)
        {
            return ScryerResult<ScryerTvActionResult>.Fail(detail.Failure!);
        }

        if (!TryReadTvActionDetail(detail.Value!, targetKey, facet, out var actionDetail))
        {
            return TvActionFailure("This title has no supported external identifier.");
        }

        var manageOptions = new Dictionary<string, object?>
        {
            ["qualityProfileId"] = destination.QualityProfileId,
            ["monitorType"] = "MONITORED"
        };
        if (facet is not "MOVIE")
        {
            manageOptions["useSeasonFolders"] = true;
        }

        var input = useManage
            ? JsonSerializer.SerializeToElement(new
            {
                name = actionDetail.Title,
                libraryId = destination.Id,
                facet,
                monitored = true,
                tags = Array.Empty<string>(),
                options = manageOptions,
                externalIds = actionDetail.ExternalIds.Select(id => new { source = id.Source, value = id.Value }).ToArray(),
                year = actionDetail.Year,
                overview = actionDetail.Overview
            })
            : JsonSerializer.SerializeToElement(new
            {
                libraryId = destination.Id,
                facet,
                title = actionDetail.Title,
                externalIds = actionDetail.ExternalIds.Select(id => new { source = id.Source, value = id.Value }).ToArray(),
                year = actionDetail.Year,
                overview = actionDetail.Overview,
                requestedQualityProfileId = destination.QualityProfileId,
                requestedMonitorType = "MONITORED"
            });

        if (!useManage)
        {
            var requested = await SubmitMediaRequestAsync(jellyfinUserId, input, cancellationToken).ConfigureAwait(false);
            return requested.IsSuccess && requested.Value!.ValueKind == JsonValueKind.Object && TryReadBoundedString(requested.Value!, "requestId", out _)
                ? ScryerResult<ScryerTvActionResult>.Success(new ScryerTvActionResult(ScryerTvActionKind.Requested, destination.Name))
                : ScryerResult<ScryerTvActionResult>.Fail(requested.Failure ?? ScryerFailure.InvalidResponse);
        }

        var added = await AddTitleAsync(jellyfinUserId, input, cancellationToken).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return ScryerResult<ScryerTvActionResult>.Fail(added.Failure!);
        }

        if (added.Value!.ValueKind != JsonValueKind.Object ||
            !added.Value!.TryGetProperty("reusedExistingTitle", out var reused) ||
            reused.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ScryerResult<ScryerTvActionResult>.Fail(ScryerFailure.InvalidResponse);
        }

        return ScryerResult<ScryerTvActionResult>.Success(new ScryerTvActionResult(
            reused.GetBoolean() ? ScryerTvActionKind.AlreadyPresent : ScryerTvActionKind.Added,
            destination.Name));
    }

    private static IEnumerable<(string Source, string Value)> RecommendationLookups(ScryerRecommendationSeed seed)
    {
        var movie = string.Equals(NormalizeFacet(seed.Facet), "MOVIE", StringComparison.Ordinal);
        foreach (var source in movie
            ? new[] { "tmdb", "tmdb_movie", "imdb", "tvdb", "tvdb_movie" }
            : new[] { "tvdb", "tvdb_series", "tvdb_show", "tmdb", "tmdb_series", "tmdb_tv", "tmdb_show", "imdb" })
        {
            var baseSource = source.Split('_', 2)[0];
            var value = seed.ProviderIds.FirstOrDefault(pair => string.Equals(pair.Key, baseSource, StringComparison.OrdinalIgnoreCase)).Value;
            if (IsBoundedIdentifier(value))
            {
                yield return (source, value.Trim());
            }
        }
    }

    private static bool TryParseRecommendationItems(JsonElement titles, out IReadOnlyList<ScryerTvDiscoveryItem> items)
    {
        items = Array.Empty<ScryerTvDiscoveryItem>();
        if (titles.ValueKind != JsonValueKind.Array || titles.GetArrayLength() > 8)
        {
            return false;
        }

        foreach (var title in titles.EnumerateArray())
        {
            if (title.ValueKind != JsonValueKind.Object || !title.TryGetProperty("moreLikeThis", out var recommendations))
            {
                return false;
            }

            if (TryReadDiscoveryItems(recommendations, out items) && items.Count > 0)
            {
                return true;
            }
        }

        return true;
    }

    private static bool TryAppendDiscoverySections(
        JsonElement home,
        string propertyName,
        Uri publicAuthority,
        HashSet<string> seenTargets,
        List<ScryerTvDiscoveryRail> rails)
    {
        if (home.ValueKind != JsonValueKind.Object || !home.TryGetProperty(propertyName, out var sections) ||
            sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() > 64)
        {
            return false;
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object ||
                !TryReadBoundedString(section, "sectionId", out var sectionId) ||
                !TryReadBoundedString(section, "title", out var title) ||
                !section.TryGetProperty("items", out var sectionItems) ||
                !TryReadDiscoveryItems(sectionItems, out var items))
            {
                return false;
            }

            if (!TryResolvePosterUrls(items, publicAuthority, out var resolvedItems))
            {
                return false;
            }

            var unique = Deduplicate(resolvedItems, seenTargets);
            if (unique.Count > 0)
            {
                rails.Add(new ScryerTvDiscoveryRail($"{propertyName}:{sectionId}", title, unique));
            }
        }

        return true;
    }

    private static bool TryReadDiscoveryItems(JsonElement value, out IReadOnlyList<ScryerTvDiscoveryItem> items)
    {
        items = Array.Empty<ScryerTvDiscoveryItem>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 30)
        {
            return false;
        }

        var parsed = new List<ScryerTvDiscoveryItem>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryReadBoundedString(item, "targetKey", out var targetKey) ||
                !TryReadBoundedString(item, "targetKind", out var targetKind) ||
                NormalizeFacet(targetKind) is null ||
                !TryReadBoundedString(item, "displayTitle", out var displayTitle))
            {
                return false;
            }

            int? year = null;
            if (item.TryGetProperty("year", out var yearElement) && yearElement.ValueKind != JsonValueKind.Null)
            {
                if (yearElement.ValueKind != JsonValueKind.Number || !yearElement.TryGetInt32(out var parsedYear) || parsedYear is < 1800 or > 2100)
                {
                    return false;
                }

                year = parsedYear;
            }

            string? posterUrl = null;
            if (item.TryGetProperty("posterUrl", out var posterElement) && posterElement.ValueKind != JsonValueKind.Null)
            {
                posterUrl = posterElement.ValueKind == JsonValueKind.String ? posterElement.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(posterUrl) || posterUrl.Length > 4096 || posterUrl.StartsWith("//", StringComparison.Ordinal) ||
                    (!posterUrl.StartsWith("/", StringComparison.Ordinal) &&
                     (!Uri.TryCreate(posterUrl, UriKind.Absolute, out var posterUri) ||
                      (posterUri.Scheme != Uri.UriSchemeHttps && posterUri.Scheme != Uri.UriSchemeHttp))))
                {
                    return false;
                }
            }

            parsed.Add(new ScryerTvDiscoveryItem(targetKey, NormalizeFacet(targetKind)!, displayTitle, year, posterUrl, null));
        }

        items = parsed;
        return true;
    }

    private static IReadOnlyList<ScryerTvDiscoveryItem> Deduplicate(
        IReadOnlyList<ScryerTvDiscoveryItem> items,
        HashSet<string> seenTargets)
    {
        var unique = new List<ScryerTvDiscoveryItem>(items.Count);
        foreach (var item in items)
        {
            if (seenTargets.Add(item.TargetKey))
            {
                unique.Add(item);
            }
        }

        return unique;
    }

    private static bool TryResolvePosterUrls(
        IReadOnlyList<ScryerTvDiscoveryItem> items,
        Uri publicAuthority,
        out IReadOnlyList<ScryerTvDiscoveryItem> resolved)
    {
        var result = new List<ScryerTvDiscoveryItem>(items.Count);
        foreach (var item in items)
        {
            if (item.PosterUrl is null)
            {
                result.Add(item);
                continue;
            }

            if (!Uri.TryCreate(publicAuthority, item.PosterUrl, out var posterUri) ||
                (posterUri.Scheme != Uri.UriSchemeHttps && posterUri.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(posterUri.UserInfo))
            {
                resolved = Array.Empty<ScryerTvDiscoveryItem>();
                return false;
            }

            result.Add(item with { PosterUrl = posterUri.AbsoluteUri });
        }

        resolved = result;
        return true;
    }

    private static bool TryReadTvLibraries(JsonElement value, bool request, string expectedFacet, out IReadOnlyList<ScryerTvLibrary> libraries)
    {
        libraries = Array.Empty<ScryerTvLibrary>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumLibraryGrants)
        {
            return false;
        }

        var parsed = new List<ScryerTvLibrary>();
        foreach (var library in value.EnumerateArray())
        {
            if (library.ValueKind != JsonValueKind.Object ||
                !TryReadBoundedString(library, "id", out var id) ||
                !TryReadBoundedString(library, "name", out var name) ||
                !TryReadBoundedString(library, "facet", out var facet) ||
                !string.Equals(NormalizeFacet(facet), expectedFacet, StringComparison.Ordinal) ||
                !library.TryGetProperty("isDefault", out var isDefault) ||
                isDefault.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            var profileProperty = request ? "requestQualityProfileDefaultId" : "qualityProfileId";
            string? profileId = null;
            if (library.TryGetProperty(profileProperty, out var profile) && profile.ValueKind != JsonValueKind.Null)
            {
                profileId = profile.ValueKind == JsonValueKind.String ? profile.GetString()?.Trim() : null;
                if (!IsBoundedIdentifier(profileId ?? string.Empty))
                {
                    return false;
                }
            }

            parsed.Add(new ScryerTvLibrary(id, name, isDefault.GetBoolean(), profileId));
        }

        libraries = parsed;
        return true;
    }

    private static bool TryReadTvActionDetail(JsonElement value, string expectedTargetKey, string expectedFacet, out ScryerTvActionDetail detail)
    {
        detail = default!;
        if (value.ValueKind != JsonValueKind.Object ||
            !TryReadBoundedString(value, "targetKey", out var targetKey) ||
            !string.Equals(targetKey, expectedTargetKey.Trim(), StringComparison.Ordinal) ||
            !TryReadBoundedString(value, "targetKind", out var targetKind) ||
            !string.Equals(NormalizeFacet(targetKind), expectedFacet, StringComparison.Ordinal) ||
            !TryReadBoundedString(value, "displayTitle", out var title) ||
            !value.TryGetProperty("externalIds", out var externalIds) ||
            externalIds.ValueKind != JsonValueKind.Array || externalIds.GetArrayLength() is < 1 or > 16)
        {
            return false;
        }

        var ids = new List<ScryerTvExternalId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var externalId in externalIds.EnumerateArray())
        {
            if (externalId.ValueKind != JsonValueKind.Object ||
                !TryReadBoundedString(externalId, "source", out var source) || source.Length > 64 ||
                !TryReadBoundedString(externalId, "id", out var id))
            {
                return false;
            }

            source = source.ToLowerInvariant();
            if (seen.Add(source + "\u001f" + id))
            {
                ids.Add(new ScryerTvExternalId(source, id));
            }
        }

        int? year = null;
        if (value.TryGetProperty("year", out var yearElement) && yearElement.ValueKind != JsonValueKind.Null)
        {
            if (yearElement.ValueKind != JsonValueKind.Number || !yearElement.TryGetInt32(out var parsedYear) || parsedYear is < 1800 or > 2100)
            {
                return false;
            }

            year = parsedYear;
        }

        string? overview = null;
        if (value.TryGetProperty("overview", out var overviewElement) && overviewElement.ValueKind != JsonValueKind.Null)
        {
            overview = overviewElement.ValueKind == JsonValueKind.String ? overviewElement.GetString()?.Trim() : null;
            if (overview is null || overview.Length > 8192)
            {
                return false;
            }
        }

        detail = new ScryerTvActionDetail(title, year, overview, ids);
        return true;
    }

    private static ScryerResult<ScryerTvActionResult> TvActionFailure(string message) =>
        ScryerResult<ScryerTvActionResult>.Fail(new ScryerFailure(ScryerFailureCode.InvalidResponse, message));

    private sealed record ScryerTvLibrary(string Id, string Name, bool IsDefault, string? QualityProfileId);
    private sealed record ScryerTvExternalId(string Source, string Value);
    private sealed record ScryerTvActionDetail(string Title, int? Year, string? Overview, IReadOnlyList<ScryerTvExternalId> ExternalIds);

    private Task<ScryerResult<JsonElement>> ExecuteIdentifierMutationAsync(string jellyfinUserId, string operationName, string document, string rootField, string requestId, CancellationToken cancellationToken) =>
        IsBoundedIdentifier(requestId)
            ? ExecuteOperationAsync(jellyfinUserId, operationName, document, new { requestId = requestId.Trim() }, rootField, cancellationToken)
            : Task.FromResult(ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse));

    private async Task<ScryerResult<JsonElement>> ExecuteOperationAsync(string jellyfinUserId, string operationName, string document, object variables, string? rootField, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jellyfinUserId)) return ScryerResult<JsonElement>.Fail(ScryerFailure.NotConnected);

        byte[] payload;
        try { payload = JsonSerializer.SerializeToUtf8Bytes(new { operationName, query = document, variables }); }
        catch (JsonException) { return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse); }
        if (payload.Length > MaximumRequestBytes) return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse);

        var configuration = _configurationProvider.GetConfiguration();
        if (!configuration.IsSuccess) return ScryerResult<JsonElement>.Fail(configuration.Failure!);
        var lease = await _sessionService.GetAccessTokenAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (!lease.IsSuccess) return ScryerResult<JsonElement>.Fail(lease.Failure!);
        var currentConfiguration = _configurationProvider.GetConfiguration();
        if (!currentConfiguration.IsSuccess || !SameConfiguration(configuration.Value!, currentConfiguration.Value!))
        {
            return ScryerResult<JsonElement>.Fail(ScryerFailure.AuthorizationExpired);
        }

        return await PostAndProjectAsync(BuildGraphqlEndpoint(currentConfiguration.Value!.InternalAuthority), lease.Value!, payload, rootField, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScryerResult<JsonElement>> PostAndProjectAsync(Uri endpoint, ScryerAccessTokenLease lease, byte[] payload, string? rootField, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
            using var timeout = CreateTimeout(cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var statusFailure = MapStatus(response.StatusCode);
            if (HasTerminalHttpFailure(response.StatusCode)) return ScryerResult<JsonElement>.Fail(statusFailure!);
            if (!IsJsonResponse(response.Content.Headers.ContentType)) return ScryerResult<JsonElement>.Fail(statusFailure ?? ScryerFailure.InvalidResponse);

            using var document = await ReadJsonAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var graphQlFailure = MapGraphQlFailure(document.RootElement);
            if (graphQlFailure is not null) return ScryerResult<JsonElement>.Fail(graphQlFailure);
            if (statusFailure is not null || !TryGetRootField(document.RootElement, rootField, out var root))
            {
                return ScryerResult<JsonElement>.Fail(statusFailure ?? ScryerFailure.InvalidResponse);
            }

            return ScryerResult<JsonElement>.Success(root.Clone());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ScryerResult<JsonElement>.Fail(ScryerFailure.Offline); }
        catch (HttpRequestException) { return ScryerResult<JsonElement>.Fail(ScryerFailure.Offline); }
        catch (JsonException) { return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse); }
        catch (InvalidDataException) { return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse); }
        catch (UriFormatException) { return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse); }
    }

    private static ScryerResult<ScryerCapabilitySnapshot> ParseCapabilitySnapshot(JsonElement me)
    {
        if (me.ValueKind != JsonValueKind.Object || !TryReadBoundedString(me, "id", out var userId) || !TryReadBoundedString(me, "username", out var username) ||
            !TryReadPermissionSet(me, "appPermissions", MaximumAppPermissions, out var appPermissions) || !TryReadLibraryCapabilities(me, out var libraries))
        {
            return ScryerResult<ScryerCapabilitySnapshot>.Fail(ScryerFailure.InvalidResponse);
        }

        return ScryerResult<ScryerCapabilitySnapshot>.Success(new ScryerCapabilitySnapshot(userId, username, appPermissions, libraries));
    }

    private static bool TryReadLibraryCapabilities(JsonElement me, out IReadOnlyList<ScryerLibraryCapabilities> libraries)
    {
        libraries = Array.Empty<ScryerLibraryCapabilities>();
        if (!me.TryGetProperty("libraryPermissions", out var grants) || grants.ValueKind != JsonValueKind.Array || grants.GetArrayLength() > MaximumLibraryGrants) return false;
        var byLibraryId = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var grant in grants.EnumerateArray())
        {
            if (grant.ValueKind != JsonValueKind.Object || !TryReadBoundedString(grant, "libraryId", out var libraryId) || !TryReadPermissionSet(grant, "permissions", MaximumAppPermissions, out var permissions)) return false;
            if (!byLibraryId.TryGetValue(libraryId, out var combined)) byLibraryId[libraryId] = combined = new HashSet<string>(StringComparer.Ordinal);
            combined.UnionWith(permissions);
        }

        var result = new List<ScryerLibraryCapabilities>(byLibraryId.Count);
        foreach (var pair in byLibraryId)
        {
            result.Add(new ScryerLibraryCapabilities(pair.Key, pair.Value.Contains("VIEW"), pair.Value.Contains("REQUEST"), pair.Value.Contains("AUTO_APPROVE_REQUESTS"), pair.Value.Contains("MANAGE_TITLES")));
        }

        libraries = result;
        return true;
    }

    private static bool TryReadPermissionSet(JsonElement owner, string propertyName, int maximumCount, out IReadOnlyList<string> permissions)
    {
        permissions = Array.Empty<string>();
        if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maximumCount) return false;
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in property.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(value) || value.Length > MaximumIdentifierLength) return false;
            result.Add(value);
        }

        permissions = new List<string>(result);
        return true;
    }

    private static bool TryReadBoundedString(JsonElement owner, string propertyName, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= MaximumIdentifierLength;
    }

    private static bool TryGetRootField(JsonElement root, string? rootField, out JsonElement value)
    {
        value = default;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
        if (rootField is null)
        {
            value = data;
            return true;
        }

        return data.TryGetProperty(rootField, out value);
    }

    private static ScryerResult<JsonElement> AddRequestQualityProfileCompatibility(JsonElement libraries)
    {
        if (libraries.ValueKind != JsonValueKind.Array)
        {
            return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var library in libraries.EnumerateArray())
            {
                if (library.ValueKind != JsonValueKind.Object || !library.TryGetProperty("requestQualityProfileDefaultId", out var defaultProfileId))
                {
                    return ScryerResult<JsonElement>.Fail(ScryerFailure.InvalidResponse);
                }

                writer.WriteStartObject();
                foreach (var property in library.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "qualityProfileId", StringComparison.Ordinal))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WritePropertyName("qualityProfileId");
                defaultProfileId.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var projected = JsonDocument.Parse(buffer.ToArray());
        return ScryerResult<JsonElement>.Success(projected.RootElement.Clone());
    }

    private static ScryerFailure? MapGraphQlFailure(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return null;
        foreach (var error in errors.EnumerateArray())
        {
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("extensions", out var extensions) &&
                extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString()?.Trim().ToUpperInvariant() : null;
            switch (code)
            {
                case "UNAUTHORIZED":
                    return ScryerFailure.AuthorizationExpired;
                case "FORBIDDEN":
                case "PERMISSION_DENIED":
                    return PermissionDenied();
                case "TEMPORARY_UNAVAILABLE":
                case "RATE_LIMITED":
                    return RateLimited();
                case "VALIDATION_ERROR":
                case "GRAPHQL_VALIDATION_FAILED":
                    return ScryerFailure.Incompatible;
                case "INTERNAL_ERROR":
                case "INTERNAL_SERVER_ERROR":
                    return ScryerFailure.Offline;
            }
        }

        return ScryerFailure.Internal;
    }

    private static ScryerFailure? MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => ScryerFailure.AuthorizationExpired,
        HttpStatusCode.Forbidden => PermissionDenied(),
        HttpStatusCode.TooManyRequests => RateLimited(),
        _ when (int)statusCode >= 500 => ScryerFailure.Offline,
        _ when (int)statusCode is >= 200 and <= 299 => null,
        _ => ScryerFailure.InvalidResponse
    };

    private static ScryerFailure PermissionDenied() => new(ScryerFailureCode.PermissionDenied, "Your Scryer account does not have permission to perform this action.");
    private static ScryerFailure RateLimited() => new(ScryerFailureCode.RateLimited, "Scryer is rate limiting requests. Try again shortly.");
    private static bool HasTerminalHttpFailure(HttpStatusCode statusCode) => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    private static bool IsJsonResponse(MediaTypeHeaderValue? contentType) => contentType?.MediaType is not null && (contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || contentType.MediaType.Equals("application/graphql-response+json", StringComparison.OrdinalIgnoreCase));

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumResponseBytes) throw new InvalidDataException("GraphQL response exceeded the maximum size.");
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonDocument.Parse(buffered.ToArray());
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return timeout;
    }

    private static Uri BuildGraphqlEndpoint(Uri authority)
    {
        var basePath = authority.AbsolutePath.TrimEnd('/');
        return new UriBuilder(authority) { Path = (basePath == "/" ? string.Empty : basePath) + "/graphql", Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    private static bool SameConfiguration(ScryerOAuthConfiguration left, ScryerOAuthConfiguration right) =>
        Uri.Compare(left.InternalAuthority, right.InternalAuthority, UriComponents.AbsoluteUri, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 &&
        Uri.Compare(left.PublicAuthority, right.PublicAuthority, UriComponents.AbsoluteUri, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 &&
        Uri.Compare(left.RedirectUri, right.RedirectUri, UriComponents.AbsoluteUri, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 &&
        string.Equals(left.ClientId, right.ClientId, StringComparison.Ordinal);

    private static bool IsBoundedIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaximumIdentifierLength;
    private static string? NormalizeFacet(string? facet) => facet?.Trim().ToUpperInvariant() switch { "MOVIE" or "SERIES" or "ANIME" => facet.Trim().ToUpperInvariant(), _ => null };
    private static bool TryNormalizeMonitorType(string? monitorType, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(monitorType) ? null : monitorType.Trim().ToUpperInvariant();
        return normalized is null or "MONITORED" or "UNMONITORED" or "FUTURE_EPISODES" or "MISSING_AND_FUTURE_EPISODES" or "ALL_EPISODES" or "NONE";
    }
}
