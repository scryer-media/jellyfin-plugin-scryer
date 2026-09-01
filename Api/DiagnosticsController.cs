using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Scryer/Diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly ScryerConnectionDiagnostics _diagnostics;

    public DiagnosticsController(ScryerConnectionDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
    }

    [HttpGet]
    public async Task<ActionResult<ScryerDiagnosticsSnapshot>> Get(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _diagnostics.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));
    }
}
