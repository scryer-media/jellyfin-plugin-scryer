using System;
using System.Collections.Generic;
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
[ScryerFeature(ScryerFeature.Calendar)]
[Route("Scryer/Calendar")]
[Produces(MediaTypeNames.Application.Json)]
public class CalendarController : ControllerBase
{
    private const int MaximumCalendarDays = 62;
    private readonly IScryerGraphqlService _graphql;

    public CalendarController(IScryerGraphqlService graphql)
    {
        _graphql = graphql;
    }

    [HttpGet("Upcoming")]
    public async Task<ActionResult<object>> GetUpcoming(
        [FromQuery] int? days,
        CancellationToken cancellationToken)
    {
        var requestedDays = days ?? 30;
        if (!TrustedJellyfinActor.TryGetUserId(User, out var jellyfinUserId)) return Unauthorized();
        if (requestedDays is < 1 or > MaximumCalendarDays)
        {
            return ScryerFailureHttpMapper.InvalidClientInput();
        }

        var start = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = start.AddDays(requestedDays);
        var result = await _graphql.GetCalendarEpisodesAsync(jellyfinUserId, start, end, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(new { calendarEpisodes = result.Value!, titlePosters = new Dictionary<string, string?>() })
            : ScryerFailureHttpMapper.ToActionResult(result.Failure!);
    }
}
