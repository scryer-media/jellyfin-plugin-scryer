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
[ScryerFeature(ScryerFeature.Requests)]
[Route("Scryer/Requests")]
[Produces(MediaTypeNames.Application.Json)]
public class RequestsController : ControllerBase
{
    private const int MaximumIdentifierLength = 128;
    private const int MaximumTitleLength = 256;
    private const int MaximumOverviewLength = 8192;
    private const int MaximumExternalIds = 20;
    private const int MinimumYear = 1800;
    private const int MaximumYear = 2100;
    private readonly IScryerGraphqlService _graphql;

    public RequestsController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet("Mine")]
    public async Task<ActionResult<object>> GetMine(CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        var result = await _graphql.GetMyMediaRequestsAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { myMediaRequests = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpGet]
    public async Task<ActionResult<JsonElement>> GetAll(CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        var result = await _graphql.GetManageableMediaRequestsAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { mediaRequests = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpPost]
    public async Task<ActionResult<JsonElement>> Create([FromBody] SubmitRequestDto? dto, CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!TryBuildSubmitInput(dto, out var input)) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.SubmitMediaRequestAsync(jellyfinUserId, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { submitMediaRequest = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpPost("{requestId}/approve")]
    public async Task<ActionResult<JsonElement>> Approve(
        [FromRoute] string? requestId,
        [FromQuery] string? qualityProfileId,
        [FromQuery] string? monitorType,
        CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!IsIdentifier(requestId) || !IsIdentifier(qualityProfileId) || !TryNormalizeMonitorType(monitorType, out var normalizedMonitorType)) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.ApproveMediaRequestAsync(jellyfinUserId, requestId!, qualityProfileId!, normalizedMonitorType, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { approveMediaRequest = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpPost("{requestId}/dismiss")]
    public async Task<ActionResult<JsonElement>> Dismiss([FromRoute] string? requestId, CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!IsIdentifier(requestId)) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.DismissMediaRequestAsync(jellyfinUserId, requestId!, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { dismissMediaRequest = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpPut("{requestId}")]
    public async Task<ActionResult<JsonElement>> UpdateMine(
        [FromRoute] string? requestId,
        [FromBody] UpdateRequestDto? dto,
        CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!IsIdentifier(requestId) || dto is null || !IsIdentifier(dto.RequestedQualityProfileId) || !TryNormalizeMonitorType(dto.RequestedMonitorType, out var requestedMonitorType))
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var input = JsonSerializer.SerializeToElement(new
        {
            requestId = requestId!.Trim(),
            requestedQualityProfileId = dto.RequestedQualityProfileId.Trim(),
            requestedMonitorType
        });
        var result = await _graphql.UpdateMyMediaRequestAsync(jellyfinUserId, input, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { updateMyMediaRequest = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    [HttpPost("{requestId}/cancel")]
    public async Task<ActionResult<JsonElement>> CancelMine([FromRoute] string? requestId, CancellationToken cancellationToken)
    {
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (!IsIdentifier(requestId)) return ScryerFailureHttpMapper.InvalidClientInput();
        var result = await _graphql.CancelMyMediaRequestAsync(jellyfinUserId, requestId!, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { cancelMyMediaRequest = result.Value! })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }

    private static bool TryBuildSubmitInput(SubmitRequestDto? dto, out JsonElement input)
    {
        input = default;
        if (dto is null || !IsIdentifier(dto.LibraryId) || !TryNormalizeFacet(dto.Facet, out var facet) ||
            string.IsNullOrWhiteSpace(dto.Title) || dto.Title.Trim().Length > MaximumTitleLength ||
            dto.ExternalIds is null || dto.ExternalIds.Length is < 1 or > MaximumExternalIds ||
            dto.Year is < MinimumYear or > MaximumYear || !IsOptionalText(dto.Overview, MaximumOverviewLength) ||
            !IsOptionalText(dto.SortTitle, MaximumTitleLength) || !IsOptionalText(dto.Slug, MaximumTitleLength) ||
            !IsOptionalText(dto.Language, 64) || !IsOptionalText(dto.ContentStatus, MaximumTitleLength) ||
            dto.RuntimeMinutes is < 0 or > 1440 ||
            (dto.RequestedQualityProfileId is not null && !IsIdentifier(dto.RequestedQualityProfileId)) ||
            !TryNormalizeMonitorType(dto.RequestedMonitorType, out var requestedMonitorType))
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

        input = JsonSerializer.SerializeToElement(new
        {
            libraryId = dto.LibraryId.Trim(),
            facet,
            title = dto.Title.Trim(),
            externalIds,
            year = dto.Year,
            overview = TrimOrNull(dto.Overview),
            sortTitle = TrimOrNull(dto.SortTitle),
            slug = TrimOrNull(dto.Slug),
            runtimeMinutes = dto.RuntimeMinutes,
            language = TrimOrNull(dto.Language),
            contentStatus = TrimOrNull(dto.ContentStatus),
            requestedQualityProfileId = TrimOrNull(dto.RequestedQualityProfileId),
            requestedMonitorType
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

public class SubmitRequestDto
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

    public string? RequestedQualityProfileId { get; set; }

    public string? RequestedMonitorType { get; set; }
}

public class UpdateRequestDto
{
    public string RequestedQualityProfileId { get; set; } = string.Empty;

    public string? RequestedMonitorType { get; set; }
}

public class ExternalIdDto
{
    public string Source { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
