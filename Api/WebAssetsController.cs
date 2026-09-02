using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Scryer.Api;

[ApiController]
[AllowAnonymous]
[Route("Scryer/Web")]
public class WebAssetsController : ControllerBase
{
    // Allowlist: only these embedded resources are servable through this route.
    private static readonly HashSet<string> ScriptFiles = new()
    {
        "scryer-loader.js",
        "scryer-strings.js",
        "scryer-core.js",
        "scryer-styles.js",
        "scryer-discovery.js",
        "scryer-calendar.js",
        "scryer-requests.js",
        "scryer-downloads.js"
    };

    [HttpGet("{fileName}.js")]
    public ActionResult GetScript(string fileName)
    {
        var name = $"{fileName}.js";
        if (!ScriptFiles.Contains(name))
        {
            return NotFound();
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{GetType().Namespace!.Replace(".Api", string.Empty)}.Web.{name}";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }
        Response.Headers.CacheControl = "no-store";
        return File(stream, "application/javascript");
    }

    [HttpGet("scryer-logo.svg")]
    public ActionResult GetScryerBrand()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.Scryer.Web.scryer-logo.svg");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public,max-age=86400";
        return File(stream, "image/svg+xml");
    }

    [HttpGet("config")]
    public ActionResult<object> GetConfig()
    {
        var config = Plugin.Instance?.Configuration;
        var imageBaseUrl = config?.ScryerPublicBaseUrl ?? string.Empty;
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            imageBaseUrl,
            features = new
            {
                discovery = config?.EnableDiscovery ?? false,
                requests = config?.EnableRequests ?? false,
                calendar = config?.EnableCalendar ?? false,
                downloads = config?.EnableDownloads ?? false
            }
        });
    }
}
