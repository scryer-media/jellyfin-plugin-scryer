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
    // Whitelist: only these embedded resources are servable through this route.
    private static readonly HashSet<string> ScriptFiles = new()
    {
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
    [HttpGet("config")]
    public ActionResult<object> GetConfig()
    {
        var config = Plugin.Instance?.Configuration;
        var imageBaseUrl = string.IsNullOrEmpty(config?.ScryerPublicBaseUrl)
            ? config?.ScryerApiBaseUrl ?? string.Empty
            : config.ScryerPublicBaseUrl;
        return Ok(new { imageBaseUrl });
    }
}
