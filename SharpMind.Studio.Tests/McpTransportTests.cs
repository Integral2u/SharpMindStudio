using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Vault;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpTransportTests
{
    [Fact]
    public async Task Client_TalksOverAnyTransportAndRedactsTheReply()
    {
        var transport = new FakeTransport();
        var secrets = new TestSecretResolver(("TOKEN", "s3cret-value"));
        await using var client = new McpServerClient(
            transport, secrets, [], "2024-11-05");

        await client.InitializeAsync();
        IReadOnlyList<McpToolInfo> tools = await client.ListToolsAsync();
        Assert.Equal("echo", Assert.Single(tools).Name);
        Assert.Contains(transport.Sent, message =>
            message["method"]?.GetValue<string>() == "notifications/initialized");

        McpToolResult result = await client.CallToolAsync(
            "echo",
            new JsonObject { ["text"] = "Bearer %vault:TOKEN%" });

        Assert.Equal("Bearer ***", result.Text);
        Assert.Contains(transport.Sent, message =>
            message.ToJsonString().Contains("s3cret-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Client_RejectsAMissingSecretBeforeSending()
    {
        var transport = new FakeTransport();
        await using var client = new McpServerClient(
            transport, new TestSecretResolver(), [], "2024-11-05");
        await client.InitializeAsync();

        SecretMissingException error = await Assert.ThrowsAsync<SecretMissingException>(
            () => client.CallToolAsync(
                "echo",
                new JsonObject { ["text"] = "%vault:MISSING%" }));

        Assert.Contains("MISSING", error.Message);
        Assert.DoesNotContain(transport.Sent, message =>
            message["method"]?.GetValue<string>() == "tools/call");
    }

    [Fact]
    public async Task HttpTransport_UsesJsonAndEchoesTheSessionAndVersion()
    {
        var captured = new List<CapturedRequest>();
        var handler = new StubHandler(async request =>
        {
            JsonObject message = await ReadMessageAsync(request);
            string method = message["method"]!.GetValue<string>();
            captured.Add(Capture(method, request));

            if (method == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            JsonNode result = method switch
            {
                "initialize" => new JsonObject { ["protocolVersion"] = "2025-03-26" },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "echo",
                            ["description"] = "Echoes text",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                _ => new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = message["params"]?["arguments"]?["text"]?.GetValue<string>()
                                ?? "ok",
                        },
                    },
                },
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = message["id"]?.DeepClone(),
                        ["result"] = result,
                    }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"),
            };

            if (method == "initialize")
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-1");
            return response;
        });

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer token-1",
        };
        var http = new HttpClient(handler);
        var transport = new HttpMcpTransport(
            "https://example.test/mcp", headers, http);
        await using var client = new McpServerClient(
            transport, new TestSecretResolver(), [], "2025-03-26");

        await client.InitializeAsync();
        IReadOnlyList<McpToolInfo> tools = await client.ListToolsAsync();
        Assert.Equal("echo", Assert.Single(tools).Name);

        CapturedRequest list = Assert.Single(captured, request => request.Method == "tools/list");
        Assert.Equal("session-1", list.Session);
        Assert.Equal("2025-03-26", list.Protocol);
        Assert.Equal("Bearer token-1", list.Authorization);
        Assert.All(captured, request => Assert.Equal("2025-03-26", request.Protocol));
    }

    [Fact]
    public async Task HttpTransport_ReadsServerSentEventsAndAdoptsTheServerVersion()
    {
        var captured = new List<CapturedRequest>();
        var handler = new StubHandler(async request =>
        {
            JsonObject message = await ReadMessageAsync(request);
            string method = message["method"]!.GetValue<string>();
            captured.Add(Capture(method, request));

            if (method == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            string json = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = message["id"]?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["tools"] = new JsonArray(),
                },
            }.ToJsonString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"event: message\ndata: {json}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });

        var transport = new HttpMcpTransport(
            "https://example.test/mcp",
            new Dictionary<string, string>(),
            new HttpClient(handler));
        await using var client = new McpServerClient(
            transport, new TestSecretResolver(), [], "2025-03-26");

        await client.InitializeAsync();
        await client.ListToolsAsync();

        CapturedRequest list = Assert.Single(captured, request => request.Method == "tools/list");
        Assert.Equal("2024-11-05", list.Protocol);
    }

    [Fact]
    public async Task HttpTransport_ReportsAnHttpError()
    {
        var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream unavailable", Encoding.UTF8, "text/plain"),
            }));

        var transport = new HttpMcpTransport(
            "https://example.test/mcp",
            new Dictionary<string, string>(),
            new HttpClient(handler));
        await using var client = new McpServerClient(
            transport, new TestSecretResolver(), [], "2025-03-26");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.InitializeAsync());

        Assert.Contains("MCP HTTP 502", error.Message);
        Assert.Contains("upstream unavailable", error.Message);
    }

    [Fact]
    public void ClientFactory_RejectsAMissingSecretInAnHttpHeader()
    {
        var settings = new McpServerSettings
        {
            Id = "remote",
            Name = "Remote",
            Transport = McpTransports.Http,
            Url = "https://example.test/mcp",
            Headers =
            {
                ["Authorization"] = "Bearer %vault:MISSING%",
            },
        };

        SecretMissingException error = Assert.Throws<SecretMissingException>(
            () => McpClientFactory.Create(settings, new TestSecretResolver()));

        Assert.Contains("MISSING", error.Message);
    }

    [Fact]
    public async Task HttpTransport_SendsTheOAuthBearerTokenInsteadOfAManualHeader()
    {
        var captured = new List<CapturedRequest>();
        var handler = new StubHandler(async request =>
        {
            JsonObject message = await ReadMessageAsync(request);
            string method = message["method"]!.GetValue<string>();
            captured.Add(Capture(method, request));

            if (method == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = message["id"]?.DeepClone(),
                        ["result"] = new JsonObject
                        {
                            ["protocolVersion"] = "2025-03-26",
                        },
                    }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer manual",
        };
        var transport = new HttpMcpTransport(
            "https://example.test/mcp",
            headers,
            new HttpClient(handler),
            accessToken: _ => Task.FromResult<string?>("oauth-token"));
        await using var client = new McpServerClient(
            transport, new TestSecretResolver(), [], "2025-03-26");

        await client.InitializeAsync();

        Assert.Equal("Bearer oauth-token", captured[0].Authorization);
    }

    [Fact]
    public void ClientFactory_RejectsOAuthWithoutATokenProvider()
    {
        var settings = new McpServerSettings
        {
            Id = "remote",
            Name = "Remote",
            Transport = McpTransports.Http,
            Url = "https://example.test/mcp",
            Auth = McpAuth.OAuth,
        };

        Assert.Throws<McpSignInRequiredException>(
            () => McpClientFactory.Create(settings, new TestSecretResolver()));
    }

    private static async Task<JsonObject> ReadMessageAsync(HttpRequestMessage request)
    {
        string body = await request.Content!.ReadAsStringAsync();
        return JsonNode.Parse(body)!.AsObject();
    }

    private static CapturedRequest Capture(string method, HttpRequestMessage request) =>
        new(
            method,
            First(request, "MCP-Protocol-Version"),
            First(request, "Mcp-Session-Id"),
            First(request, "Authorization"));

    private static string? First(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private sealed record CapturedRequest(
        string Method,
        string? Protocol,
        string? Session,
        string? Authorization);

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class FakeTransport : IMcpTransport
    {
        private readonly Channel<JsonNode> _messages =
            Channel.CreateUnbounded<JsonNode>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        public List<JsonNode> Sent { get; } = [];

        public string Description => "fake";

        public string? ErrorTail => null;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendAsync(JsonNode message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message.DeepClone());

            if (message is not JsonObject request) return Task.CompletedTask;
            string? method = request["method"]?.GetValue<string>();
            if (method is null || method == "notifications/initialized")
                return Task.CompletedTask;

            JsonNode result = method switch
            {
                "initialize" => new JsonObject { ["protocolVersion"] = "2024-11-05" },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "echo",
                            ["description"] = "Echoes text",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" },
                        },
                    },
                },
                _ => new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = request["params"]?["arguments"]?["text"]?.GetValue<string>()
                                ?? "ok",
                        },
                    },
                },
            };

            _messages.Writer.TryWrite(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]?.DeepClone(),
                ["result"] = result,
            });
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<JsonNode> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (JsonNode message in _messages.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
