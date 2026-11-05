using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.HuggingFace;

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
