using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ComputeClientTests
{
    private static readonly ComputeProvider Provider = new(
        "compute-1",
        "Local Docker",
        ComputeProviderKinds.Local,
        "http://127.0.0.1:8080",
        "test-token",
        "tinystories",
        ["tinystories"]);

    [Fact]
    public async Task Health_ParsesTheHostSnapshot()
    {
        var handler = new CaptureHandler(_ => Json(
            """{"status":"ok","model":"tinystories","modelConfigured":true,"modelLoaded":true,"dataRoot":"/data/SharpMind","runningJobs":2}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        ComputeHealth health = await client.GetHealthAsync(Provider);

        Assert.Equal("ok", health.Status);
        Assert.Equal("tinystories", health.Model);
        Assert.True(health.ModelConfigured);
        Assert.True(health.ModelLoaded);
        Assert.Equal(2, health.RunningJobs);
    }

    [Fact]
    public async Task Models_ReadsIdsSizesAndTheActiveFlag()
    {
        var handler = new CaptureHandler(_ => Json(
            """{"object":"list","data":[{"id":"a","bytes":10,"active":false},{"id":"b","bytes":20,"active":true}]}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        IReadOnlyList<ComputeModelInfo> models = await client.ListModelsAsync(Provider);

        Assert.Equal("http://127.0.0.1:8080/v1/models", handler.LastUri);
        Assert.Equal("Bearer test-token", handler.LastAuthorization);
        Assert.Equal(new[] { "a", "b" }, models.Select(model => model.Id).ToArray());
        Assert.True(models[1].Active);
        Assert.Equal(20, models[1].Bytes);
    }

    [Fact]
    public async Task Stream_YieldsDeltasUsageAndDone()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Hel"}}]}
            data: {"choices":[{"delta":{"content":"lo"}}]}
            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}
            data: {"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":2}}
            data: [DONE]

            """;
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        var events = new List<ComputeStreamEvent>();
        await foreach (ComputeStreamEvent entry in client.StreamChatAsync(
            Provider,
            "tinystories",
            [new JsonObject { ["role"] = "user", ["content"] = "hi" }],
            temperature: 0.5f,
            topP: 0.9f,
            maxTokens: 16))
        {
            events.Add(entry);
        }

        Assert.Equal("Hello", string.Concat(events.Select(entry => entry.Delta)));
        Assert.Equal(8, events.Last(entry => entry.PromptTokens is not null).PromptTokens);
        Assert.Equal(2, events.Last(entry => entry.CompletionTokens is not null).CompletionTokens);
        Assert.Contains(events, entry => entry.IsComplete);
        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", handler.LastUri);
        Assert.Contains("\"max_tokens\":16", handler.LastBody);
        Assert.Contains("\"top_p\"", handler.LastBody ?? "");
    }

    [Fact]
    public async Task Stream_SurfacesAnErrorFrame()
    {
        const string sse = """
            data: {"error":{"message":"No model configured."}}
            data: [DONE]

            """;
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        var errors = new List<string>();
        await foreach (ComputeStreamEvent entry in client.StreamChatAsync(
            Provider,
            "tinystories",
            [new JsonObject { ["role"] = "user", ["content"] = "hi" }]))
        {
            if (entry.Error is not null) errors.Add(entry.Error);
        }

        Assert.Contains("No model configured.", errors);
    }

    [Fact]
    public async Task UploadModel_SendsTheNameAndActivateFlag()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("tinystories.smm");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);

        var handler = new CaptureHandler(_ => Json(
            """{"id":"tinystories","name":"tinystories.smm","path":"/data/SharpMind/Models/tinystories.smm","bytes":4,"active":true}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        ComputeUploadResult result = await client.UploadModelAsync(Provider, file, activate: true);

        Assert.Equal("PUT", handler.LastMethod);
        Assert.Equal(
            "http://127.0.0.1:8080/v1/models/tinystories.smm?activate=true",
            handler.LastUri);
        Assert.True(result.Active);
        Assert.Equal(4, result.Bytes);
    }

    [Fact]
    public async Task UploadFile_EscapesEachPathSegment()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("corpus.txt");
        await File.WriteAllTextAsync(file, "hello");

        var handler = new CaptureHandler(_ => Json(
            """{"path":"datasets/a b/corpus.txt","fullPath":"/data/SharpMind/Files/datasets/a b/corpus.txt","bytes":5}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        ComputeUploadResult result = await client.UploadFileAsync(
            Provider, file, "datasets/a b/corpus.txt");

        Assert.Equal(
            "http://127.0.0.1:8080/v1/files/datasets/a%20b/corpus.txt",
            handler.LastUri);
        Assert.Equal("/data/SharpMind/Files/datasets/a b/corpus.txt", result.Path);
    }

    [Fact]
    public async Task Training_StartWrapsTheJobAndReadsTheSnapshot()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(
                """{"id":"job-1","status":"queued"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        ComputeJob job = await client.StartTrainingAsync(
            Provider,
            new JsonObject { ["name"] = "smoke", ["totalSteps"] = 10 });

        Assert.Equal("POST", handler.LastMethod);
        Assert.Equal("http://127.0.0.1:8080/v1/training/jobs", handler.LastUri);
        Assert.Contains("\"job\"", handler.LastBody);
        Assert.Contains("\"name\":\"smoke\"", handler.LastBody);
        Assert.Equal("job-1", job.Id);
        Assert.Equal("queued", job.Status);
    }

    [Fact]
    public async Task Job_ReadsProgressAndArtifacts()
    {
        var handler = new CaptureHandler(_ => Json(
            """{"id":"job-1","status":"succeeded","progress":1,"step":10,"loss":1.5,"valLoss":1.7,"success":true,"exportPath":"/data/out.smm"}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        ComputeJob job = await client.GetJobAsync(Provider, "job-1");

        Assert.Equal("http://127.0.0.1:8080/v1/training/jobs/job-1", handler.LastUri);
        Assert.Equal(1, job.Progress);
        Assert.Equal(10, job.Step);
        Assert.True(job.Success);
        Assert.Equal("/data/out.smm", job.ExportPath);
    }

    [Fact]
    public async Task DownloadArtifact_UsesTheDownloadEndpoint()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 9, 9]),
        });
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        await using Stream stream = await client.DownloadArtifactAsync(Provider, "job-1");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        Assert.Equal(
            "http://127.0.0.1:8080/v1/training/jobs/job-1/download",
            handler.LastUri);
        Assert.Equal(new byte[] { 9, 9, 9 }, memory.ToArray());
    }

    [Fact]
    public async Task DeleteModel_UsesTheDeleteEndpoint()
    {
        var handler = new CaptureHandler(_ => Json("""{"deleted":"tinystories.smm"}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        await client.DeleteModelAsync(Provider, "tinystories.smm");

        Assert.Equal("DELETE", handler.LastMethod);
        Assert.Equal("http://127.0.0.1:8080/v1/models/tinystories.smm", handler.LastUri);
        Assert.Equal("Bearer test-token", handler.LastAuthorization);
    }

    [Fact]
    public async Task HttpErrors_AreSurfacedWithTheHostMessage()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"message":"Unauthorized."}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        ComputeException error = await Assert.ThrowsAsync<ComputeException>(
            () => client.GetHealthAsync(Provider));

        Assert.Equal(401, error.StatusCode);
        Assert.Contains("Unauthorized", error.Message);
    }

    [Fact]
    public async Task UnusableProvider_FailsBeforeAnyRequest()
    {
        var handler = new CaptureHandler(_ => Json("{}"));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);
        var provider = Provider with { Token = "" };

        await Assert.ThrowsAsync<ComputeException>(() => client.GetHealthAsync(provider));
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task BaseUrlWithV1_IsNormalized()
    {
        var handler = new CaptureHandler(_ => Json("""{"data":[]}"""));
        using var http = new HttpClient(handler);
        var client = new ComputeClient(http);

        await client.ListModelsAsync(Provider with { BaseUrl = "https://host/v1" });

        Assert.Equal("https://host/v1/models", handler.LastUri);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
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
