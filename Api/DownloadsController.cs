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
[ScryerFeature(ScryerFeature.Downloads)]
[Route("Scryer/Downloads")]
[Produces(MediaTypeNames.Application.Json)]
public class DownloadsController : ControllerBase
{
    private const int MaximumOffset = 10_000;
    private readonly IScryerGraphqlService _graphql;

    public DownloadsController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet]
    public async Task<ActionResult<JsonElement>> GetQueue([FromQuery] int? offset, CancellationToken cancellationToken)
    {
        var pageOffset = offset ?? 0;
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (pageOffset is < 0 or > MaximumOffset) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.GetDownloadQueuePageAsync(jellyfinUserId, pageOffset, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { downloadQueuePage = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpGet("History")]
    public async Task<ActionResult<JsonElement>> GetHistory([FromQuery] int? offset, CancellationToken cancellationToken)
    {
        var pageOffset = offset ?? 0;
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (pageOffset is < 0 or > MaximumOffset) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.GetDownloadHistoryPageAsync(jellyfinUserId, pageOffset, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { downloadHistory = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }
}
