using Jellyfin.Plugin.Scryer.OAuth;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

/// <summary>Normalizes safe Scryer failures into the browser-facing HTTP contract.</summary>
public static class ScryerFailureHttpMapper
{
    public static ObjectResult InvalidClientInput()
    {
        return new ObjectResult(new ScryerFailureResponse("invalid_request", "The request contains invalid input."))
        {
            StatusCode = 400
        };
    }

    public static ObjectResult ToActionResult(ScryerFailure failure)
    {
        return new ObjectResult(ScryerFailureResponse.From(failure))
        {
            StatusCode = ToStatusCode(failure.Code)
        };
    }

    private static int ToStatusCode(ScryerFailureCode code) => code switch
    {
        ScryerFailureCode.NotConnected or ScryerFailureCode.AuthorizationExpired => 401,
        ScryerFailureCode.PermissionDenied => 403,
        ScryerFailureCode.RequestConflict => 409,
        ScryerFailureCode.RateLimited => 429,
        ScryerFailureCode.ScryerOffline or ScryerFailureCode.NotConfigured or ScryerFailureCode.ScryerIncompatible => 503,
        ScryerFailureCode.InvalidResponse => 502,
        _ => 500
    };
}

/// <summary>Safe, stable failure payload used by authenticated plugin endpoints.</summary>
public sealed record ScryerFailureResponse(string Code, string Message)
{
    public static ScryerFailureResponse From(ScryerFailure failure)
    {
        return new ScryerFailureResponse(failure.WireCode, SafeMessage(failure.Code));
    }

    private static string SafeMessage(ScryerFailureCode code) => code switch
    {
        ScryerFailureCode.NotConfigured => "Scryer has not been configured.",
        ScryerFailureCode.NotConnected => "Connect a Scryer account to continue.",
        ScryerFailureCode.AuthorizationExpired => "Your Scryer connection has expired. Reconnect to continue.",
        ScryerFailureCode.PermissionDenied => "You do not have permission to perform this action.",
        ScryerFailureCode.ScryerOffline => "Scryer is currently unreachable.",
        ScryerFailureCode.ScryerIncompatible => "The configured Scryer server is incompatible with this plugin.",
        ScryerFailureCode.RateLimited => "Scryer is rate limiting requests. Try again shortly.",
        ScryerFailureCode.InvalidResponse => "Scryer returned an invalid response.",
        ScryerFailureCode.RequestConflict => "The request conflicts with the current Scryer state.",
        _ => "The request could not be completed."
    };
}
