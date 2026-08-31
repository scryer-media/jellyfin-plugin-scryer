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
[Route("Scryer/Downloads")]
[Produces(MediaTypeNames.Application.Json)]
public class DownloadsController : ControllerBase
{
    private readonly ScryerApiClient _client;

    public DownloadsController(ScryerApiClient client)
    {
        _client = client;
    }

    [HttpGet]
    public async Task<ActionResult<JsonElement>> GetQueue(CancellationToken cancellationToken)
    {
        return Ok(await _client.GetDownloadQueueAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("History")]
    public async Task<ActionResult<JsonElement>> GetHistory(CancellationToken cancellationToken)
    {
        return Ok(await _client.GetDownloadHistoryAsync(cancellationToken).ConfigureAwait(false));
    }
}
