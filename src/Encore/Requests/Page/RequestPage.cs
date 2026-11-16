using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Encore.Requests.Page;

/// <summary>
/// The request page (design 10.1): <c>GET /request</c>, <c>/request/app.js</c>, <c>/request/app.css</c>, served from resources
/// embedded in the assembly (no build step, no static-file middleware). Security headers (CSP, nosniff, no-referrer, HSTS) are
/// added for every <c>/request*</c> path by the API host's middleware; the page itself only sets the content type and cache lifetime.
/// The HTML uses relative asset URLs (<c>request/app.js?v=hash</c>; the asset routes ignore the query), so it works under any path base a proxy adds or strips.
/// </summary>
public static class RequestPage
{
    private const string CacheControl = "public, max-age=300";

    private static readonly Lazy<byte[]> Script = new(() => Load("app.js"));
    private static readonly Lazy<byte[]> Style = new(() => Load("app.css"));
    // The HTML carries ?v=<content hash> on both assets (computed once), so a browser or CDN that keeps an old copy
    // longer than max-age (Cloudflare's Browser Cache TTL does) fetches the new file after a deploy.
    private static readonly Lazy<byte[]> Html = new(() => Version(Load("index.html"), Script.Value, Style.Value));

    /// <summary>A short content hash (first 8 bytes of SHA-256, lower-case hex) used as the asset cache-busting version.</summary>
    public static string ContentHash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content).AsSpan(0, 8)).ToLowerInvariant();

    /// <summary>Fills the <c>{{js}}</c> and <c>{{css}}</c> placeholders of the page HTML with the assets' content hashes.</summary>
    public static byte[] Version(byte[] html, byte[] script, byte[] style) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(html)
            .Replace("{{js}}", ContentHash(script), StringComparison.Ordinal)
            .Replace("{{css}}", ContentHash(style), StringComparison.Ordinal));

    public static IEndpointRouteBuilder MapRequestPage(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        string[] methods = ["GET", "HEAD"];
        routes.MapMethods("/request", methods, (HttpContext context) => Page(context));
        routes.MapMethods("/request/app.js", methods, (HttpContext context) => Serve(context, Script.Value, "text/javascript; charset=utf-8"));
        routes.MapMethods("/request/app.css", methods, (HttpContext context) => Serve(context, Style.Value, "text/css; charset=utf-8"));
        return routes;
    }

    private static IResult Page(HttpContext context)
    {
        // "/request/" would make the relative asset URLs resolve to /request/request/...; send it to "/request" (keeping ?q=).
        if (context.Request.Path.Value is { } path && path.EndsWith('/'))
        {
            return Results.Redirect("../request" + context.Request.QueryString.Value, permanent: false);
        }

        return Serve(context, Html.Value, "text/html; charset=utf-8");
    }

    private static IResult Serve(HttpContext context, byte[] body, string contentType)
    {
        context.Response.Headers[HeaderNames.CacheControl] = CacheControl;
        return Results.Bytes(body, contentType);
    }

    private static byte[] Load(string name)
    {
        var assembly = typeof(RequestPage).Assembly;
        using var stream = assembly.GetManifestResourceStream("Encore.Page." + name)
            ?? throw new InvalidOperationException("Embedded page resource missing: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
