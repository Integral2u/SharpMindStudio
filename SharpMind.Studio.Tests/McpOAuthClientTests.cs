using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpOAuthClientTests
{
    [Fact]
    public async Task Discover_FollowsTheProtectedResourceChallenge()
    {
        var handler = new StubHandler(async request =>
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://mcp.example/mcp")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer resource_metadata=\"https://auth.example/.well-known/oauth-protected-resource\"");
                return challenge;
            }

            if (url == "https://auth.example/.well-known/oauth-protected-resource")
            {
                return Json(new JsonObject
                {
                    ["resource"] = "https://mcp.example/mcp",
                    ["authorization_servers"] = new JsonArray("https://auth.example"),
                    ["scopes_supported"] = new JsonArray("mcp:read"),
                });
            }

            if (url == "https://auth.example/.well-known/oauth-authorization-server")
            {
                return Json(new JsonObject
                {
                    ["issuer"] = "https://auth.example",
                    ["authorization_endpoint"] = "https://auth.example/authorize",
                    ["token_endpoint"] = "https://auth.example/token",
                    ["registration_endpoint"] = "https://auth.example/register",
                    ["scopes_supported"] = new JsonArray("mcp:read"),
                    ["code_challenge_methods_supported"] = new JsonArray("S256"),
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new McpOAuthClient(new HttpClient(handler));
        McpOAuthDiscovery discovery = await client.DiscoverAsync("https://mcp.example/mcp");

        Assert.Equal("https://mcp.example/mcp", discovery.Resource);
        Assert.Equal("https://auth.example", discovery.AuthorizationServer);
        Assert.Equal("https://auth.example/authorize", discovery.AuthorizationEndpoint);
        Assert.Equal("https://auth.example/token", discovery.TokenEndpoint);
        Assert.Equal("https://auth.example/register", discovery.RegistrationEndpoint);
        Assert.Contains("mcp:read", discovery.ScopesSupported);
    }

    [Fact]
    public async Task Discover_FallsBackToWellKnownMetadata()
    {
        var handler = new StubHandler(async request =>
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://mcp.example/mcp")
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            if (url == "https://mcp.example/.well-known/oauth-protected-resource")
            {
                return Json(new JsonObject
                {
                    ["authorization_servers"] = new JsonArray("https://mcp.example"),
                });
            }

            if (url == "https://mcp.example/.well-known/openid-configuration")
            {
                return Json(new JsonObject
                {
                    ["authorization_endpoint"] = "https://mcp.example/authorize",
                    ["token_endpoint"] = "https://mcp.example/token",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new McpOAuthClient(new HttpClient(handler));
        McpOAuthDiscovery discovery = await client.DiscoverAsync("https://mcp.example/mcp");

        Assert.Equal("https://mcp.example/authorize", discovery.AuthorizationEndpoint);
        Assert.Equal("https://mcp.example/token", discovery.TokenEndpoint);
    }

    [Fact]
    public async Task Register_SendsAPublicClientAndKeepsTheCredentials()
    {
        JsonObject? sent = null;
        var handler = new StubHandler(async request =>
        {
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return Json(new JsonObject
            {
                ["client_id"] = "client-1",
                ["client_secret"] = "secret-1",
            });
        });

        var client = new McpOAuthClient(new HttpClient(handler));
        McpOAuthClientRegistration registration = await client.RegisterAsync(
            Discovery(),
            "http://127.0.0.1:33418/callback",
            "mcp:read");

        Assert.Equal("client-1", registration.ClientId);
        Assert.Equal("secret-1", registration.ClientSecret);
        Assert.NotNull(sent);
        Assert.Equal("SharpMind Studio", sent["client_name"]!.GetValue<string>());
        Assert.Equal("none", sent["token_endpoint_auth_method"]!.GetValue<string>());
        Assert.Equal("mcp:read", sent["scope"]!.GetValue<string>());
    }

    [Fact]
    public void AuthorizationUrl_ContainsPkceStateAndResource()
    {
        var client = new McpOAuthClient(new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));
        (string verifier, string challenge) = McpOAuthClient.CreatePkce();
        string state = McpOAuthClient.CreateState();

        string url = client.BuildAuthorizationUrl(
            Discovery(), "client-1", "http://127.0.0.1:33418/callback",
            "mcp:read", state, challenge);

        Assert.NotEqual(verifier, challenge);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("client_id=client-1", url);
        Assert.Contains("state=" + Uri.EscapeDataString(state), url);
        Assert.Contains("resource=" + Uri.EscapeDataString("https://mcp.example/mcp"), url);
        Assert.Contains("scope=mcp%3Aread", url);
    }

    [Fact]
    public async Task ExchangeCode_PostsTheVerifierAndParsesTheToken()
    {
        Dictionary<string, string>? form = null;
        var handler = new StubHandler(async request =>
        {
            form = ParseForm(await request.Content!.ReadAsStringAsync());
            return Json(new JsonObject
            {
                ["access_token"] = "access-1",
                ["refresh_token"] = "refresh-1",
                ["expires_in"] = 3600,
                ["token_type"] = "Bearer",
                ["scope"] = "mcp:read",
            });
        });

        var client = new McpOAuthClient(new HttpClient(handler));
        McpOAuthToken token = await client.ExchangeCodeAsync(
            Discovery(), "client-1", "secret-1", "code-1",
            "http://127.0.0.1:33418/callback", "verifier-1");

        Assert.Equal("access-1", token.AccessToken);
        Assert.Equal("refresh-1", token.RefreshToken);
        Assert.True(token.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.NotNull(form);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("verifier-1", form["code_verifier"]);
        Assert.Equal("secret-1", form["client_secret"]);
        Assert.Equal("https://mcp.example/mcp", form["resource"]);
    }

    [Fact]
    public async Task Refresh_UsesTheStoredRefreshTokenAndKeepsItWhenTheServerDoesNotRotate()
    {
        Dictionary<string, string>? form = null;
        var handler = new StubHandler(async request =>
        {
            form = ParseForm(await request.Content!.ReadAsStringAsync());
            return Json(new JsonObject
            {
                ["access_token"] = "access-2",
                ["token_type"] = "Bearer",
            });
        });

        var client = new McpOAuthClient(new HttpClient(handler));
        var session = new McpOAuthSession(
            "access-1",
            "refresh-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "Bearer",
            "mcp:read",
            "client-1",
            null,
            null,
            "https://auth.example",
            "https://auth.example/token",
            "https://mcp.example/mcp");

        McpOAuthToken token = await client.RefreshAsync(session);

        Assert.Equal("access-2", token.AccessToken);
        Assert.Equal("refresh-1", token.RefreshToken);
        Assert.NotNull(form);
        Assert.Equal("refresh_token", form["grant_type"]);
    }

    private static McpOAuthDiscovery Discovery() => new(
        "https://mcp.example/mcp",
        "https://auth.example",
        "https://auth.example/authorize",
        "https://auth.example/token",
        "https://auth.example/register",
        ["mcp:read"]);

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static Dictionary<string, string> ParseForm(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request);
    }
}
