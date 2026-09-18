using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAligner;

/// <summary>Adds the optional subtitle-editor integration without modifying jellyfin-web on disk.</summary>
public sealed class ClientScriptInjectionFilter : IStartupFilter
{
    private const string Marker = "data-subtitle-aligner-client";
    private const string ScriptTag = "<script defer src=\"../SubtitleAligner/client.js?v=0.4.0.11\" data-subtitle-aligner-client></script>";
    private readonly ILogger<ClientScriptInjectionFilter> _logger;
    private int _logged;

    /// <summary>Initializes a new instance of the <see cref="ClientScriptInjectionFilter"/> class.</summary>
    public ClientScriptInjectionFilter(ILogger<ClientScriptInjectionFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private async Task InjectAsync(HttpContext context, Func<Task> next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsWebIndex(context.Request.Path.Value))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        Stream originalBody = context.Response.Body;
        await using MemoryStream bufferedBody = new();
        context.Response.Body = bufferedBody;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        bufferedBody.Position = 0;
        bool isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await bufferedBody.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        string html;
        using (StreamReader reader = new(bufferedBody, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        int closingBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closingBody >= 0 && !html.Contains(Marker, StringComparison.Ordinal))
        {
            html = html.Insert(closingBody, ScriptTag + Environment.NewLine);
            if (Interlocked.Exchange(ref _logged, 1) == 0)
            {
                _logger.LogInformation("Subtitle Aligner enabled its subtitle-editor button without changing jellyfin-web files");
            }
        }

        byte[] bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsWebIndex(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase));
    }
}
