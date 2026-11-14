using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class VultrClientTests
{
    private const string ApiKey = "vultr-test-key";

    [Fact]
    public async Task ListPlans_ParsesGpuFieldsAndCosts()
    {
        var handler = Json("""
            {"plans":[
              {"id":"vcg-a16-6c-60g-16vram","type":"vcg","vcpu_count":6,"ram":61440,"disk":60,
               "monthly_cost":240,"hourly_cost":0.47,"gpu_vram_gb":16,
               "gpu_type":"NVIDIA A16","locations":["ewr","ord"]},
              {"id":"vc2-1c-1gb","type":"vc2","vcpu_count":1,"ram":1024,"disk":25,
               "monthly_cost":5,"hourly_cost":0.007,"gpu_vram_gb":0,"locations":["ewr"]}
            ]}
            """);
        var client = new VultrClient(handler.Client);

        IReadOnlyList<VultrPlan> plans = await client.ListPlansAsync(ApiKey);

        Assert.Equal(2, plans.Count);
        VultrPlan gpu = plans[0];
        Assert.Equal("vcg-a16-6c-60g-16vram", gpu.Id);
        Assert.True(gpu.IsGpu);
        Assert.Equal(6, gpu.VcpuCount);
        Assert.Equal(16, gpu.GpuVramGb);
        Assert.Equal("NVIDIA A16", gpu.GpuType);
        Assert.Equal(0.47, gpu.HourlyCost, 3);
        Assert.Equal(240, gpu.MonthlyCost, 2);
        Assert.Equal(["ewr", "ord"], gpu.Regions);
        Assert.False(plans[1].IsGpu);
    }

    [Fact]
    public async Task ListSshKeys_ParsesExistingKeys()
    {
        var handler = Json("""
            {"ssh_keys":[
              {"id":"key-1","name":"workstation","ssh_key":"ssh-ed25519 AAAA test"}
            ]}
            """);
        var client = new VultrClient(handler.Client);

        IReadOnlyList<VultrSshKey> keys = await client.ListSshKeysAsync(ApiKey);

        VultrSshKey key = Assert.Single(keys);
        Assert.Equal("key-1", key.Id);
        Assert.Equal("workstation", key.Name);
        Assert.StartsWith("ssh-ed25519", key.PublicKey);
    }

    [Fact]
    public async Task CreateInstance_SendsTheSshKeyFirewallAndTag()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {"instance":{
                  "id":"instance-1","label":"sharpmind-abc","status":"pending",
                  "main_ip":"0.0.0.0","power_status":"stopped","server_status":"installingbooting",
                  "region":"ewr","plan":"vcg-a16-6c-60g-16vram","pending_charges":0,
                  "date_created":"2026-11-14T00:00:00+00:00"
                }}
                """);
        });
        var client = new VultrClient(handler.Client);

        VultrInstance instance = await client.CreateInstanceAsync(ApiKey, new VultrCreateInstanceRequest
        {
            Region = "ewr",
            Plan = "vcg-a16-6c-60g-16vram",
            OsId = 2284,
            Label = "sharpmind-abc",
            Hostname = "sharpmind-abc",
            SshKeyIds = ["key-1"],
            FirewallGroupId = "fw-1",
        });

        Assert.Equal("instance-1", instance.Id);
        Assert.Equal("pending", instance.Status);
        Assert.NotNull(body);
        JsonNode json = JsonNode.Parse(body!)!;
        Assert.Equal("ewr", json["region"]!.GetValue<string>());
        Assert.Equal("vcg-a16-6c-60g-16vram", json["plan"]!.GetValue<string>());
        Assert.Equal(2284, json["os_id"]!.GetValue<int>());
        Assert.Equal("key-1", json["sshkey_id"]![0]!.GetValue<string>());
        Assert.Equal("fw-1", json["firewall_group_id"]!.GetValue<string>());
        Assert.Equal("sharpmind", json["tag"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetInstance_ReportsThePublicIpAndRunningState()
    {
        var handler = Json("""
            {"instance":{
              "id":"instance-1","label":"sharpmind-abc","status":"active",
              "main_ip":"203.0.113.10","power_status":"running","server_status":"ok",
              "region":"ewr","plan":"vcg-a16-6c-60g-16vram","pending_charges":0.12,
              "date_created":"2026-11-14T00:00:00+00:00"
            }}
            """);
        var client = new VultrClient(handler.Client);

        VultrInstance instance = await client.GetInstanceAsync(ApiKey, "instance-1");

        Assert.True(instance.IsRunning);
        Assert.Equal("203.0.113.10", instance.MainIp);
        Assert.Equal(0.12, instance.PendingCharges, 2);
    }

    [Fact]
    public async Task DeleteInstance_UsesDelete()
    {
        HttpMethod? method = null;
        string? path = null;
        var handler = new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new VultrClient(handler.Client);

        await client.DeleteInstanceAsync(ApiKey, "instance-1");

        Assert.Equal(HttpMethod.Delete, method);
        Assert.EndsWith("/instances/instance-1", path);
    }

    [Fact]
    public async Task InvalidKey_SurfacesTheVultrMessageAndStatus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":"Invalid API key.","status":401}""",
                Encoding.UTF8,
                "application/json"),
        });
        var client = new VultrClient(handler.Client);

        VultrException error = await Assert.ThrowsAsync<VultrException>(
            () => client.GetAccountAsync(ApiKey));

        Assert.Equal(401, error.StatusCode);
        Assert.Contains("Invalid API key", error.Message);
    }

    private static StubHandler Json(string json) =>
        new(_ => JsonResponse(json));

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
