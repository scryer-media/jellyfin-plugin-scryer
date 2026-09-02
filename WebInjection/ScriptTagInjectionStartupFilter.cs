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

        var bytes = buffer.ToArray();

        try
        {
            var injection = HtmlScriptInjector.Inject(bytes);
            bytes = injection.Content;

            if (injection.Injected)
            {
                _status.RecordInjected();

                if (System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation("Scryer web assets injected via request-time middleware.");
                }
            }
            else if (injection.AlreadyPresent)
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

internal static class HtmlScriptInjector
{
    internal const string LoaderVersion = "153.6";
    private const string ScriptTag =
        "<script src=\"/Scryer/Web/scryer-loader.js?v=153.6\" data-scryer-loader=\"153.6\" defer></script>";

    private static readonly byte[] BodyClose = Encoding.ASCII.GetBytes("</body>");
    private static readonly byte[] LoaderPath = Encoding.ASCII.GetBytes("/Scryer/Web/scryer-loader.js");
    private static readonly byte[] LoaderMarker = Encoding.ASCII.GetBytes("data-scryer-loader=\"153.6\"");
    private static readonly byte[] ScriptBytes = Encoding.ASCII.GetBytes(ScriptTag + "\n");

    internal static (byte[] Content, bool Injected, bool AlreadyPresent) Inject(byte[] html)
    {
        var alreadyPresent = IndexOfAsciiIgnoreCase(html, LoaderMarker) >= 0
            || IndexOfAsciiIgnoreCase(html, LoaderPath) >= 0;
        if (alreadyPresent)
        {
            return (html, false, true);
        }

        var bodyClose = LastIndexOfAsciiIgnoreCase(html, BodyClose);
        if (bodyClose < 0)
        {
            return (html, false, false);
        }

        var result = new byte[html.Length + ScriptBytes.Length];
        Buffer.BlockCopy(html, 0, result, 0, bodyClose);
        Buffer.BlockCopy(ScriptBytes, 0, result, bodyClose, ScriptBytes.Length);
        Buffer.BlockCopy(html, bodyClose, result, bodyClose + ScriptBytes.Length, html.Length - bodyClose);
        return (result, true, false);
    }

    private static int IndexOfAsciiIgnoreCase(byte[] value, byte[] pattern)
    {
        for (var index = 0; index <= value.Length - pattern.Length; index++)
        {
            if (MatchesAsciiIgnoreCase(value, index, pattern))
            {
                return index;
            }
        }

        return -1;
    }

    private static int LastIndexOfAsciiIgnoreCase(byte[] value, byte[] pattern)
    {
        for (var index = value.Length - pattern.Length; index >= 0; index--)
        {
            if (MatchesAsciiIgnoreCase(value, index, pattern))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool MatchesAsciiIgnoreCase(byte[] value, int offset, byte[] pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (FoldAscii(value[offset + index]) != FoldAscii(pattern[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte FoldAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        ? (byte)(value + ('a' - 'A'))
        : value;
}
