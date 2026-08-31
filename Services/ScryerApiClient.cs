using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Scryer.Services;

// GraphQL client for Scryer (POST /graphql, Authorization: Bearer <api key>).
public class ScryerApiClient
{
    private readonly HttpClient _httpClient;

    public ScryerApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(ScryerApiClient));
    }

    public async Task<JsonElement> ExecuteAsync(string query, object? variables, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Scryer plugin is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(config.ScryerApiBaseUrl), "/graphql"))
        {
            Content = JsonContent.Create(new { query, variables })
        };

        if (!string.IsNullOrEmpty(config.ScryerApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ScryerApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (body.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"Scryer GraphQL error: {errors}");
        }

        return body.GetProperty("data");
    }

    public Task<JsonElement> SearchMetadataMultiAsync(string query, int limit, CancellationToken cancellationToken)
    {
        const string gql = """
            query SearchMetadataMulti($query: String!, $limit: Int!) {
                searchMetadataMulti(query: $query, limit: $limit) {
                    movies { tmdbId tvdbId imdbId name year overview posterUrl }
                    series { tmdbId tvdbId imdbId name year overview posterUrl }
                    anime { tmdbId tvdbId imdbId name year overview posterUrl }
                }
            }
            """;

        return ExecuteAsync(gql, new { query, limit }, cancellationToken);
    }

    public Task<JsonElement> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        // roots (with real IDs) come from here, not the standalone rootFolders(facet)
        // query -- that one only returns path/isDefault, no ID, and Scryer's addTitle
        // rejects a path used as rootFolderId ("must reference a configured library root").
        const string gql = """
            query Libraries {
                libraries { id name facet qualityProfileId roots { id path isDefault } }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    // Card payloads have no overview/externalIds; only DiscoveryItemPayload does.
    public Task<JsonElement> GetDiscoveryItemDetailAsync(string targetKey, CancellationToken cancellationToken)
    {
        const string gql = """
            query DiscoveryItemDetail($targetKey: String!) {
                discoveryItemDetail(input: { targetKey: $targetKey }) {
                    targetKey targetKind displayTitle year posterUrl overview
                    externalIds { source id }
                    rating
                    externalRatings { source value score normalized votes url }
                }
            }
            """;

        return ExecuteAsync(gql, new { targetKey }, cancellationToken);
    }

    public Task<JsonElement> GetQualityProfilesAsync(CancellationToken cancellationToken)
    {
        const string gql = """
            query QualityProfiles {
                qualityProfileSettings { profiles { id name } }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    // Uses plain addTitle, not addTitleAndQueueDownload: the latter needs a sourceHint we have no UI for.
    public Task<JsonElement> AddTitleAsync(object input, CancellationToken cancellationToken)
    {
        const string gql = """
            mutation AddTitle($input: AddTitleInput!) {
                addTitle(input: $input) {
                    title { id name }
                }
            }
            """;

        return ExecuteAsync(gql, new { input }, cancellationToken);
    }

    public Task<JsonElement> GetDiscoveryHomeCardsAsync(CancellationToken cancellationToken)
    {
        const string gql = """
            query DiscoveryHomeCards {
                discoveryHomeCards {
                    heroItem { id targetKey targetKind displayTitle year posterUrl }
                    publicSections {
                        sectionId
                        title
                        items { id targetKey targetKind displayTitle year posterUrl }
                    }
                    personalizedSections {
                        sectionId
                        title
                        items { id targetKey targetKind displayTitle year posterUrl }
                    }
                    canViewPersonalized
                }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    public Task<JsonElement> GetCalendarEpisodesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
    {
        const string gql = """
            query CalendarEpisodes($startDate: Date!, $endDate: Date!) {
                calendarEpisodes(startDate: $startDate, endDate: $endDate) {
                    id titleId titleName titleSlug titleFacet libraryName
                    seasonNumber episodeNumber episodeTitle overview
                    airDate imageUrl monitored
                    mediaAvailability { state primaryQualityLabel }
                }
            }
            """;

        return ExecuteAsync(
            gql,
            new { startDate = startDate.ToString("yyyy-MM-dd"), endDate = endDate.ToString("yyyy-MM-dd") },
            cancellationToken);
    }

    // Batches distinct title IDs into one aliased query (title(id:) has no plural form).
    public async Task<Dictionary<string, string?>> GetTitlePostersAsync(IReadOnlyList<string> titleIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>();
        if (titleIds.Count == 0)
        {
            return result;
        }

        var variableDecls = string.Join(", ", titleIds.Select((_, i) => $"$id{i}: ID!"));
        var fields = string.Join(" ", titleIds.Select((_, i) => $"t{i}: title(id: $id{i}) {{ id posterUrl }}"));
        var gql = $"query TitlePosters({variableDecls}) {{ {fields} }}";

        var variables = new Dictionary<string, object>();
        for (var i = 0; i < titleIds.Count; i++)
        {
            variables[$"id{i}"] = titleIds[i];
        }

        var data = await ExecuteAsync(gql, variables, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < titleIds.Count; i++)
        {
            if (data.TryGetProperty($"t{i}", out var title) && title.ValueKind == JsonValueKind.Object &&
                title.TryGetProperty("posterUrl", out var posterUrl) && posterUrl.ValueKind == JsonValueKind.String)
            {
                result[titleIds[i]] = posterUrl.GetString();
            }
        }

        return result;
    }

    public Task<JsonElement> GetMyMediaRequestsAsync(CancellationToken cancellationToken)
    {
        const string gql = """
            query MyMediaRequests {
                myMediaRequests {
                    id libraryId facet status title year posterUrl createdAt createdTitleId
                }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    public Task<JsonElement> GetAllMediaRequestsAsync(CancellationToken cancellationToken)
    {
        const string gql = """
            query MediaRequests {
                mediaRequests {
                    id libraryId facet status title year posterUrl createdAt createdTitleId
                }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    public Task<JsonElement> SubmitMediaRequestAsync(object input, CancellationToken cancellationToken)
    {
        const string gql = """
            mutation SubmitMediaRequest($input: SubmitMediaRequestInput!) {
                submitMediaRequest(input: $input) {
                    requestId
                }
            }
            """;

        return ExecuteAsync(gql, new { input }, cancellationToken);
    }

    public Task<JsonElement> ApproveMediaRequestAsync(string requestId, string qualityProfileId, CancellationToken cancellationToken)
    {
        const string gql = """
            mutation ApproveMediaRequest($input: ApproveMediaRequestInput!) {
                approveMediaRequest(input: $input) {
                    titleId
                    wantedSearch { queuedCount skippedInProgressCount }
                    searchError
                }
            }
            """;

        return ExecuteAsync(gql, new { input = new { requestId, qualityProfileId } }, cancellationToken);
    }

    public Task<JsonElement> GetDownloadQueueAsync(CancellationToken cancellationToken)
    {
        const string gql = """
            query DownloadQueuePage {
                downloadQueuePage(limit: 100, offset: 0, scryerSubmittedOnly: true) {
                    items {
                        id titleId titleName facet clientName
                        state displayState progressPercent
                        sizeBytes remainingSeconds
                        attentionRequired attentionReason
                    }
                    totalCount
                    hasMore
                    ready
                }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    public Task<JsonElement> GetDownloadHistoryAsync(CancellationToken cancellationToken)
    {
        const string gql = """
            query DownloadHistoryPage {
                downloadHistory(limit: 100, offset: 0, scryerSubmittedOnly: true) {
                    items {
                        id titleId titleName facet clientName
                        state displayState progressPercent
                        sizeBytes importedAt importErrorMessage
                    }
                    totalCount
                    hasMore
                }
            }
            """;

        return ExecuteAsync(gql, null, cancellationToken);
    }

    public Task<JsonElement> DismissMediaRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        const string gql = """
            mutation DismissMediaRequest($requestId: ID!) {
                dismissMediaRequest(requestId: $requestId) {
                    requestId
                }
            }
            """;

        return ExecuteAsync(gql, new { requestId }, cancellationToken);
    }
}
