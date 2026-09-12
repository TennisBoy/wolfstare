using System.Text;

namespace Wolfstare.Service.Api;

/// <summary>
/// Serves the single-page app and injects the API token into the page it hands out.
///
/// The token is anti-CSRF, not anti-user (spec §10.2): putting it in the served DOM is safe
/// because a cross-origin page cannot read this page's DOM, and <see cref="LocalApiGuard"/>
/// already rejects a cross-origin Origin. Any non-API route falls back to index.html so client
/// routing works.
/// </summary>
public static class SpaEndpoints
{
    public static void MapWolfstareSpa(this IEndpointRouteBuilder app, string token)
    {
        var indexPath = Path.Combine(app.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootPath ?? "wwwroot", "index.html");

        app.MapFallback(async context =>
        {
            // Never let the fallback answer an API path — those should 404 as JSON, not as the app.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!File.Exists(indexPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Wolfstare UI is not built. Run `npm run build` in web/.");
                return;
            }

            var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted);
            html = InjectToken(html, token);

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(html, Encoding.UTF8, context.RequestAborted);
        });
    }

    /// <summary>
    /// Sets the token in the placeholder meta tag, or inserts a fresh one before &lt;/head&gt;
    /// if a build reformatted the placeholder away. HTML-escapes the token defensively, though a
    /// base64 token needs no escaping.
    /// </summary>
    private static string InjectToken(string html, string token)
    {
        var escaped = System.Net.WebUtility.HtmlEncode(token);
        var tag = $"<meta name=\"wolfstare-token\" content=\"{escaped}\">";

        var start = html.IndexOf("<meta name=\"wolfstare-token\"", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = html.IndexOf('>', start);
            if (end > start) return html[..start] + tag + html[(end + 1)..];
        }

        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head >= 0 ? html[..head] + tag + html[head..] : html;
    }
}

