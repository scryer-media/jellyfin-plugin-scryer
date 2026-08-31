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
[Route("Scryer/Discovery")]
[Produces(MediaTypeNames.Application.Json)]
public class DiscoveryController : ControllerBase
{
    private readonly ScryerApiClient _client;

    public DiscoveryController(ScryerApiClient client)
    {
        _client = client;
    }

    [HttpGet("Trending")]
    public async Task<ActionResult<JsonElement>> GetTrending(CancellationToken cancellationToken)
    {
        return Ok(await _client.GetDiscoveryHomeCardsAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Search")]
    public async Task<ActionResult<JsonElement>> Search(
        [FromQuery] string q,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        return Ok(await _client.SearchMetadataMultiAsync(q, limit == 0 ? 25 : limit, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Item")]
    public async Task<ActionResult<JsonElement>> GetItemDetail(
        [FromQuery] string targetKey,
        CancellationToken cancellationToken)
    {
        return Ok(await _client.GetDiscoveryItemDetailAsync(targetKey, cancellationToken).ConfigureAwait(false));
    }
}
