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
    private const int MaximumTargetKeyLength = 256;
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
}
