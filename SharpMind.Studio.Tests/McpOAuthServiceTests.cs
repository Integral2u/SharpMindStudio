using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpOAuthServiceTests
{
    [Fact]
    public async Task SignIn_DiscoversRegistersAndStoresTheToken()
    {
        using var temp = new TempDirectory();
        var store = new McpOAuthStore(
            temp.Combine("mcp-oauth.json"),
            temp.Combine("mcp-oauth.key"));

        var handler = new StubHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://mcp.example/mcp")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer resource_metadata=\"https://auth.example/.well-known/oauth-protected-resource\"");
                return Task.FromResult(challenge);
            }

            if (url == "https://auth.example/.well-known/oauth-protected-resource")
            {
                return Json(new JsonObject
                {
                    ["authorization_servers"] = new JsonArray("https://auth.example"),
                });
            }

            if (url == "https://auth.example/.well-known/oauth-authorization-server")
            {
                return Json(new JsonObject
                {
                    ["authorization_endpoint"] = "https://auth.example/authorize",
                    ["token_endpoint"] = "https://auth.example/token",
                    ["registration_endpoint"] = "https://auth.example/register",
                    ["code_challenge_methods_supported"] = new JsonArray("S256"),
                });
            }

            if (url == "https://auth.example/register")
            {
                return Json(new JsonObject { ["client_id"] = "client-1" });
            }

            if (url == "https://auth.example/token")
            {
                return Json(new JsonObject
                {
                    ["access_token"] = "access-1",
                    ["refresh_token"] = "refresh-1",
                    ["expires_in"] = 3600,
                    ["token_type"] = "Bearer",
                    ["scope"] = "mcp:read",
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var browser = new FakeBrowser();
        var loopback = new FakeLoopback();
        var service = new McpOAuthService(
            new McpOAuthClient(new HttpClient(handler)), store, browser, loopback);

        McpOAuthSignInResult result = await service.SignInAsync(Server());

        Assert.True(result.SignedIn);
        Assert.NotNull(browser.Url);
        Assert.Contains("code_challenge_method=S256", browser.Url);
        Assert.Contains("client_id=client-1", browser.Url);

        string? state = QueryValue(browser.Url!, "state");
        Assert.Equal(loopback.ExpectedState, state);
        Assert.True(service.IsSignedIn("remote"));

        McpOAuthSession? session = store.Get("remote");
        Assert.NotNull(session);
        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("refresh-1", session.RefreshToken);
        Assert.Equal("client-1", session.ClientId);
        Assert.Equal("access-1", await service.GetAccessTokenAsync(Server()));

        service.SignOut("remote");
        Assert.False(service.IsSignedIn("remote"));
    }

    [Fact]
    public async Task GetAccessToken_RefreshesAnExpiredToken()
    {
        using var temp = new TempDirectory();
        var store = new McpOAuthStore(
            temp.Combine("mcp-oauth.json"),
            temp.Combine("mcp-oauth.key"));
        store.Save("remote", new McpOAuthSession(
            "old-access",
            "refresh-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "Bearer",
            "mcp:read",
            "client-1",
            null,
            null,
            "https://auth.example",
            "https://auth.example/token",
            "https://mcp.example/mcp"));

        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.ToString() == "https://auth.example/token")
            {
                return Json(new JsonObject
                {
                    ["access_token"] = "access-2",
                    ["expires_in"] = 3600,
                    ["token_type"] = "Bearer",
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var service = new McpOAuthService(
            new McpOAuthClient(new HttpClient(handler)),
            store,
            new FakeBrowser(),
            new FakeLoopback());

        string? token = await service.GetAccessTokenAsync(Server());

        Assert.Equal("access-2", token);
        Assert.Equal("access-2", store.Get("remote")!.AccessToken);
        Assert.Equal("refresh-1", store.Get("remote")!.RefreshToken);
    }

    [Fact]
    public async Task GetAccessToken_RequiresSignInWithoutAToken()
    {
        using var temp = new TempDirectory();
        var service = new McpOAuthService(
            new McpOAuthClient(new HttpClient(new StubHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))),
            new McpOAuthStore(
                temp.Combine("mcp-oauth.json"),
                temp.Combine("mcp-oauth.key")),
            new FakeBrowser(),
            new FakeLoopback());

        await Assert.ThrowsAsync<McpSignInRequiredException>(
            () => service.GetAccessTokenAsync(Server()));
    }

    [Fact]
    public async Task SignIn_WithoutRegistrationSupport_AsksForAClientId()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://mcp.example/mcp")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer resource_metadata=\"https://auth.example/.well-known/oauth-protected-resource\"");
                return Task.FromResult(challenge);
            }

            if (url == "https://auth.example/.well-known/oauth-protected-resource")
            {
                return Json(new JsonObject
                {
                    ["authorization_servers"] = new JsonArray("https://auth.example"),
                });
            }

            if (url == "https://auth.example/.well-known/oauth-authorization-server")
            {
                return Json(new JsonObject
                {
                    ["authorization_endpoint"] = "https://auth.example/authorize",
                    ["token_endpoint"] = "https://auth.example/token",
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var service = new McpOAuthService(
            new McpOAuthClient(new HttpClient(handler)),
            new McpOAuthStore(
                temp.Combine("mcp-oauth.json"),
                temp.Combine("mcp-oauth.key")),
            new FakeBrowser(),
            new FakeLoopback());

        McpOAuthException error = await Assert.ThrowsAsync<McpOAuthException>(
            () => service.SignInAsync(Server()));

        Assert.Contains("client ID", error.Message);
    }

    private static McpServerSettings Server() => new()
    {
        Id = "remote",
        Name = "Remote",
        Transport = McpTransports.Http,
        Url = "https://mcp.example/mcp",
        Auth = McpAuth.OAuth,
        OAuthScopes = "mcp:read",
    };

    private static string? QueryValue(string url, string name)
    {
        Uri uri = new(url);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    private static Task<HttpResponseMessage> Json(JsonObject body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json"),
        });

    private sealed class FakeBrowser : IMcpOAuthBrowser
    {
        public string? Url { get; private set; }

        public Task OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            Url = url;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLoopback : IMcpOAuthLoopback
    {
        public string RedirectUri => "http://127.0.0.1:33418/callback";

        public string? ExpectedState { get; private set; }

        public Task<string> WaitForCodeAsync(
            string expectedState,
            CancellationToken cancellationToken = default)
        {
            ExpectedState = expectedState;
            return Task.FromResult("code-1");
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request);
    }
}
