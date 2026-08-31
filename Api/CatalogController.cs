using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

// Admin-only: bypasses the request/approve workflow, adding the title monitored directly.
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Scryer/Catalog")]
[Produces(MediaTypeNames.Application.Json)]
public class CatalogController : ControllerBase
{
    private readonly ScryerApiClient _client;

    public CatalogController(ScryerApiClient client)
    {
        _client = client;
    }

    [HttpGet("QualityProfiles")]
    public async Task<ActionResult<JsonElement>> GetQualityProfiles(CancellationToken cancellationToken)
    {
        return Ok(await _client.GetQualityProfilesAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Add")]
    public async Task<ActionResult<JsonElement>> Add([FromBody] AddTitleDto dto, CancellationToken cancellationToken)
    {
        var input = new
        {
            name = dto.Name,
            facet = dto.Facet,
            libraryId = dto.LibraryId,
            monitored = dto.Monitored,
            tags = System.Array.Empty<string>(),
            externalIds = dto.ExternalIds,
            year = dto.Year,
            overview = dto.Overview,
            options = new
            {
                qualityProfileId = dto.QualityProfileId,
                rootFolderId = dto.RootFolderId
            }
        };

        return Ok(await _client.AddTitleAsync(input, cancellationToken).ConfigureAwait(false));
    }
}

public class AddTitleDto
{
    public string Name { get; set; } = string.Empty;

    public string Facet { get; set; } = "MOVIE";

    public string LibraryId { get; set; } = string.Empty;

    public bool Monitored { get; set; } = true;

    public ExternalIdDto[] ExternalIds { get; set; } = System.Array.Empty<ExternalIdDto>();

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public string? QualityProfileId { get; set; }

    public string? RootFolderId { get; set; }
}

// ExternalIdDto is defined in RequestsController.cs (same namespace).
