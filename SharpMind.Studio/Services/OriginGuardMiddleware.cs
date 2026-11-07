using Microsoft.Extensions.Primitives;

namespace SharpMind.Studio.Services;

/// <summary>
/// Rejects cross-site browser requests to the admin gateway and the Blazor
/// circuit. Those paths trust a loopback peer as an administrator, so a page on
/// another origin must not be able to reach them. Non-browser clients do not
/// send Fetch Metadata and are left alone; a browser that does not send it is
/// still rejected when it presents an opaque <c>Origin: null</c>.
///
/// This is not host filtering: a page served from an attacker-controlled domain
/// that DNS-rebinds to 127.0.0.1 is same-origin and is out of scope until host
/// restrictions are enabled.
/// </summary>
public sealed class OriginGuardMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly bool _enabled = configuration.GetValue("Studio:OriginGuard", true);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_enabled && IsGuardedPath(context.Request.Path) && IsCrossSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(
                "Cross-site request rejected.", context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static bool IsGuardedPath(PathString path) =>
        path.StartsWithSegments("/gateway", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase);

    private static bool IsCrossSite(HttpRequest request)
    {
        StringValues site = request.Headers["Sec-Fetch-Site"];
        if (!StringValues.IsNullOrEmpty(site))
        {
            string value = site.ToString();
            return !value.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        // No Fetch Metadata: only reject the unambiguous opaque origin. WebSocket
        // upgrades from modern browsers still carry Origin; a cross-site page
        // cannot obtain a Blazor connection token because negotiate is a fetch.
        return string.Equals(
            request.Headers.Origin.ToString(), "null", StringComparison.OrdinalIgnoreCase);
    }
}
