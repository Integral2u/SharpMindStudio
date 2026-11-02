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

        if (!gateway.LanAllowed)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
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

        if (path is "/favicon.ico" or "/sharpmind-icon.svg" or "/sharpmind-logo.svg" or "/health")
        {
            await next(context);
            return;
        }

        // A gateway in proxy mode signs each request. The upstream Studio trusts
        // that short-lived claim instead of holding the phone's device token.
        if (!gateway.ProxyActive &&
            GatewayClaims.TryValidate(
                gateway.GatewaySecret,
                context.Request.Headers[GatewayClaims.HeaderName],
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                DateTimeOffset.UtcNow,
                out GatewayClaim? claim) &&
            claim is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, claim.DeviceId),
                new Claim(ClaimTypes.Name, "Paired device"),
                new Claim("studio:mode", claim.Mode),
                new Claim("studio:device", "true"),
                new Claim("studio:gateway", "true"),
            ], "StudioGateway"));

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



