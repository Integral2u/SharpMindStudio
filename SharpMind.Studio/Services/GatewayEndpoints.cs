using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using QRCoder;
using SharpMind.Studio.Core.Gateway;

namespace SharpMind.Studio.Services;

public static class GatewayEndpoints
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/pair", async context =>
        {
            IWebHostEnvironment env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var file = env.WebRootFileProvider.GetFileInfo("gateway/pair.html");
            if (!file.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await using Stream stream = file.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });

        app.MapGet("/gateway", (HttpContext context) =>
            IsLoopback(context)
                ? Results.Redirect("/settings?tab=system")
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/gateway/api/network", (HttpContext context, GatewayService gateway) =>
            IsLoopback(context)
                ? Results.Json(new { addresses = gateway.LanAddresses() })
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapPost("/gateway/api/pairing/new", (HttpContext context, GatewayService gateway) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            PairingOffer offer = gateway.CreatePairing();
            return Results.Json(new
            {
                id = offer.Session.Id,
                token = offer.Token,
                pin = offer.Pin,
                expiresUtc = offer.Session.ExpiresUtc,
            });
        });

        app.MapGet("/gateway/api/pairings", (HttpContext context, GatewayService gateway) =>
            IsLoopback(context)
                ? Results.Json(gateway.Pairings()
                    .Where(value => value.Status == PairingStatuses.AwaitingApproval)
                    .Select(ToPublicPairing))
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/gateway/api/events", async (HttpContext context, GatewayService gateway) =>
        {
            if (!IsLoopback(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var reader = gateway.Subscribe(context.RequestAborted);
            await foreach (PairingSession session in reader.ReadAllAsync(context.RequestAborted))
            {
                string json = JsonSerializer.Serialize(ToPublicPairing(session));
                await context.Response.WriteAsync($"event: pairing\ndata: {json}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });

        app.MapPost("/gateway/api/pairings/{id}/approve", (HttpContext context, GatewayService gateway, string id, ApproveRequest request) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            PairingSession? session = gateway.Approve(id, request.Mode);
            return session is null ? Results.NotFound() : Results.Json(ToPublicPairing(session));
        });

        app.MapPost("/gateway/api/pairings/{id}/deny", (HttpContext context, GatewayService gateway, string id) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            PairingSession? session = gateway.Deny(id);
            return session is null ? Results.NotFound() : Results.Json(ToPublicPairing(session));
        });

        app.MapGet("/gateway/api/devices", (HttpContext context, GatewayService gateway) =>
            IsLoopback(context)
                ? Results.Json(gateway.Devices().Select(value => new
                {
                    value.Id,
                    value.Name,
                    value.Mode,
                    value.LastSeenUtc,
                    value.LastIp,
                    value.Revoked,
                }))
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapPost("/gateway/api/devices/{id}/revoke", (HttpContext context, GatewayService gateway, string id) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return gateway.Revoke(id) ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/gateway/api/devices/{id}/mode", (HttpContext context, GatewayService gateway, string id, ApproveRequest request) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return gateway.SetMode(id, request.Mode) ? Results.Ok() : Results.NotFound();
        });

        app.MapGet("/gateway/api/qr", (HttpContext context, string text) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var generator = new QRCodeGenerator();
            using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            var qr = new PngByteQRCode(data);
            return Results.File(qr.GetGraphic(8), "image/png");
        });

        app.MapPost("/pair/submit", (HttpContext context, GatewayService gateway, PairSubmit request) =>
        {
            string? ip = context.Connection.RemoteIpAddress?.ToString();
            string? ua = context.Request.Headers.UserAgent.ToString();
            PairingSession? session = gateway.SubmitPairing(
                request.Token ?? "", request.Pin ?? "", request.Name ?? "Phone", ua, ip);
            if (session is null) return Results.NotFound();
            return Results.Json(ToPublicPairing(session));
        });

        app.MapGet("/pair/status", (HttpContext context, GatewayService gateway, string token) =>
        {
            PairingSession? session = gateway.PairingStatus(token);
            if (session is null) return Results.NotFound();

            if (session.Status == PairingStatuses.Approved)
            {
                string? deviceToken = gateway.TakeApprovedToken(session.Id);
                if (deviceToken is not null)
                {
                    context.Response.Cookies.Append(GatewayService.DeviceCookie, deviceToken, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = context.Request.IsHttps,
                        Path = "/",
                        Expires = DateTimeOffset.UtcNow.AddDays(180),
                    });
                }
            }

            return Results.Json(ToPublicPairing(session));
        });

        return app;
    }

    public static bool IsLoopback(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private static object ToPublicPairing(PairingSession session) => new
    {
        session.Id,
        session.DeviceName,
        session.UserAgent,
        session.Ip,
        session.Status,
        session.ApprovedMode,
        session.Attempts,
        session.ExpiresUtc,
    };

    public sealed record ApproveRequest(string Mode);
    public sealed record PairSubmit(string? Token, string? Pin, string? Name);
}





