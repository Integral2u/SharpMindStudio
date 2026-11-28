using System.Net;
using System.Text;
using System.Text.Json;
using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class AkamaiClientTests
{
    private const string Token = "linode-test-token";

    [Fact]
    public async Task Account_ReadsBalanceAndCompany()
    {
        var client = new AkamaiClient(Json(
            """{"company":"Acme","email":"a@example.com","balance":12.5,"uninvoiced_balance":3.25}"""));

        AkamaiAccount account = await client.GetAccountAsync(Token);

        Assert.Equal("Acme", account.Company);
        Assert.Equal(12.5, account.Balance);
        Assert.Equal(3.25, account.PendingCharges);
    }

    [Fact]
    public async Task Types_ReadsGpuAndPrice()
    {
        var client = new AkamaiClient(Json(
            """
            {"data":[
              {"id":"g6-standard-2","label":"Linode 4GB","class":"standard","vcpus":2,
               "memory":4096,"disk":81920,"gpus":0,"accelerated_devices":0,
               "price":{"hourly":0.036,"monthly":24}},
              {"id":"g1-gpu-rtx6000-1","label":"GPU RTX 6000","class":"gpu","vcpus":8,
               "memory":32768,"disk":640000,"gpus":1,"accelerated_devices":0,
               "price":{"hourly":1.5,"monthly":1000}}
            ],"page":1,"pages":1}
            """));

        IReadOnlyList<AkamaiType> types = await client.ListTypesAsync(Token);

        Assert.Equal("g1-gpu-rtx6000-1", types[0].Id);
        Assert.True(types[0].IsGpu);
        Assert.Equal(1, types[0].Gpus);
        Assert.Equal(1.5, types[0].HourlyCost);
        Assert.False(types[1].IsGpu);
    }

    [Fact]
    public async Task RegionsAndKeys_ReadCapabilitiesAndNumericIds()
    {
        var handler = new StubHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/regions", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"data":[{"id":"us-east","label":"Newark, NJ","country":"us","capabilities":["GPU Linodes","Metadata"]}],"pages":1}""");
            }
            if (path.EndsWith("/profile/sshkeys", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"data":[{"id":42,"label":"laptop","ssh_key":"ssh-ed25519 AAAA test"}],"pages":1}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new AkamaiClient(handler.Client);

        IReadOnlyList<AkamaiRegion> regions = await client.ListRegionsAsync(Token);
        IReadOnlyList<AkamaiSshKey> keys = await client.ListSshKeysAsync(Token);

        Assert.True(regions[0].SupportsGpu);
        Assert.Equal("us-east", regions[0].Id);
        Assert.Equal("42", keys[0].Id);
        Assert.Equal("laptop", keys[0].Label);
    }

    [Fact]
    public async Task CreateInstance_SendsAuthorizedKeyFirewallAndTag()
    {
        JsonElement? body = null;
        var handler = new StubHandler(request =>
        {
            body = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return JsonResponse(
                """
                {"id":123,"label":"sharpmind-abc","status":"provisioning",
                 "ipv4":["192.168.1.2","203.0.113.10"],"region":"us-east",
                 "type":"g1-gpu-rtx6000-1","created":"2026-11-28T01:00:00"}
                """);
        });
        var client = new AkamaiClient(handler.Client);

        AkamaiInstance instance = await client.CreateInstanceAsync(Token,
            new AkamaiCreateInstanceRequest
            {
                Region = "us-east",
                TypeId = "g1-gpu-rtx6000-1",
                ImageId = "linode/ubuntu24.04",
                Label = "sharpmind-abc",
                SshPublicKey = "ssh-ed25519 AAAA test",
            });

        Assert.Equal("123", instance.Id);
        Assert.Equal("203.0.113.10", instance.PublicIpv4);
        Assert.NotNull(body);
        JsonElement json = body!.Value;
        Assert.Equal("us-east", json.GetProperty("region").GetString());
        Assert.False(json.TryGetProperty("firewall_id", out _));
        Assert.False(json.TryGetProperty("tags", out _));
        Assert.Equal(
            "ssh-ed25519 AAAA test",
            json.GetProperty("authorized_keys")[0].GetString());
    }

    [Fact]
    public async Task AssignFirewallDevice_SendsNumericLinodeId()
    {
        JsonElement? body = null;
        var handler = new StubHandler(request =>
        {
            body = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return JsonResponse("""{"id":500,"entity":{"id":123,"type":"linode"}}""");
        });
        var client = new AkamaiClient(handler.Client);

        await client.AssignFirewallDeviceAsync(Token, "9000", "123");

        Assert.NotNull(body);
        Assert.Equal(123, body!.Value.GetProperty("id").GetInt64());
        Assert.Equal("linode", body.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CreateFirewall_AllowsComputeAndStudioPorts()
    {
        JsonElement? body = null;
        var handler = new StubHandler(request =>
        {
            body = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return JsonResponse("""{"id":9000,"label":"sharpmind-abc"}""");
        });
        var client = new AkamaiClient(handler.Client);

        string id = await client.CreateFirewallAsync(Token, "sharpmind-abc");

        Assert.Equal("9000", id);
        Assert.NotNull(body);
        string[] ports = [.. body!.Value
            .GetProperty("rules")
            .GetProperty("inbound")
            .EnumerateArray()
            .Select(rule => rule.GetProperty("ports").GetString() ?? "")];
        Assert.Contains("22", ports);
        Assert.Contains("8080", ports);
        Assert.Contains("5117", ports);
    }

    [Fact]
    public async Task InvalidToken_ReadsLinodeErrorReason()
    {
        var client = new AkamaiClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"errors":[{"reason":"Invalid Token"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            }).Client);

        AkamaiException error = await Assert.ThrowsAsync<AkamaiException>(
            () => client.GetAccountAsync(Token));

        Assert.Equal(401, error.StatusCode);
        Assert.Contains("Invalid Token", error.Message);
    }

    [Fact]
    public void Akamai_IsAKnownComputePrefab()
    {
        ComputeProviderPrefab prefab =
            ComputeProviderPrefabs.Find(ComputeProviderPrefabs.Akamai);

        Assert.Equal("Akamai Cloud (Linode)", prefab.Name);
        Assert.Equal(ComputeProviderKinds.Remote, prefab.Kind);
        Assert.NotEmpty(prefab.ReferralUrl ?? "");
    }

    private static HttpClient Json(string json) =>
        new StubHandler(_ => JsonResponse(json)).Client;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
            Client = new HttpClient(this);
        }

        public HttpClient Client { get; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
