using System;
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
[Route("Scryer/Calendar")]
[Produces(MediaTypeNames.Application.Json)]
public class CalendarController : ControllerBase
{
    private readonly ScryerApiClient _client;

    public CalendarController(ScryerApiClient client)
    {
        _client = client;
    }

    [HttpGet("Upcoming")]
    public async Task<ActionResult<object>> GetUpcoming(
        [FromQuery] int days,
        CancellationToken cancellationToken)
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = start.AddDays(days <= 0 ? 30 : days);
        var data = await _client.GetCalendarEpisodesAsync(start, end, cancellationToken).ConfigureAwait(false);

        var episodes = data.GetProperty("calendarEpisodes");
        var titleIds = episodes.EnumerateArray()
            .Select(e => e.GetProperty("titleId").GetString()!)
            .Distinct()
            .ToArray();

        var posters = await _client.GetTitlePostersAsync(titleIds, cancellationToken).ConfigureAwait(false);

        return Ok(new { calendarEpisodes = episodes, titlePosters = posters });
    }
}
