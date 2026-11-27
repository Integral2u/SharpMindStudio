using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.HuggingFace;

/// <summary>The signed-in HuggingFace account and its writable namespaces.</summary>
public sealed record HuggingFaceUser(
    string Name,
    string? FullName,
    IReadOnlyList<string> Organizations)
{
    public IReadOnlyList<string> Namespaces =>
        [Name, .. Organizations.Where(org =>
            !string.Equals(org, Name, StringComparison.OrdinalIgnoreCase))];
}

/// <summary>One file to add to a Hub commit. Exactly one of the two sources is set.</summary>
public sealed record HuggingFaceUploadFile
{
    public required string PathInRepo { get; init; }
    public string? LocalPath { get; init; }
    public byte[]? Content { get; init; }

    public static HuggingFaceUploadFile FromPath(string pathInRepo, string localPath) =>
        new() { PathInRepo = pathInRepo, LocalPath = localPath };

    public static HuggingFaceUploadFile FromText(string pathInRepo, string text) =>
        new() { PathInRepo = pathInRepo, Content = Encoding.UTF8.GetBytes(text) };
}

/// <summary>Progress for a multi-file Hub upload.</summary>
public sealed record HuggingFaceUploadProgress(
    string Stage,
    string? FileName,
    long BytesCompleted,
    long TotalBytes,
    double? Percent);

/// <summary>The commit created by an upload.</summary>
public sealed record HuggingFaceCommitResult(
    string RepoId,
    string RepoUrl,
    string CommitUrl,
    string? CommitOid);

/// <summary>A file already committed to a model repository.</summary>
public sealed record HuggingFaceRepoFile(string Path, long? Size);

/// <summary>Visibility and freshness of an existing model repository.</summary>
public sealed record HuggingFaceRepoInfo(
    string Id,
    bool Private,
    bool Gated,
    DateTimeOffset? LastModified);

public sealed record HuggingFaceFile(
    string RepoId,
    string FileName,
    long? Size,
    string? Quantization,
    string? Sha256);

public sealed record HuggingFaceRepo(
    string Id,
    string? Author,
    long Downloads,
    long Likes,
    bool Gated,
    DateTimeOffset? LastModified,
    string? License,
    IReadOnlyList<HuggingFaceFile> Files)
{
    public int GgufFileCount => Files.Count;
}

public sealed record HuggingFaceDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double? Percent);

/// <summary>
/// Small HuggingFace Hub client for GGUF discovery and download. It only reads
/// public metadata and downloads model files; it never executes repository code.
/// </summary>
public sealed class HuggingFaceClient
{
    private const string ApiRoot = "https://huggingface.co/api/models";
    private const string FileRoot = "https://huggingface.co";
    private static readonly Regex QuantPattern = new(
        @"(?i)(IQ\d+[A-Z0-9_]*|Q\d+(?:_[A-Z0-9]+)+|Q\d+|BF16|F16|F32)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HttpClient _http;

    public HuggingFaceClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<IReadOnlyList<HuggingFaceRepo>> SearchAsync(
        string? query,
        int limit = 30,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        string url = $"{ApiRoot}?filter=gguf&sort=downloads&direction=-1&limit={limit}&full=true";
        if (!string.IsNullOrWhiteSpace(query))
            url += "&search=" + Uri.EscapeDataString(query.Trim());

        using HttpResponseMessage response = await SendAsync(url, token, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var repos = new List<HuggingFaceRepo>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return repos;

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            HuggingFaceRepo? repo = ParseRepo(item);
            if (repo is not null) repos.Add(repo);
        }

        return repos;
    }

    public async Task<IReadOnlyList<HuggingFaceFile>> GetFilesAsync(
        string repoId,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        string url = $"{ApiRoot}/{EscapeRepo(repoId)}?blobs=true";
        using HttpResponseMessage response = await SendAsync(url, token, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return ParseFiles(document.RootElement, repoId);
    }

    /// <summary>Reads the signed-in account and its organizations. Requires a token.</summary>
    public async Task<HuggingFaceUser> WhoAmIAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{FileRoot}/api/whoami-v2");
        ApplyAuth(request, token);
        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string name = ReadString(root, "name") ?? "";
        if (name.Length == 0)
            throw new HttpRequestException("HuggingFace did not return an account name.");

        var organizations = new List<string>();
        if (root.TryGetProperty("orgs", out JsonElement orgs) && orgs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement org in orgs.EnumerateArray())
            {
                string? orgName = ReadString(org, "name");
                if (!string.IsNullOrWhiteSpace(orgName)) organizations.Add(orgName);
            }
        }

        return new HuggingFaceUser(name, ReadString(root, "fullname"), organizations);
    }

    /// <summary>Creates a model repository, treating an existing repository as success.</summary>
    public async Task CreateModelRepoAsync(
        string repoId,
        string token,
        bool privateRepo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        (string? organization, string name) = SplitRepo(repoId);

        var payload = new JsonObject
        {
            ["type"] = "model",
            ["name"] = name,
            ["visibility"] = privateRepo ? "private" : "public",
        };
        if (organization is not null) payload["organization"] = organization;

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{FileRoot}/api/repos/create")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyAuth(request, token);
        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) return;

        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.PaymentRequired)
        {
            // A token can lack the "create repository" permission while still having
            // write access to an existing repository. Continue when the repo already
            // exists, which is the normal path for a second quantization.
            if (await RepoExistsAsync(repoId, token, cancellationToken).ConfigureAwait(false))
                return;

            string body = "";
            try
            {
                body = await response.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            string detail = body.Length > 500 ? body[..500] : body;
            throw new HttpRequestException(
                "HuggingFace refused to create the repository. The token needs Write access "
                + "and permission to create repositories in this namespace. "
                + "Create the repository on huggingface.co first, or use a token with write access."
                + (string.IsNullOrWhiteSpace(detail) ? "" : $" HuggingFace said: {detail.Trim()}"),
                inner: null,
                response.StatusCode);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the model repository is visible to this token.</summary>
    public async Task<bool> RepoExistsAsync(
        string repoId,
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{FileRoot}/api/models/{EscapeRepo(repoId)}");
        ApplyAuth(request, token);
        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;
        if (!response.IsSuccessStatusCode)
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Reads visibility and last-modified time, or null when the repo is missing.</summary>
    public async Task<HuggingFaceRepoInfo?> GetModelInfoAsync(
        string repoId,
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{FileRoot}/api/models/{EscapeRepo(repoId)}");
        ApplyAuth(request, token);
        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string? id = ReadString(root, "id");
        bool isPrivate = root.TryGetProperty("private", out JsonElement privateElement)
            && privateElement.ValueKind is JsonValueKind.True;
        bool gated = root.TryGetProperty("gated", out JsonElement gatedElement)
            && gatedElement.ValueKind is JsonValueKind.True;
        DateTimeOffset? modified = null;
        if (ReadString(root, "lastModified") is { } lastModified
            && DateTimeOffset.TryParse(lastModified, out DateTimeOffset parsed))
        {
            modified = parsed;
        }

        return new HuggingFaceRepoInfo(id ?? repoId, isPrivate, gated, modified);
    }

    /// <summary>Lists committed files, or an empty list when the repository is new.</summary>
    public async Task<IReadOnlyList<HuggingFaceRepoFile>> ListModelFilesAsync(
        string repoId,
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{FileRoot}/api/models/{EscapeRepo(repoId)}/tree/main?recursive=true");
        ApplyAuth(request, token);
        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var files = new List<HuggingFaceRepoFile>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (ReadString(item, "type") is { } type &&
                !string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            long? size = item.TryGetProperty("size", out JsonElement sizeElement)
                && sizeElement.ValueKind == JsonValueKind.Number
                && sizeElement.TryGetInt64(out long parsed)
                    ? parsed
                    : null;
            files.Add(new HuggingFaceRepoFile(path, size));
        }

        return files;
    }

    /// <summary>
    /// Uploads several files in one Hub commit. Large files use the LFS batch
    /// protocol; small files are embedded in the commit payload. Progress is
    /// reported for hashing and byte transfer separately.
    /// </summary>
    public async Task<HuggingFaceCommitResult> UploadFilesAsync(
        string repoId,
        IReadOnlyList<HuggingFaceUploadFile> files,
        string token,
        string commitMessage = "Upload model",
        string revision = "main",
        IProgress<HuggingFaceUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one file is required.", nameof(files));

        var entries = files.Select(UploadEntry.Create).ToList();
        long totalBytes = entries.Sum(entry => entry.Size);

        progress?.Report(new HuggingFaceUploadProgress(
            "Preparing", null, 0, totalBytes, 0));

        var preupload = new JsonObject
        {
            ["files"] = new JsonArray([.. entries.Select(entry => (JsonNode)new JsonObject
            {
                ["path"] = entry.File.PathInRepo,
                ["sample"] = Convert.ToBase64String(entry.Sample),
                ["size"] = entry.Size,
            })]),
        };

        using (var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FileRoot}/api/models/{EscapeRepo(repoId)}/preupload/{Uri.EscapeDataString(revision)}")
        {
            Content = new StringContent(
                preupload.ToJsonString(), Encoding.UTF8, "application/json"),
        })
        {
            ApplyAuth(request, token);
            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            await using Stream stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("files", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array)
            {
                throw new HttpRequestException("HuggingFace returned an invalid preupload response.");
            }

            foreach (JsonElement item in list.EnumerateArray())
            {
                string path = ReadString(item, "path") ?? "";
                UploadEntry? entry = entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.File.PathInRepo, path, StringComparison.Ordinal));
                if (entry is null) continue;

                string mode = ReadString(item, "uploadMode") ?? "regular";
                entry.UploadMode = mode;
                entry.Ignored = item.TryGetProperty("shouldIgnore", out JsonElement ignore)
                    && ignore.ValueKind is JsonValueKind.True;
            }
        }

        long hashDone = 0;
        long lfsTotal = entries.Where(entry => entry.IsLfs && !entry.Ignored)
            .Sum(entry => entry.Size);
        foreach (UploadEntry entry in entries)
        {
            if (entry.Ignored || !entry.IsLfs || entry.Sha256 is not null) continue;
            entry.Sha256 = await HashAsync(
                entry,
                bytes =>
                {
                    hashDone += bytes;
                    progress?.Report(new HuggingFaceUploadProgress(
                        "Hashing", Path.GetFileName(entry.File.PathInRepo),
                        hashDone, lfsTotal,
                        lfsTotal > 0 ? (double)hashDone / lfsTotal : 1));
                },
                cancellationToken).ConfigureAwait(false);
        }

        List<UploadEntry> lfsEntries = [.. entries
            .Where(entry => entry.IsLfs && !entry.Ignored && entry.Sha256 is not null)];

        if (lfsEntries.Count > 0)
        {
            await UploadLfsFilesAsync(
                repoId, lfsEntries, token, revision, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new HuggingFaceUploadProgress(
            "Committing", null, totalBytes, totalBytes, 1));

        string ndjson = BuildCommitPayload(entries, commitMessage);
        using var commit = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FileRoot}/api/models/{EscapeRepo(repoId)}/commit/{Uri.EscapeDataString(revision)}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ndjson)),
        };
        commit.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        ApplyAuth(commit, token);

        using HttpResponseMessage commitResponse = await _http
            .SendAsync(commit, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(commitResponse, cancellationToken).ConfigureAwait(false);

        await using Stream commitStream = await commitResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument commitDocument = await JsonDocument
            .ParseAsync(commitStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        string commitUrl = ReadString(commitDocument.RootElement, "commitUrl")
            ?? $"{FileRoot}/{repoId}/commit/{revision}";
        string? commitOid = ReadString(commitDocument.RootElement, "commitOid");

        return new HuggingFaceCommitResult(
            repoId,
            $"{FileRoot}/{repoId}",
            commitUrl,
            commitOid);
    }

    private async Task UploadLfsFilesAsync(
        string repoId,
        IReadOnlyList<UploadEntry> entries,
        string token,
        string revision,
        IProgress<HuggingFaceUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = entries.Sum(entry => entry.Size);
        var payload = new JsonObject
        {
            ["operation"] = "upload",
            ["transfers"] = new JsonArray("basic", "multipart"),
            ["objects"] = new JsonArray([.. entries.Select(entry =>
                (JsonNode)new JsonObject
                {
                    ["oid"] = entry.Sha256,
                    ["size"] = entry.Size,
                })]),
            ["hash_algo"] = "sha256",
            ["ref"] = new JsonObject { ["name"] = revision },
        };

        using var batch = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FileRoot}/{EscapeRepo(repoId)}.git/info/lfs/objects/batch")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/vnd.git-lfs+json"),
        };
        batch.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.git-lfs+json"));
        ApplyAuth(batch, token);

        using HttpResponseMessage batchResponse = await _http
            .SendAsync(batch, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(batchResponse, cancellationToken).ConfigureAwait(false);

        await using Stream batchStream = await batchResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(batchStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("objects", out JsonElement objects)
            || objects.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException("HuggingFace returned an invalid LFS batch response.");
        }

        long completed = 0;
        foreach (JsonElement item in objects.EnumerateArray())
        {
            if (item.TryGetProperty("error", out JsonElement error))
            {
                string message = error.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? "LFS upload rejected."
                    : "LFS upload rejected.";
                throw new HttpRequestException(message);
            }

            string oid = ReadString(item, "oid") ?? "";
            UploadEntry? entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Sha256, oid, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;

            if (!item.TryGetProperty("actions", out JsonElement actions)
                || actions.ValueKind != JsonValueKind.Object)
            {
                completed += entry.Size;
                continue;
            }

            JsonElement upload = actions.GetProperty("upload");
            string href = upload.GetProperty("href").GetString()
                ?? throw new HttpRequestException("LFS upload action has no URL.");
            JsonElement header = upload.TryGetProperty("header", out JsonElement headerElement)
                ? headerElement
                : default;

            await UploadLfsEntryAsync(
                entry, href, header,
                bytes =>
                {
                    completed += bytes;
                    progress?.Report(new HuggingFaceUploadProgress(
                        "Uploading",
                        Path.GetFileName(entry.File.PathInRepo),
                        completed, total,
                        total > 0 ? (double)completed / total : 1));
                },
                cancellationToken).ConfigureAwait(false);

            if (actions.TryGetProperty("verify", out JsonElement verify)
                && verify.ValueKind == JsonValueKind.Object
                && verify.TryGetProperty("href", out JsonElement verifyHref)
                && verifyHref.ValueKind == JsonValueKind.String)
            {
                using var verifyRequest = new HttpRequestMessage(
                    HttpMethod.Post, verifyHref.GetString()!)
                {
                    Content = new StringContent(
                        new JsonObject
                        {
                            ["oid"] = entry.Sha256,
                            ["size"] = entry.Size,
                        }.ToJsonString(),
                        Encoding.UTF8,
                        "application/vnd.git-lfs+json"),
                };
                ApplyAuth(verifyRequest, token);
                using HttpResponseMessage verifyResponse = await _http
                    .SendAsync(verifyRequest, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await EnsureSuccessAsync(verifyResponse, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new HuggingFaceUploadProgress(
            "Uploading", null, total, total, 1));
    }

    private async Task UploadLfsEntryAsync(
        UploadEntry entry,
        string href,
        JsonElement header,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        long chunkSize = 0;
        if (header.ValueKind == JsonValueKind.Object
            && header.TryGetProperty("chunk_size", out JsonElement chunk)
            && chunk.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            _ = long.TryParse(chunk.ToString(), out chunkSize);
        }

        if (chunkSize <= 0)
        {
            await using Stream source = entry.OpenRead();
            await using var progressStream = new ProgressReadStream(source, report);
            using var content = new StreamContent(progressStream);
            content.Headers.ContentLength = entry.Size;
            using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = content };
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (entry.File.LocalPath is null)
            throw new InvalidOperationException("Multipart LFS upload needs a local file.");

        var partUrls = new SortedDictionary<int, string>();
        foreach (JsonProperty property in header.EnumerateObject())
        {
            if (int.TryParse(property.Name, out int part)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                partUrls[part] = property.Value.GetString()!;
            }
        }

        var etags = new List<(int Part, string ETag)>();
        foreach ((int part, string partUrl) in partUrls)
        {
            await using var file = new FileStream(
                entry.File.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65_536, useAsync: true);
            long offset = (long)(part - 1) * chunkSize;
            long length = Math.Min(chunkSize, entry.Size - offset);
            if (length <= 0) continue;
            file.Position = offset;

            await using var slice = new SliceReadStream(file, length);
            await using var progressStream = new ProgressReadStream(slice, report);
            using var content = new StreamContent(progressStream);
            content.Headers.ContentLength = length;
            using var request = new HttpRequestMessage(HttpMethod.Put, partUrl) { Content = content };
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            string? etag = response.Headers.ETag?.Tag
                ?? (response.Headers.TryGetValues("ETag", out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null);
            if (string.IsNullOrWhiteSpace(etag))
                throw new HttpRequestException("LFS multipart upload returned no ETag.");
            etags.Add((part, etag));
        }

        var completion = new JsonObject
        {
            ["oid"] = entry.Sha256,
            ["parts"] = new JsonArray([.. etags.Select(item =>
                (JsonNode)new JsonObject
                {
                    ["partNumber"] = item.Part,
                    ["etag"] = item.ETag,
                })]),
        };
        using var complete = new HttpRequestMessage(HttpMethod.Post, href)
        {
            Content = new StringContent(
                completion.ToJsonString(), Encoding.UTF8, "application/vnd.git-lfs+json"),
        };
        using HttpResponseMessage completeResponse = await _http
            .SendAsync(complete, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(completeResponse, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCommitPayload(
        IReadOnlyList<UploadEntry> entries,
        string commitMessage)
    {
        var builder = new StringBuilder();
        builder.Append(JsonSerializer.Serialize(new
        {
            key = "header",
            value = new { summary = commitMessage, description = "" },
        })).Append('\n');

        foreach (UploadEntry entry in entries)
        {
            if (entry.Ignored) continue;
            if (entry.IsLfs)
            {
                builder.Append(JsonSerializer.Serialize(new
                {
                    key = "lfsFile",
                    value = new
                    {
                        path = entry.File.PathInRepo,
                        algo = "sha256",
                        oid = entry.Sha256,
                        size = entry.Size,
                    },
                })).Append('\n');
            }
            else
            {
                if (entry.Size > 50L * 1024 * 1024)
                {
                    throw new InvalidOperationException(
                        $"{entry.File.PathInRepo} was not accepted for LFS and is too large to embed in a commit.");
                }

                byte[] content = entry.ReadAllBytes();
                builder.Append(JsonSerializer.Serialize(new
                {
                    key = "file",
                    value = new
                    {
                        content = Convert.ToBase64String(content),
                        path = entry.File.PathInRepo,
                        encoding = "base64",
                    },
                })).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static async Task<string> HashAsync(
        UploadEntry entry,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        await using Stream source = entry.OpenRead();
        await using var progress = new ProgressReadStream(source, report);
        byte[] hash = await sha.ComputeHashAsync(progress, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (string? Organization, string Name) SplitRepo(string repoId)
    {
        string[] parts = repoId.Trim().Split('/');
        if (parts.Length == 0 || parts.Length > 2)
            throw new ArgumentException("Use a repository id like owner/name.", nameof(repoId));
        string name = parts[^1];
        if (name.Length == 0)
            throw new ArgumentException("The repository name is empty.", nameof(repoId));
        if (parts.Length == 1) return (null, name);
        string organization = parts[0];
        if (organization.Length == 0)
            throw new ArgumentException("The repository owner is empty.", nameof(repoId));
        return (organization, name);
    }

    private static void ApplyAuth(HttpRequestMessage request, string token)
    {
        request.Headers.UserAgent.ParseAdd("SharpMind-Studio/1.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    private sealed class UploadEntry
    {
        public required HuggingFaceUploadFile File { get; init; }
        public required long Size { get; init; }
        public required byte[] Sample { get; init; }
        public string UploadMode { get; set; } = "regular";
        public bool Ignored { get; set; }
        public string? Sha256 { get; set; }

        public bool IsLfs => string.Equals(UploadMode, "lfs", StringComparison.OrdinalIgnoreCase);

        public Stream OpenRead() => File.Content is { } content
            ? new MemoryStream(content, writable: false)
            : new FileStream(
                File.LocalPath!, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65_536, useAsync: true);

        public byte[] ReadAllBytes() => File.Content
            ?? System.IO.File.ReadAllBytes(File.LocalPath!);

        public static UploadEntry Create(HuggingFaceUploadFile file)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(file.PathInRepo);
            if (file.Content is { } content)
            {
                return new UploadEntry
                {
                    File = file,
                    Size = content.LongLength,
                    Sample = content.Length <= 512 ? content : content[..512],
                };
            }

            if (string.IsNullOrWhiteSpace(file.LocalPath))
                throw new ArgumentException("An upload needs content or a local path.", nameof(file));
            var info = new FileInfo(file.LocalPath);
            if (!info.Exists) throw new FileNotFoundException(file.LocalPath);

            byte[] sample = new byte[(int)Math.Min(512, info.Length)];
            using (var stream = new FileStream(
                file.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 512, useAsync: false))
            {
                _ = stream.Read(sample, 0, sample.Length);
            }

            return new UploadEntry
            {
                File = file,
                Size = info.Length,
                Sample = sample,
            };
        }
    }

    private sealed class ProgressReadStream(
        Stream inner,
        Action<long> report) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (read > 0) report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) report(read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SliceReadStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken)
                .ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public async Task<string> DownloadAsync(
        HuggingFaceFile file,
        string destination,
        string? token = null,
        IProgress<HuggingFaceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string full = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string part = full + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        string url = $"{FileRoot}/{EscapeRepo(file.RepoId)}/resolve/main/"
            + $"{Uri.EscapeDataString(file.FileName)}?download=true";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, token);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append && response.StatusCode != HttpStatusCode.OK)
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        if (!append) existing = 0;
        long? total = response.Content.Headers.ContentLength is { } length
            ? existing + length
            : file.Size;

        await using Stream input = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            part,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 65_536,
            useAsync: true);

        byte[] buffer = new byte[65_536];
        long received = existing;
        var lastReport = DateTime.UtcNow;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new HuggingFaceDownloadProgress(
                    received,
                    total,
                    total is > 0 ? (double)received / total.Value : null));
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Close();
        File.Move(part, full, overwrite: true);
        progress?.Report(new HuggingFaceDownloadProgress(
            received,
            total,
            total is > 0 ? 1 : null));
        return full;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, token);
        return await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? token)
    {
        request.Headers.UserAgent.ParseAdd("SharpMind-Studio/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        string message = body.Length > 400 ? body[..400] : body;
        if (string.IsNullOrWhiteSpace(message))
            message = response.ReasonPhrase ?? "HuggingFace request failed.";
        throw new HttpRequestException(
            $"HuggingFace {(int)response.StatusCode}: {message.Trim()}",
            inner: null,
            response.StatusCode);
    }

    private static HuggingFaceRepo? ParseRepo(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? id = ReadString(item, "id") ?? ReadString(item, "modelId");
        if (string.IsNullOrWhiteSpace(id)) return null;

        IReadOnlyList<string> tags = ReadStrings(item, "tags");
        bool gated = ReadGated(item);
        string? licenseTag = tags
            .FirstOrDefault(tag => tag.StartsWith("license:", StringComparison.OrdinalIgnoreCase));
        string? license = licenseTag is null ? null : licenseTag["license:".Length..];
        DateTimeOffset? lastModified = null;
        if (ReadString(item, "lastModified") is { } modified
            && DateTimeOffset.TryParse(modified, out DateTimeOffset parsed))
            lastModified = parsed;

        return new HuggingFaceRepo(
            id,
            ReadString(item, "author"),
            ReadLong(item, "downloads"),
            ReadLong(item, "likes"),
            gated,
            lastModified,
            license,
            ParseFiles(item, id));
    }

    private static IReadOnlyList<HuggingFaceFile> ParseFiles(JsonElement item, string repoId)
    {
        if (!item.TryGetProperty("siblings", out JsonElement siblings)
            || siblings.ValueKind != JsonValueKind.Array)
            return [];

        var files = new List<HuggingFaceFile>();
        foreach (JsonElement sibling in siblings.EnumerateArray())
        {
            if (sibling.ValueKind != JsonValueKind.Object) continue;
            string? fileName = ReadString(sibling, "rfilename");
            if (string.IsNullOrWhiteSpace(fileName)
                || !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                continue;

            long? size = ReadLongNullable(sibling, "size");
            string? sha = null;
            if (sibling.TryGetProperty("lfs", out JsonElement lfs)
                && lfs.ValueKind == JsonValueKind.Object)
            {
                size ??= ReadLongNullable(lfs, "size");
                sha = ReadString(lfs, "sha256");
            }

            files.Add(new HuggingFaceFile(
                repoId,
                fileName,
                size,
                DetectQuantization(fileName),
                sha));
        }

        return [.. files.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    public static string? DetectQuantization(string fileName)
    {
        Match match = QuantPattern.Match(fileName);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static bool ReadGated(JsonElement item)
    {
        if (!item.TryGetProperty("gated", out JsonElement gated)) return false;
        return gated.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => !string.Equals(
                gated.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name)
        => ReadLongNullable(element, name) ?? 0;

    private static long? ReadLongNullable(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out long parsed) => parsed,
            _ => null,
        };
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
            return [];
        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")];
    }

    private static string EscapeRepo(string repoId) =>
        string.Join('/', repoId.Split('/').Select(Uri.EscapeDataString));
}
