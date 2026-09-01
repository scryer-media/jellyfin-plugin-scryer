using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Scryer.Api;

public enum ScryerFeature
{
    Discovery,
    Requests,
    Calendar,
    Downloads
}

/// <summary>Fails closed when every feature associated with an endpoint is disabled.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScryerFeatureAttribute : ActionFilterAttribute
{
    private readonly ScryerFeature[] _features;

    public ScryerFeatureAttribute(params ScryerFeature[] features)
    {
        _features = features.Length == 0
            ? throw new ArgumentException("At least one feature is required.", nameof(features))
            : features;
        Order = -3000;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = ScryerFailureHttpMapper.InvalidClientInput();
            return;
        }

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is not null && _features.Any(feature => feature switch
        {
            ScryerFeature.Discovery => configuration.EnableDiscovery,
            ScryerFeature.Requests => configuration.EnableRequests,
            ScryerFeature.Calendar => configuration.EnableCalendar,
            ScryerFeature.Downloads => configuration.EnableDownloads,
            _ => false
        }))
        {
            return;
        }

        context.Result = new NotFoundObjectResult(new
        {
            code = "feature_disabled",
            message = "This Scryer feature is disabled by the Jellyfin administrator."
        });
    }
}
