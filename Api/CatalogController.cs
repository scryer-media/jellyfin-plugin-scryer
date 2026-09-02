using System.Collections.Generic;
using System.Linq;
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
    private const int MaximumIdentifierLength = 128;
    private const int MaximumTitleLength = 256;
    private const int MaximumOverviewLength = 8192;
    private const int MaximumExternalIds = 20;
    private const int MinimumYear = 1800;
    private const int MaximumYear = 2100;
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

    [HttpPost("Titles")]
    public async Task<ActionResult<JsonElement>> AddTitle([FromBody] AddTitleDto? dto, CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!TryBuildAddInput(dto, out var input)) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.AddTitleAsync(jellyfinUserId, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { addTitle = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    private static bool TryBuildAddInput(AddTitleDto? dto, out JsonElement input)
    {
        input = default;
        if (dto is null || !IsIdentifier(dto.LibraryId) || !TryNormalizeFacet(dto.Facet, out var facet) ||
            string.IsNullOrWhiteSpace(dto.Title) || dto.Title.Trim().Length > MaximumTitleLength ||
            dto.ExternalIds is null || dto.ExternalIds.Length is < 1 or > MaximumExternalIds ||
            dto.Year is < MinimumYear or > MaximumYear || !IsOptionalText(dto.Overview, MaximumOverviewLength) ||
            !IsOptionalText(dto.SortTitle, MaximumTitleLength) || !IsOptionalText(dto.Slug, MaximumTitleLength) ||
            !IsOptionalText(dto.Language, 64) || !IsOptionalText(dto.ContentStatus, MaximumTitleLength) ||
            dto.RuntimeMinutes is < 0 or > 1440 ||
            (dto.QualityProfileId is not null && !IsIdentifier(dto.QualityProfileId)) ||
            !TryNormalizeMonitorType(dto.MonitorType, out var monitorType))
        {
            return false;
        }

        var externalIds = dto.ExternalIds
            .Select(externalId => new { source = externalId?.Source?.Trim(), value = externalId?.Value?.Trim() })
            .ToArray();
        if (externalIds.Any(externalId => string.IsNullOrWhiteSpace(externalId.source) || externalId.source.Length > 64 ||
            string.IsNullOrWhiteSpace(externalId.value) || externalId.value.Length > 256))
        {
            return false;
        }

        var options = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(dto.QualityProfileId)) options["qualityProfileId"] = dto.QualityProfileId.Trim();
        if (monitorType is not null) options["monitorType"] = monitorType;
        if (facet is not "MOVIE") options["useSeasonFolders"] = true;

        input = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["name"] = dto.Title.Trim(),
            ["facet"] = facet,
            ["libraryId"] = dto.LibraryId.Trim(),
            ["monitored"] = monitorType is not ("UNMONITORED" or "NONE"),
            ["tags"] = System.Array.Empty<string>(),
            ["options"] = options,
            ["externalIds"] = externalIds,
            ["year"] = dto.Year,
            ["overview"] = TrimOrNull(dto.Overview),
            ["sortTitle"] = TrimOrNull(dto.SortTitle),
            ["slug"] = TrimOrNull(dto.Slug),
            ["runtimeMinutes"] = dto.RuntimeMinutes,
            ["language"] = TrimOrNull(dto.Language),
            ["contentStatus"] = TrimOrNull(dto.ContentStatus),
        });
        return true;
    }

    private static bool IsIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaximumIdentifierLength;
    private static bool IsOptionalText(string? value, int maximumLength) => value is null || value.Trim().Length <= maximumLength;
    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryNormalizeFacet(string? facet, out string normalized)
    {
        normalized = facet?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized is "MOVIE" or "SERIES" or "ANIME";
    }

    private static bool TryNormalizeMonitorType(string? monitorType, out string? normalized)
    {
        normalized = TrimOrNull(monitorType)?.ToUpperInvariant();
        return normalized is null or "MONITORED" or "UNMONITORED" or "FUTURE_EPISODES" or "MISSING_AND_FUTURE_EPISODES" or "ALL_EPISODES" or "NONE";
    }
}

public class AddTitleDto
{
    public string LibraryId { get; set; } = string.Empty;
    public string Facet { get; set; } = "MOVIE";
    public string Title { get; set; } = string.Empty;
    public ExternalIdDto[] ExternalIds { get; set; } = System.Array.Empty<ExternalIdDto>();
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? SortTitle { get; set; }
    public string? Slug { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? Language { get; set; }
    public string? ContentStatus { get; set; }
    public string? QualityProfileId { get; set; }
    public string? MonitorType { get; set; }
}
