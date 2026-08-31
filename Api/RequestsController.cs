using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

// Thin passthrough onto Scryer's media-request GraphQL API; GetMine filters via RequestAttributionStore.
[ApiController]
[Authorize]
[Route("Scryer/Requests")]
[Produces(MediaTypeNames.Application.Json)]
public class RequestsController : ControllerBase
{
    private readonly ScryerApiClient _client;
    private readonly RequestAttributionStore _attribution;

    public RequestsController(
        ScryerApiClient client,
        RequestAttributionStore attribution)
    {
        _client = client;
        _attribution = attribution;
    }

    private string JellyfinUserId => User.Claims.First(c => c.Type == "Jellyfin-UserId").Value;

    [HttpGet("Mine")]
    public async Task<ActionResult<object>> GetMine(CancellationToken cancellationToken)
    {
        var userId = JellyfinUserId;

        var data = await _client.GetAllMediaRequestsAsync(cancellationToken).ConfigureAwait(false);
        var mine = data.GetProperty("mediaRequests")
            .EnumerateArray()
            .Where(r => _attribution.BelongsTo(r.GetProperty("id").GetString()!, userId))
            .ToArray();

        return Ok(new { myMediaRequests = mine });
    }

    [HttpGet]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<JsonElement>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _client.GetAllMediaRequestsAsync(cancellationToken).ConfigureAwait(false));
    }

    // TEMPORARY: Scryer's REQUEST library-permission check rejects the shared API key's
    // account even though `me` reports REQUEST granted on every library (server-side bug,
    // reported upstream). Until that's fixed, route requests straight through addTitle
    // (same as admin Add-to-Catalog) instead of submitMediaRequest/approve. This means every
    // request is added to the library immediately, with no admin approval step.
    [HttpPost]
    public async Task<ActionResult<JsonElement>> Create([FromBody] SubmitRequestDto dto, CancellationToken cancellationToken)
    {
        var input = new
        {
            name = dto.Title,
            facet = dto.Facet,
            libraryId = dto.LibraryId,
            monitored = true,
            tags = System.Array.Empty<string>(),
            externalIds = dto.ExternalIds,
            year = dto.Year,
            overview = dto.Overview
        };

        var result = await _client.AddTitleAsync(input, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    [HttpPost("{requestId}/approve")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<JsonElement>> Approve(
        [FromRoute] string requestId,
        [FromQuery] string qualityProfileId,
        CancellationToken cancellationToken)
    {
        return Ok(await _client.ApproveMediaRequestAsync(requestId, qualityProfileId, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("{requestId}/dismiss")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<JsonElement>> Dismiss([FromRoute] string requestId, CancellationToken cancellationToken)
    {
        return Ok(await _client.DismissMediaRequestAsync(requestId, cancellationToken).ConfigureAwait(false));
    }
}

public class SubmitRequestDto
{
    public string LibraryId { get; set; } = string.Empty;

    public string Facet { get; set; } = "MOVIE";

    public string Title { get; set; } = string.Empty;

    public ExternalIdDto[] ExternalIds { get; set; } = System.Array.Empty<ExternalIdDto>();

    public int? Year { get; set; }

    public string? Overview { get; set; }
}

public class ExternalIdDto
{
    public string Source { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
