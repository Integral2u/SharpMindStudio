using System.Net;
using System.Net.Http;
using System.Text;
using SharpMind.Studio.Core.HuggingFace;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class HuggingFaceClientTests
{
    [Fact]
    public async Task Search_ParsesReposFilesLicenseAndTokenHeader()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("""
                [
                  {
                    "id": "TinyLlama/TinyLlama-1.1B-Chat-v1.0-GGUF",
                    "author": "TinyLlama",
                    "gated": false,
                    "downloads": 1234,
                    "likes": 12,
                    "lastModified": "2024-01-02T03:04:05.000Z",
                    "tags": ["gguf", "license:apache-2.0"],
                    "siblings": [
                      { "rfilename": "tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf" },
                      { "rfilename": "README.md" }
                    ]
                  },
                  {
                    "id": "secret/gated-gguf",
                    "gated": "auto",
                    "tags": ["gguf"],
                    "siblings": []
                  }
                ]
                """);
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        IReadOnlyList<HuggingFaceRepo> repos = await client.SearchAsync(
            "tinyllama", token: "hf_test");

        Assert.Equal(2, repos.Count);
        HuggingFaceRepo repo = repos[0];
        Assert.Equal("TinyLlama/TinyLlama-1.1B-Chat-v1.0-GGUF", repo.Id);
        Assert.Equal("TinyLlama", repo.Author);
        Assert.Equal(1234, repo.Downloads);
        Assert.Equal(12, repo.Likes);
        Assert.False(repo.Gated);
        Assert.Equal("apache-2.0", repo.License);
        Assert.Single(repo.Files);
        Assert.Equal("Q4_K_M", repo.Files[0].Quantization);
        Assert.True(repos[1].Gated);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal("hf_test", captured.Headers.Authorization.Parameter);
        Assert.Contains("search=tinyllama", captured.RequestUri!.Query);
    }

    [Fact]
    public async Task GetFiles_ParsesSizesAndSha()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            {
              "siblings": [
                {
                  "rfilename": "model.Q8_0.gguf",
                  "size": 123456,
                  "lfs": { "sha256": "abc123", "size": 123456 }
                },
                { "rfilename": "README.md", "size": 10 }
              ]
            }
            """));
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        IReadOnlyList<HuggingFaceFile> files = await client.GetFilesAsync("owner/repo");

        HuggingFaceFile file = Assert.Single(files);
        Assert.Equal("model.Q8_0.gguf", file.FileName);
        Assert.Equal(123456, file.Size);
        Assert.Equal("Q8_0", file.Quantization);
        Assert.Equal("abc123", file.Sha256);
    }

    [Fact]
    public async Task Download_WritesFileAndReportsProgress()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello model");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);
        using var temp = new TempDirectory();
        string destination = temp.Combine("model.gguf");
        var progress = new List<HuggingFaceDownloadProgress>();

        string path = await client.DownloadAsync(
            new HuggingFaceFile("owner/repo", "model.gguf", payload.Length, "Q8_0", null),
            destination,
            progress: new InlineProgress<HuggingFaceDownloadProgress>(progress.Add));

        Assert.Equal(destination, path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Download_ResumesPartialFile()
    {
        string? range = null;
        var handler = new StubHandler(request =>
        {
            range = request.Headers.Range?.ToString();
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("world")),
            };
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);
        using var temp = new TempDirectory();
        string destination = temp.Combine("model.gguf");
        await File.WriteAllTextAsync(destination + ".part", "hello ");

        string path = await client.DownloadAsync(
            new HuggingFaceFile("owner/repo", "model.gguf", 11, "Q8_0", null),
            destination);

        Assert.Equal("bytes=6-", range);
        Assert.Equal("hello world", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Token_IsEncryptedAtRest()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("huggingface.json");
        string key = temp.Combine("huggingface.key");
        var store = new HuggingFaceSettingsStore(settings, key);

        store.SetToken("hf_secret");
        Assert.True(store.HasToken);
        Assert.Equal("hf_secret", store.ApiToken);
        Assert.DoesNotContain("hf_secret", File.ReadAllText(settings));

        var reopened = new HuggingFaceSettingsStore(settings, key);
        Assert.Equal("hf_secret", reopened.ApiToken);
        reopened.ClearToken();
        Assert.Null(reopened.ApiToken);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
