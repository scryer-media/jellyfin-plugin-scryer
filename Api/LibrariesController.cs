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
[Route("Scryer/Libraries")]
[Produces(MediaTypeNames.Application.Json)]
public class LibrariesController : ControllerBase
{
    private readonly IScryerGraphqlService _graphql;

    public LibrariesController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet]
    public async Task<ActionResult<JsonElement>> GetAll([FromQuery] string? facet, CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (facet is not null && facet.Trim() is not ("MOVIE" or "SERIES" or "ANIME"))
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var result = await _graphql.GetRequestLibrariesAsync(jellyfinUserId, facet, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { libraries = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpGet("Manageable")]
    public async Task<ActionResult<JsonElement>> GetManageable([FromQuery] string? facet, CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (facet is not null && facet.Trim() is not ("MOVIE" or "SERIES" or "ANIME"))
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var result = await _graphql.GetManageableLibrariesAsync(jellyfinUserId, facet, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { libraries = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }
}
