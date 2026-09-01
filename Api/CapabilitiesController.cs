using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

/// <summary>Provides the authenticated user's Scryer capabilities to the plugin UI.</summary>
[ApiController]
[Authorize]
[ScryerFeature(ScryerFeature.Discovery, ScryerFeature.Calendar, ScryerFeature.Requests, ScryerFeature.Downloads)]
[Route("Scryer/Capabilities")]
[Produces(MediaTypeNames.Application.Json)]
public class CapabilitiesController : ControllerBase
{
    private readonly IScryerGraphqlService _graphql;

    public CapabilitiesController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        var result = await _graphql.GetCapabilitySnapshotAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { capabilities = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }
}
