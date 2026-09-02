using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Scryer.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

/// <summary>Bound, per-Jellyfin-user OAuth endpoints. No browser-supplied identity is accepted.</summary>
[ApiController]
[Authorize]
[Route("Scryer/Auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IScryerOAuthFlowService _flowService;

    public AuthController(IScryerOAuthFlowService flowService)
    {
        _flowService = flowService ?? throw new ArgumentNullException(nameof(flowService));
    }

    [HttpPost("Start")]
    public async Task<IActionResult> Start(
        [FromBody] ScryerOAuthStartRequest? request,
        CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!TryGetTrustedJellyfinUserId(out var jellyfinUserId))
        {
            return Unauthorized();
        }

        var started = await _flowService.StartAsync(jellyfinUserId, request?.ReturnPage, cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return Failure(started.Failure!);
        }

        var flow = started.Value!;
        Response.Cookies.Append(flow.CookieName, flow.CookieValue, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = flow.CookieSecure,
            Path = flow.CookiePath,
            Expires = flow.ExpiresAt
        });
        return Ok(new ScryerOAuthStartDto(flow.AuthorizationUri.AbsoluteUri));
    }

    [AllowAnonymous]
    [HttpGet("Callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? state,
        [FromQuery(Name = "code")] string? authorizationCode,
        [FromQuery(Name = "error")] string? authorizationError,
        CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        Response.Headers["Referrer-Policy"] = "no-referrer";
        ScryerOAuthCallbackCookie? callbackCookie = null;
        string? protectedCookie = null;
        if (_flowService.TryGetCallbackCookie(state, out var resolvedCookie))
        {
            callbackCookie = resolvedCookie;
            Request.Cookies.TryGetValue(callbackCookie.Name, out protectedCookie);
        }

        var staged = await _flowService.StageCallbackAsync(
            state,
            protectedCookie,
            authorizationCode,
            authorizationError,
            cancellationToken).ConfigureAwait(false);

        if (callbackCookie is not null)
        {
            Response.Cookies.Delete(callbackCookie.Name, new CookieOptions { Path = callbackCookie.Path });
        }

        if (staged.Success)
        {
            Response.Cookies.Append(staged.FinalizeCookieName!, staged.FinalizeCookieValue!, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = staged.FinalizeCookieSecure,
                Path = staged.FinalizeCookiePath,
                Expires = staged.ExpiresAt
            });
            if (staged.RedirectUri is not null) return Redirect(staged.RedirectUri.AbsoluteUri);
        }

        // If configuration was changed during the round trip, stay on the current trusted host
        // and use only a fixed local page. The failure is available from Status.
        var basePath = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
        return LocalRedirect(basePath + "/web/index.html#/scryer-discovery");
    }

    [HttpPost("Finalize")]
    public async Task<IActionResult> Finalize(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!TryGetTrustedJellyfinUserId(out var jellyfinUserId)) return Unauthorized();
        var cookies = Request.Cookies.Where(pair => pair.Key.StartsWith("scryer_oauth_finalize_", StringComparison.Ordinal) && pair.Key.Length <= 64).Take(3).ToArray();
        if (cookies.Length == 0) return NoContent();
        ScryerResult<bool>? result = null;
        foreach (var cookie in cookies)
        {
            result = await _flowService.FinalizeAsync(jellyfinUserId, cookie.Value, cancellationToken).ConfigureAwait(false);
            Response.Cookies.Delete(cookie.Key, new CookieOptions { Path = "/" });
            if (result.IsSuccess)
            {
                var status = await _flowService.GetStatusAsync(jellyfinUserId, CancellationToken.None).ConfigureAwait(false);
                return Ok(status);
            }
        }
        return Failure(result?.Failure ?? ScryerFailure.AuthorizationExpired);
    }

    [HttpGet("Status")]
    public async Task<ActionResult<ScryerAuthStatusDto>> Status(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!TryGetTrustedJellyfinUserId(out var jellyfinUserId))
        {
            return Unauthorized();
        }

        return Ok(await _flowService.GetStatusAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!TryGetTrustedJellyfinUserId(out var jellyfinUserId))
        {
            return Unauthorized();
        }

        var disconnected = await _flowService.DisconnectAsync(jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (!disconnected.IsSuccess)
        {
            return Failure(disconnected.Failure!);
        }

        return Ok(new ScryerAuthStatusDto(true, false, false, null));
    }

    private IActionResult Failure(ScryerFailure failure) => ScryerFailureHttpMapper.ToActionResult(failure);

    private void SetNoStoreHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Pragma"] = "no-cache";
    }

    private bool TryGetTrustedJellyfinUserId(out string jellyfinUserId) =>
        TrustedJellyfinActor.TryGetUserId(User, out jellyfinUserId);
}

/// <summary>Bounded browser input for an authenticated OAuth-start POST.</summary>
public sealed record ScryerOAuthStartRequest(string? ReturnPage);
