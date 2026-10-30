using System.Security.Claims;
using SharpMind.Studio.Core.Gateway;

namespace SharpMind.Studio.Services;

public sealed class GatewayAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, GatewayService gateway)
    {
        if (GatewayEndpoints.IsLoopback(context))
        {
            context.Items["GatewayAdmin"] = true;
            await next(context);
            return;
        }

        string path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (path.StartsWith("/gateway", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden", context.RequestAborted);
            return;
        }

        if (path is "/favicon.ico" or "/sharpmind-icon.svg" or "/sharpmind-logo.svg")
        {
            await next(context);
            return;
        }

        string? token = context.Request.Cookies[GatewayService.DeviceCookie];
        GatewayDevice? device = string.IsNullOrWhiteSpace(token) ? null : gateway.Authenticate(token);
        if (device is null)
        {
            if (context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/pair");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Pair this device first.", context.RequestAborted);
            }
            return;
        }

        gateway.Touch(device, context.Connection.RemoteIpAddress?.ToString());
        context.Items["GatewayDevice"] = device;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, device.Id),
            new Claim(ClaimTypes.Name, device.Name),
            new Claim("studio:mode", device.Mode),
            new Claim("studio:device", "true"),
        ], "StudioDevice"));

        await next(context);
    }
}



