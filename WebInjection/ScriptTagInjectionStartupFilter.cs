using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Scryer.WebInjection;

// Injects versioned plugin assets into jellyfin-web's index.html at request time.
public class ScriptTagInjectionStartupFilter : IStartupFilter
{
    private const string ScriptTag =
        "<script src=\"/Scryer/Web/scryer-loader.js?v=153.5\" data-scryer-loader=\"153.5\" defer></script>";

    private readonly ILogger<ScriptTagInjectionStartupFilter> _logger;
    private readonly ScryerInjectionStatus _status;
    private int _loggedOnce;

    public ScriptTagInjectionStartupFilter(
        ILogger<ScriptTagInjectionStartupFilter> logger,
        ScryerInjectionStatus status)
    {
        _logger = logger;
        _status = status;
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

        _status.RecordIndexRequest();

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
            var alreadyInjected = html.IndexOf("data-scryer-loader=\"153.5\"", StringComparison.OrdinalIgnoreCase) >= 0
                || html.IndexOf("/Scryer/Web/scryer-loader.js", StringComparison.OrdinalIgnoreCase) >= 0;
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            if (!alreadyInjected && bodyClose >= 0)
            {
                html = html.Substring(0, bodyClose) + ScriptTag + "\n" + html.Substring(bodyClose);
                _status.RecordInjected();

                if (System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation("Scryer web assets injected via request-time middleware.");
                }
            }
            else if (alreadyInjected)
            {
                _status.RecordAlreadyPresent();
            }
            else
            {
                var exception = new InvalidOperationException("Jellyfin web shell did not contain a closing body element.");
                _status.RecordFailure(exception);
                _logger.LogWarning(exception, "Scryer web asset injection skipped; serving original HTML.");
            }
        }
        catch (Exception ex)
        {
            _status.RecordFailure(ex);
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
