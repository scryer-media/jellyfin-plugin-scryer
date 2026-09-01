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
[ScryerFeature(ScryerFeature.Discovery, ScryerFeature.Requests)]
[Route("Scryer/Catalog")]
[Produces(MediaTypeNames.Application.Json)]
public class CatalogController : ControllerBase
{
    private readonly IScryerGraphqlService _graphql;

    public CatalogController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet("QualityProfiles")]
    public async Task<ActionResult<JsonElement>> GetQualityProfiles(CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        var result = await _graphql.GetQualityProfilesAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { qualityProfileSettings = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }
}
