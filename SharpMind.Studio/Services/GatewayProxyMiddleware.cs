using SharpMind.Studio.Core.Gateway;
using Yarp.ReverseProxy.Forwarder;

namespace SharpMind.Studio.Services;

/// <summary>
/// In proxy mode, paired LAN requests are forwarded to an upstream Studio with
/// a short-lived signed device claim. Loopback admin and the local pairing
/// pages stay on this machine.
/// </summary>
public sealed class GatewayProxyMiddleware(
    RequestDelegate next,
    IHttpForwarder forwarder,
    GatewayService gateway,
    GatewayRateLimiter limiter,
    ILogger<GatewayProxyMiddleware> logger,
    HttpMessageInvoker upstreamClient)
{
    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(5),
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!gateway.ProxyActive || GatewayEndpoints.IsLoopback(context))
        {
            await next(context);
            return;
        }

        string path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/pair", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/gateway", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (context.Items["GatewayDevice"] is not GatewayDevice device)
        {
            await next(context);
            return;
        }

        string key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.TryAcquire(key, out TimeSpan retryAfter))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Too many requests. Try again shortly.", context.RequestAborted);
            return;
        }

        string? secret = gateway.GatewaySecret;
        Uri? upstream = gateway.UpstreamUri;
        if (string.IsNullOrWhiteSpace(secret) || upstream is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Remote studio is not configured.", context.RequestAborted);
            return;
        }

        string pathAndQuery = context.Request.Path + context.Request.QueryString;
        context.Request.Headers.Remove(GatewayClaims.HeaderName);
        context.Request.Headers[GatewayClaims.HeaderName] = GatewayClaims.Sign(
            secret,
            device.Id,
            device.Mode,
            context.Request.Method,
            pathAndQuery,
            DateTimeOffset.UtcNow);

        string? authorization = gateway.UpstreamAuthorization;
        if (!string.IsNullOrWhiteSpace(authorization))
            context.Request.Headers.Authorization = $"Bearer {authorization}";

        string destination = upstream.ToString().TrimEnd('/');
        ForwarderError error = await forwarder.SendAsync(
            context,
            destination,
            upstreamClient,
            RequestConfig,
            HttpTransformer.Default);

        if (error == ForwarderError.None) return;
        if (context.Response.HasStarted) return;

        logger.LogWarning("Upstream proxy failed for {Path}: {Error}", pathAndQuery, error);
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync($"Remote studio did not answer ({error}).", context.RequestAborted);
    }
}
