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
[Route("Scryer/Libraries")]
[Produces(MediaTypeNames.Application.Json)]
public class LibrariesController : ControllerBase
{
    private readonly ScryerApiClient _client;

    public LibrariesController(ScryerApiClient client)
    {
        _client = client;
    }

    [HttpGet]
    public async Task<ActionResult<JsonElement>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false));
    }
}
