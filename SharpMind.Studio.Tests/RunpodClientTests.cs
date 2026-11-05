using System.Net;
using System.Net.Http;
using System.Text;
using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class RunpodClientTests
{
    private const string BaseUrl = "https://api.runpod.io/v2";

    [Fact]
    public async Task CreatePod_PostsTheV2ShapeAndReadsThePod()
    {
        var handler = new CaptureHandler(_ => Json(
            """{"id":"pod123","name":"sharpmind-abc","status":"PROVISIONING","image":"me/server:gpu","cost":0.44,"dataCenterId":"US-KS-2"}""",
            HttpStatusCode.Created));
        using var http = new HttpClient(handler);
        var client = new RunpodClient(http);

        RunpodPod pod = await client.CreatePodAsync("rpa_key", new RunpodCreateRequest
        {
            Name = "sharpmind-abc",
            Image = "me/server:gpu",
            GpuType = "NVIDIA GeForce RTX 4090",
            DiskGb = 30,
            Ports = ["8080/http", "5117/http"],
            Env = new Dictionary<string, string> { ["SHARPMIND_TOKEN"] = "token" },
        });

        Assert.Equal($"{BaseUrl}/pods", handler.LastUri);
        Assert.Equal("Bearer rpa_key", handler.LastAuthorization);
        Assert.Contains("\"image\":\"me/server:gpu\"", handler.LastBody);
        Assert.Contains("\"gpu\":{\"id\":\"NVIDIA GeForce RTX 4090\",\"count\":1}", handler.LastBody);
        Assert.Contains("\"disk\":30", handler.LastBody);
        Assert.Contains("\"8080/http\"", handler.LastBody);
        Assert.Contains("\"SHARPMIND_TOKEN\":\"token\"", handler.LastBody);
        Assert.Equal("pod123", pod.Id);
        Assert.Equal("PROVISIONING", pod.Status);
        Assert.Equal(0.44, pod.Cost);
    }

    [Fact]
    public async Task GetPod_UsesThePodPath()
    {
        var handler = new CaptureHandler(_ => Json(
            """{"id":"pod123","name":"sharpmind","status":"RUNNING","image":"me/server:gpu"}"""));
        using var http = new HttpClient(handler);
        var client = new RunpodClient(http);

        RunpodPod pod = await client.GetPodAsync("rpa_key", "pod123");

        Assert.Equal($"{BaseUrl}/pods/pod123", handler.LastUri);
        Assert.Equal("RUNNING", pod.Status);
    }

    [Fact]
    public async Task Terminate_PostsTheAction()
    {
        var handler = new CaptureHandler(_ => Json("""{"id":"pod123","status":"TERMINATED"}"""));
        using var http = new HttpClient(handler);
        var client = new RunpodClient(http);

        await client.TerminatePodAsync("rpa_key", "pod123");

        Assert.Equal($"{BaseUrl}/pods/pod123/action", handler.LastUri);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("\"terminate\"", handler.LastBody);
    }

    [Fact]
    public async Task Validate_UsesAOnePodList()
    {
        var handler = new CaptureHandler(_ => Json("""{"pods":[],"pagination":{"nextCursor":null,"hasNextPage":false}}"""));
        using var http = new HttpClient(handler);
        var client = new RunpodClient(http);

        await client.ValidateApiKeyAsync("rpa_key");

        Assert.Equal($"{BaseUrl}/pods?limit=1", handler.LastUri);
    }

    [Fact]
    public async Task Errors_UseTheProblemDetail()
    {
        var handler = new CaptureHandler(_ => Json(
            """{"title":"Bad Request","status":400,"detail":"no capacity for NVIDIA GeForce RTX 4090"}""",
            HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        var client = new RunpodClient(http);

        RunpodException error = await Assert.ThrowsAsync<RunpodException>(
            () => client.GetPodAsync("rpa_key", "pod123"));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("no capacity", error.Message);
    }

    [Fact]
    public async Task MissingKey_FailsBeforeAnyRequest()
    {
        var handler = new CaptureHandler(_ => Json("{}"));
        using var http = new HttpClient(handler);
        var client = new RunpodClient(http);

        await Assert.ThrowsAsync<RunpodException>(() => client.ValidateApiKeyAsync(""));
        Assert.Null(handler.LastUri);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class CaptureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.OriginalString;
            LastMethod = request.Method.Method;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
