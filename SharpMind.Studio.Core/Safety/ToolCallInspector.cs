using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Safety;

/// <summary>How much a finding matters. The configured threshold decides whether it blocks.</summary>
public enum SafetySeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>One reason a tool call looks unsafe.</summary>
public sealed record SafetyFinding(
    string Rule,
    SafetySeverity Severity,
    string Message,
    string? Detail = null);

/// <summary>Everything the inspector needs to judge one tool call.</summary>
public sealed record SafetyRequest(
    string Tool,
    string Arguments,
    string Conversation,
    IReadOnlyCollection<string> DeclaredTools,
    IReadOnlyCollection<string> AllowedHosts)
{
    public static SafetyRequest For(
        string tool,
        string arguments,
        string conversation,
        IReadOnlyCollection<string>? declaredTools = null,
        IReadOnlyCollection<string>? allowedHosts = null) =>
        new(tool,
            arguments ?? "",
            conversation ?? "",
            declaredTools ?? [],
            allowedHosts ?? []);
}

/// <summary>
/// Deterministic checks on a tool call before Studio runs it. This is the part
/// of Hugging Face's safeagents idea that belongs in the harness: Studio already
/// sees every call, so it can refuse one without a proxy, without a model and
/// without a second process.
/// </summary>
/// <remarks>
/// It is deliberately conservative about what counts as a host. A bare filename
/// like <c>hello.txt</c> must not look like a domain, or every write would be
/// flagged. Only URLs, IPv4 addresses and tokens whose suffix is a common public
/// TLD are treated as hosts.
/// </remarks>
public static class ToolCallInspector
{
    private static readonly Regex UrlHost = new(
        @"(?:(?:https?|ftp)://|//)([^/\s'""?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareHost = new(
        @"(?<![\w.@/-])((?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,})(?![\w.-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IPv4 = new(
        @"(?<![\d.])((?:\d{1,3}\.){3}\d{1,3})(?![\d.])",
        RegexOptions.Compiled);

    private static readonly string[] PublicTlds =
    [
        "com", "net", "org", "io", "ai", "dev", "app", "co", "uk", "de", "fr",
        "nl", "ru", "cn", "xyz", "info", "me", "tv", "cc", "to", "sh", "gg",
        "live", "cloud", "site", "online", "tech", "store", "link", "top", "biz",
    ];

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "json", "jsonl", "cs", "py", "js", "ts", "tsx", "jsx",
        "csv", "tsv", "log", "yaml", "yml", "xml", "html", "css", "scss",
        "mm", "gguf", "smm", "dll", "exe", "zip", "tar", "gz", "png", "jpg",
        "jpeg", "webp", "gif", "svg", "pdf", "wav", "mp3", "mp4", "sln", "csproj",
        "props", "targets", "lock", "toml", "ini", "cfg", "conf", "sh", "ps1",
        "bat", "cmd", "sql", "db", "bin", "dat", "idx", "safetensors",
    };

    private static readonly string[] NetworkClients =
    [
        "curl", "wget", "httpie", "invoke-webrequest", "invoke-restmethod",
        "start-bitstransfer", "nc ", "ncat", "netcat", "socat", "scp ",
        "ssh ", "ftp ", "sftp", "/dev/tcp/", "system.net.webclient",
        "system.net.http", "requests.", "urllib", "httpx", "axios", "fetch(",
    ];

    private static readonly string[] CredentialMarkers =
    [
        ".env", "./.env", ".netrc", "id_rsa", "id_ed25519", "credentials",
        ".aws", ".ssh", "hf_token", "huggingface_token", "github_token",
        "openai_api_key", "anthropic_api_key", "aws_secret_access_key",
        "aws_access_key_id", "api_key", "apikey", "access_token", "client_secret",
        "password", "passwd", "authorization: bearer", ".pem", ".pfx", ".key",
    ];

    private static readonly string[] MetadataEndpoints =
    [
        "169.254.169.254", "metadata.google.internal", "metadata.azure.com",
        "100.100.100.200",
    ];

    private static readonly string[] DefaultAllowedHosts =
    [
        "localhost", "127.0.0.1", "::1",
        "github.com", "raw.githubusercontent.com", "api.github.com",
        "huggingface.co", "hf.co", "cdn-lfs.huggingface.co",
        "pypi.org", "files.pythonhosted.org",
        "registry.npmjs.org", "npmjs.com",
        "crates.io", "static.crates.io",
    ];

    public static IReadOnlyList<SafetyFinding> Inspect(SafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var findings = new List<SafetyFinding>();
        string args = request.Arguments;
        string lower = args.ToLowerInvariant();

        if (request.DeclaredTools.Count > 0 &&
            !request.DeclaredTools.Contains(request.Tool, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new SafetyFinding(
                "undeclared-tool",
                SafetySeverity.Critical,
                $"The model called '{request.Tool}', which is not one of the tools offered in this session.",
                request.Tool));
        }

        if (MetadataEndpoints.Any(lower.Contains))
        {
            findings.Add(new SafetyFinding(
                "metadata-endpoint",
                SafetySeverity.Critical,
                "The call reaches a cloud metadata endpoint, which is how credentials are harvested.",
                MetadataEndpoints.First(lower.Contains)));
        }

        bool network = NetworkClients.Any(lower.Contains);
        string? credential = CredentialMarkers.FirstOrDefault(lower.Contains);
        if (network && credential is not null)
        {
            findings.Add(new SafetyFinding(
                "credential-to-network",
                SafetySeverity.Critical,
                "The call combines a network client with something that looks like a credential or secret file.",
                credential));
        }

        if (!network && credential is not null && lower.Contains("read"))
        {
            findings.Add(new SafetyFinding(
                "secret-file",
                SafetySeverity.High,
                "The call reads what looks like a credential or secret file.",
                credential));
        }

        if (lower.Contains("file://"))
        {
            findings.Add(new SafetyFinding(
                "file-url",
                SafetySeverity.Medium,
                "The call uses a file:// URL, which can read local files through a network tool.",
                "file://"));
        }

        foreach (string host in Hosts(args))
        {
            if (IsAllowed(host, request)) continue;
            findings.Add(new SafetyFinding(
                "unmentioned-host",
                SafetySeverity.High,
                $"The call contacts '{host}', which does not appear in the conversation.",
                host));
        }

        return findings;
    }

    /// <summary>The highest severity in a set, for a single-line summary.</summary>
    public static SafetySeverity Highest(IEnumerable<SafetyFinding> findings)
    {
        SafetySeverity highest = SafetySeverity.Low;
        foreach (SafetyFinding finding in findings)
        {
            if (finding.Severity > highest) highest = finding.Severity;
        }
        return highest;
    }

    public static bool Blocks(SafetyFinding finding, SafetySeverity threshold) =>
        finding.Severity >= threshold;

    private static IEnumerable<string> Hosts(string text)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in UrlHost.Matches(text))
            hosts.Add(Clean(match.Groups[1].Value));

        foreach (Match match in IPv4.Matches(text))
            hosts.Add(match.Groups[1].Value);

        foreach (Match match in BareHost.Matches(text))
        {
            string host = match.Groups[1].Value;
            string extension = host[(host.LastIndexOf('.') + 1)..];
            if (FileExtensions.Contains(extension)) continue;
            if (!PublicTlds.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            hosts.Add(host);
        }

        hosts.Remove("");
        return hosts;
    }

    private static string Clean(string host)
    {
        string value = host.Trim().TrimEnd('.');
        int colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(']') < colon) value = value[..colon];
        return value.Trim('[', ']').ToLowerInvariant();
    }

    private static bool IsAllowed(string host, SafetyRequest request)
    {
        if (DefaultAllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return true;
        if (request.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return true;

        // A host the user or system prompt named is a host the model may use.
        return request.Conversation.Contains(host, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> DefaultAllowlist => DefaultAllowedHosts;
}
