using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Scryer.WebInjection;

// Injects <script> tags into jellyfin-web's index.html at request time. scryer-core.js must load first.
public class ScriptTagInjectionStartupFilter : IStartupFilter
{
    private const string ScriptTag =
        "<script src=\"/Scryer/Web/scryer-core.js\" defer></script>" +
        "<script src=\"/Scryer/Web/scryer-styles.js\" defer></script>" +
        "<script src=\"/Scryer/Web/scryer-discovery.js\" defer></script>" +
        "<script src=\"/Scryer/Web/scryer-calendar.js\" defer></script>" +
        "<script src=\"/Scryer/Web/scryer-requests.js\" defer></script>" +
        "<script src=\"/Scryer/Web/scryer-downloads.js\" defer></script>";

    private readonly ILogger<ScriptTagInjectionStartupFilter> _logger;
    private int _loggedOnce;

    public ScriptTagInjectionStartupFilter(ILogger<ScriptTagInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!IsIndexRequest(context.Request.Path.Value) || !HttpMethods.IsGet(context.Request.Method))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == 200
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            var alreadyInjected = html.IndexOf("Scryer/Web/nav.js", StringComparison.OrdinalIgnoreCase) >= 0;
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            if (!alreadyInjected && bodyClose >= 0)
            {
                html = html.Substring(0, bodyClose) + ScriptTag + "\n" + html.Substring(bodyClose);

                if (System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation("Scryer: injected the nav script tag via request-time middleware.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scryer script tag injection failed; serving original HTML.");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase));
    }
}
