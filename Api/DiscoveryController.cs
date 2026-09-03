using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

[ApiController]
[Authorize]
[ScryerFeature(ScryerFeature.Discovery)]
[Route("Scryer/Discovery")]
[Produces(MediaTypeNames.Application.Json)]
public class DiscoveryController : ControllerBase
{
    private const int MaximumSearchLength = 256;
    private const int MaximumRecommendationSeeds = 5;
    private const int MaximumTargetKeyLength = 256;
    private const int MaximumExternalIdLength = 64;
    private static readonly HashSet<string> RecommendationExternalIdSources = new(StringComparer.Ordinal)
    {
        "imdb", "tmdb", "tmdb_movie", "tmdb_series", "tmdb_show", "tmdb_tv",
        "tvdb", "tvdb_movie", "tvdb_series", "tvdb_show",
    };
    private readonly IScryerGraphqlService _graphql;

    public DiscoveryController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet("Trending")]
    public async Task<ActionResult<JsonElement>> GetTrending(CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        var result = await _graphql.GetDiscoveryHomeCardsAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { discoveryHomeCards = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpGet("MoreLikeThis")]
    public async Task<ActionResult<JsonElement>> GetMoreLikeThis(
        [FromQuery] string? source,
        [FromQuery] string? value,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var normalizedSource = source?.Trim().ToLowerInvariant();
        var normalizedValue = value?.Trim();
        var requestedLimit = limit ?? 20;
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (normalizedSource is null
            || !RecommendationExternalIdSources.Contains(normalizedSource)
            || string.IsNullOrWhiteSpace(normalizedValue)
            || normalizedValue.Length > MaximumExternalIdLength
            || requestedLimit is < 1 or > 30)
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var result = await _graphql.GetTitleRecommendationsAsync(jellyfinUserId, normalizedSource, normalizedValue, requestedLimit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { recommendationTitles = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpPost("Recommendations")]
    public async Task<IActionResult> GetRecommendations(
        [FromBody] RecommendationBatchDto? dto,
        CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!TryBuildRecommendationSeeds(dto, out var seeds)) return ScryerFailureHttpMapper.InvalidClientInput();

        var result = await _graphql.GetRecommendationGroupsAsync(jellyfinUserId, seeds, 20, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new
            {
                recommendationGroups = result.Value!.Select(group => new
                {
                    title = group.Title,
                    items = group.Items.Select(item => new
                    {
                        targetKey = item.TargetKey,
                        targetKind = item.TargetKind,
                        displayTitle = item.DisplayTitle,
                        year = item.Year,
                        posterUrl = item.PosterUrl,
                        overview = item.Overview,
                    })
                })
            })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpGet("Search")]
    public async Task<ActionResult<JsonElement>> Search(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var requestedLimit = limit ?? 25;
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length > MaximumSearchLength || requestedLimit is < 1 or > 50)
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var result = await _graphql.SearchMetadataMultiAsync(jellyfinUserId, q, requestedLimit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { searchMetadataMulti = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpGet("Item")]
    public async Task<ActionResult<JsonElement>> GetItemDetail(
        [FromQuery] string? targetKey,
        CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(targetKey) || targetKey.Trim().Length > MaximumTargetKeyLength)
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var result = await _graphql.GetDiscoveryItemDetailAsync(jellyfinUserId, targetKey, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { discoveryItemDetail = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    private static bool TryBuildRecommendationSeeds(RecommendationBatchDto? dto, out IReadOnlyList<ScryerRecommendationSeed> seeds)
    {
        seeds = System.Array.Empty<ScryerRecommendationSeed>();
        if (dto?.Seeds is null || dto.Seeds.Length is < 1 or > MaximumRecommendationSeeds)
        {
            return false;
        }

        var parsed = new List<ScryerRecommendationSeed>(dto.Seeds.Length);
        foreach (var seed in dto.Seeds)
        {
            var title = seed?.Title?.Trim();
            var facet = seed?.Kind?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(title) || title.Length > MaximumSearchLength || facet is not ("MOVIE" or "SERIES" or "ANIME") ||
                seed!.ProviderIds is null || seed.ProviderIds.Count is < 1 or > 3)
            {
                return false;
            }

            var providerIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in seed.ProviderIds)
            {
                var source = pair.Key?.Trim().ToLowerInvariant();
                var value = pair.Value?.Trim();
                if (source is not ("tmdb" or "tvdb" or "imdb") || string.IsNullOrEmpty(value) || value.Length > MaximumExternalIdLength ||
                    !providerIds.TryAdd(source, value))
                {
                    return false;
                }
            }

            parsed.Add(new ScryerRecommendationSeed(title, facet, providerIds));
        }

        seeds = parsed;
        return true;
    }
}

public sealed class RecommendationBatchDto
{
    public RecommendationSeedDto[] Seeds { get; set; } = System.Array.Empty<RecommendationSeedDto>();
}

public sealed class RecommendationSeedDto
{
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
