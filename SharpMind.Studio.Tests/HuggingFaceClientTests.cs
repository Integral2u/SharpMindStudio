using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public void Token_CanComeFromTheVault()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("huggingface.json");
        string key = temp.Combine("huggingface.key");
        var secrets = new TestSecretResolver(("HF_TOKEN", "hf_vault_secret"));
        var store = new HuggingFaceSettingsStore(settings, key, secrets);

        store.SetToken("hf_inline");
        store.SetTokenSecret("HF_TOKEN");

        Assert.True(store.HasToken);
        Assert.Equal("hf_vault_secret", store.ApiToken);
        Assert.Equal("HF_TOKEN", store.TokenSecret);
        Assert.DoesNotContain("hf_vault_secret", File.ReadAllText(settings));
        Assert.DoesNotContain("hf_inline", File.ReadAllText(settings));

        store.SetToken("hf_other");
        Assert.Null(store.TokenSecret);
        Assert.Equal("hf_other", store.ApiToken);
    }

    [Fact]
    public async Task WhoAmI_ParsesAccountAndOrganizations()
    {
        var handler = new StubHandler(_ => JsonResponse(
            """
            {"name":"alice","fullname":"Alice","orgs":[{"name":"acme"},{"name":"research"}]}
            """));
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        HuggingFaceUser user = await client.WhoAmIAsync("hf_token");

        Assert.Equal("alice", user.Name);
        Assert.Equal("Alice", user.FullName);
        Assert.Equal(new[] { "acme", "research" }, user.Organizations);
        Assert.Equal(new[] { "alice", "acme", "research" }, user.Namespaces);
    }

    [Fact]
    public async Task CreateModelRepo_SendsOrganizationAndVisibility()
    {
        string? body = null;
        var handler = new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"url":"https://huggingface.co/acme/model"}"""),
            };
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        await client.CreateModelRepoAsync("acme/model", "hf_token", privateRepo: true);

        Assert.NotNull(body);
        using JsonDocument document = JsonDocument.Parse(body!);
        Assert.Equal("model", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("model", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("acme", document.RootElement.GetProperty("organization").GetString());
        Assert.Equal("private", document.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task CreateModelRepo_ContinuesWhenForbiddenButRepoExists()
    {
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return JsonResponse("""{"id":"acme/model"}""");
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        await client.CreateModelRepoAsync("acme/model", "hf_token", privateRepo: true);
    }

    [Fact]
    public async Task CreateModelRepo_ExplainsMissingCreatePermission()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"error":"You don't have the rights to create a model under the namespace \"acme\""}""",
                    Encoding.UTF8,
                    "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateModelRepoAsync("acme/model", "hf_token", privateRepo: true));

        Assert.Contains("Create the repository", error.Message);
        Assert.Contains("Write access", error.Message);
    }

    [Fact]
    public async Task ListModelFiles_ReadsTreePathsAndSizes()
    {
        var handler = new StubHandler(_ => JsonResponse(
            """
            [{"type":"file","path":"model_Q8_0.gguf","size":123},
             {"type":"file","path":"README.md","size":45},
             {"type":"directory","path":"folder"}]
            """));
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        IReadOnlyList<HuggingFaceRepoFile> files =
            await client.ListModelFilesAsync("acme/model", "hf_token");

        Assert.Equal(2, files.Count);
        Assert.Equal("model_Q8_0.gguf", files[0].Path);
        Assert.Equal(123, files[0].Size);
    }

    [Fact]
    public async Task UploadFiles_CommitsRegularFiles()
    {
        string? commitBody = null;
        var handler = new StubHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith("/preupload/main", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"files":[{"path":"README.md","uploadMode":"regular","shouldIgnore":false}]}""");
            }
            if (url.EndsWith("/commit/main", StringComparison.Ordinal))
            {
                commitBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(
                    """{"commitUrl":"https://huggingface.co/acme/model/commit/abc","commitOid":"abc"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        HuggingFaceCommitResult result = await client.UploadFilesAsync(
            "acme/model",
            [HuggingFaceUploadFile.FromText("README.md", "hello")],
            "hf_token");

        Assert.Equal("acme/model", result.RepoId);
        Assert.Equal("abc", result.CommitOid);
        Assert.NotNull(commitBody);
        Assert.Contains("\"key\":\"file\"", commitBody);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")), commitBody);
    }

    [Fact]
    public async Task UploadFiles_UsesLfsAndCommitsPointer()
    {
        const string content = "large model bytes";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        string oid = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        bool put = false;
        string? commitBody = null;

        var handler = new StubHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith("/preupload/main", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"files":[{"path":"model.gguf","uploadMode":"lfs","shouldIgnore":false}]}""");
            }
            if (url.EndsWith("/info/lfs/objects/batch", StringComparison.Ordinal))
            {
                string batch = """
                    {"objects":[{"oid":"__OID__","size":__SIZE__,"actions":{"upload":{"href":"https://upload.test/model"}}}]}
                    """
                    .Replace("__OID__", oid)
                    .Replace("__SIZE__", bytes.Length.ToString());
                return JsonResponse(batch);
            }
            if (url == "https://upload.test/model")
            {
                put = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (url.EndsWith("/commit/main", StringComparison.Ordinal))
            {
                commitBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(
                    """{"commitUrl":"https://huggingface.co/acme/model/commit/abc","commitOid":"abc"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        await client.UploadFilesAsync(
            "acme/model",
            [new HuggingFaceUploadFile { PathInRepo = "model.gguf", Content = bytes }],
            "hf_token");

        Assert.True(put);
        Assert.NotNull(commitBody);
        Assert.Contains("\"key\":\"lfsFile\"", commitBody);
        Assert.Contains(oid, commitBody);
    }

    [Fact]
    public async Task UploadFiles_CompletesLfsMultipartParts()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("model.gguf");
        byte[] bytes = Encoding.UTF8.GetBytes("0123456789");
        await File.WriteAllBytesAsync(source, bytes);
        string oid = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string? completionBody = null;
        var parts = new List<int>();

        var handler = new StubHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith("/preupload/main", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"files":[{"path":"model.gguf","uploadMode":"lfs","shouldIgnore":false}]}""");
            }
            if (url.EndsWith("/info/lfs/objects/batch", StringComparison.Ordinal))
            {
                string batch = """
                    {"objects":[{"oid":"__OID__","size":10,"actions":{"upload":{"href":"https://upload.test/complete","header":{"chunk_size":"4","1":"https://upload.test/1","2":"https://upload.test/2","3":"https://upload.test/3"}}}}]}
                    """
                    .Replace("__OID__", oid);
                return JsonResponse(batch);
            }
            if (url.StartsWith("https://upload.test/", StringComparison.Ordinal) &&
                url != "https://upload.test/complete")
            {
                parts.Add(int.Parse(url[^1..]));
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new EntityTagHeaderValue($"\"etag-{parts[^1]}\"");
                return response;
            }
            if (url == "https://upload.test/complete")
            {
                completionBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (url.EndsWith("/commit/main", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"commitUrl":"https://huggingface.co/acme/model/commit/abc","commitOid":"abc"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        var client = new HuggingFaceClient(http);

        await client.UploadFilesAsync(
            "acme/model",
            [new HuggingFaceUploadFile { PathInRepo = "model.gguf", LocalPath = source }],
            "hf_token");

        Assert.Equal([1, 2, 3], parts);
        Assert.NotNull(completionBody);
        Assert.Contains("\"partNumber\":1", completionBody);
        Assert.Contains("etag-3", completionBody);
        Assert.Contains(oid, completionBody);
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
