using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The admin gateway and the Blazor circuit treat loopback as admin, so a
/// cross-site page must not reach them. Fetch Metadata is the browser's own
/// attestation and cannot be set by page JavaScript.
/// </summary>
public sealed class OriginGuardTests
{
    [Fact]
    public async Task CrossSiteGatewayRequest_IsRejected()
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context("/gateway/api/settings", site: "cross-site");

        await middleware.InvokeAsync(context);

        Assert.False(called());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/_blazor")]
    [InlineData("/_blazor/negotiate")]
    public async Task CrossSiteBlazorRequest_IsRejected(string path)
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context(path, site: "cross-site");

        await middleware.InvokeAsync(context);

        Assert.False(called());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task SameOriginAndDirectRequests_Pass(string site)
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context("/gateway/api/settings", site: site);

        await middleware.InvokeAsync(context);

        Assert.True(called());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonBrowserRequestWithoutFetchMetadata_Passes()
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context("/gateway/api/settings");

        await middleware.InvokeAsync(context);

        Assert.True(called());
    }

    [Fact]
    public async Task OpaqueOriginWithoutFetchMetadata_IsRejected()
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context("/gateway/api/settings", origin: "null");

        await middleware.InvokeAsync(context);

        Assert.False(called());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task SameSiteSubdomain_IsRejected()
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context("/gateway/api/settings", site: "same-site");

        await middleware.InvokeAsync(context);

        Assert.False(called());
    }

    [Fact]
    public async Task UnrelatedPath_IsNotGuarded()
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create();
        DefaultHttpContext context = Context("/chat", site: "cross-site");

        await middleware.InvokeAsync(context);

        Assert.True(called());
    }

    [Fact]
    public async Task GuardCanBeDisabledForUnusualClients()
    {
        (OriginGuardMiddleware middleware, Func<bool> called) = Create(enabled: false);
        DefaultHttpContext context = Context("/gateway/api/settings", site: "cross-site");

        await middleware.InvokeAsync(context);

        Assert.True(called());
    }

    private static (OriginGuardMiddleware Middleware, Func<bool> Called) Create(bool enabled = true)
    {
        bool called = false;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:OriginGuard"] = enabled ? "true" : "false",
            })
            .Build();

        var middleware = new OriginGuardMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            configuration);

        return (middleware, () => called);
    }

    private static DefaultHttpContext Context(
        string path,
        string? site = null,
        string? origin = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (site is not null) context.Request.Headers["Sec-Fetch-Site"] = site;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context;
    }
}
