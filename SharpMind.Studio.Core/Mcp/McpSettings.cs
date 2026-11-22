using System.Text.Json;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// One MCP server Studio can start or call. A local server is only launched
/// when a session actually enables one of its tools, never at startup. An HTTP
/// server opens a session on the first connect and keeps it until idle.
/// </summary>
public sealed record McpServerSettings
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Transport { get; init; } = McpTransports.Stdio;
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string Url { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; init; } = true;

    /// <summary>Stop the process or session after this many idle seconds; 0 keeps it alive for the session.</summary>
    public int IdleSeconds { get; init; } = 300;

    public bool IsHttp => McpTransports.IsHttp(Transport);

    public bool IsValid => Id.Length > 0 && (IsHttp
        ? !string.IsNullOrWhiteSpace(Url) &&
          Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) &&
          uri.Scheme is "http" or "https"
        : Command.Length > 0);
}

public static class McpTransports
{
    public const string Stdio = "stdio";
    public const string Http = "http";

    public static string Normalize(string? value) =>
        string.Equals(value, Http, StringComparison.OrdinalIgnoreCase) ? Http : Stdio;

    public static bool IsHttp(string? value) =>
        string.Equals(value, Http, StringComparison.OrdinalIgnoreCase);
}

public sealed record McpSettingsDocument
{
    public const string CurrentSchema = "studio-mcp/1";

    public string Schema { get; init; } = CurrentSchema;
    public List<McpServerSettings> Servers { get; init; } = [];
}

/// <summary>
/// Reads and writes the MCP server registry. Tools found on a server are not
/// stored here: they are discovered at connect time and their permissions live
/// in the same per-tool matrix as every other tool.
/// </summary>
public sealed class McpSettingsStore
{
    public const string FileName = "mcp.json";

    private readonly Lock _gate = new();
    private McpSettingsDocument _document;

    public McpSettingsStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = System.IO.Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
        Path = System.IO.Path.Combine(Folder, FileName);
        _document = Load();
    }

    public string Folder { get; }
    public string Path { get; }

    public IReadOnlyList<McpServerSettings> Servers
    {
        get { lock (_gate) return [.. _document.Servers]; }
    }

    public IReadOnlyList<McpServerSettings> EnabledServers
    {
        get { lock (_gate) return [.. _document.Servers.Where(server => server.Enabled && server.IsValid)]; }
    }

    public McpServerSettings? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            return _document.Servers.FirstOrDefault(server =>
                string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(McpServerSettings server)
    {
        ArgumentNullException.ThrowIfNull(server);
        McpServerSettings normalized = server with
        {
            Id = server.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(server.Name) ? server.Id.Trim() : server.Name.Trim(),
            Transport = McpTransports.Normalize(server.Transport),
            Command = (server.Command ?? "").Trim(),
            Url = (server.Url ?? "").Trim(),
            Args = [.. (server.Args ?? []).Where(arg => arg.Length > 0)],
            Environment = server.Environment is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(
                    server.Environment, StringComparer.OrdinalIgnoreCase),
            Headers = server.Headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(
                    server.Headers, StringComparer.OrdinalIgnoreCase),
            IdleSeconds = Math.Clamp(server.IdleSeconds, 0, 24 * 60 * 60),
        };

        if (!normalized.IsValid)
            throw new ArgumentException(
                "An MCP server needs an id and a command, or an id and an http(s) URL.",
                nameof(server));

        lock (_gate)
        {
            int index = _document.Servers.FindIndex(existing =>
                string.Equals(existing.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _document.Servers[index] = normalized;
            else _document.Servers.Add(normalized);
            SaveLocked();
        }
    }

    public void Remove(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            _document.Servers.RemoveAll(server =>
                string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveLocked();
        }
    }

    private McpSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new McpSettingsDocument();
            McpSettingsDocument document = JsonFile.Read<McpSettingsDocument>(Path);
            if (document.Schema != McpSettingsDocument.CurrentSchema) return new McpSettingsDocument();

            return document with
            {
                Servers = [.. (document.Servers ?? [])
                    .Where(server => !string.IsNullOrWhiteSpace(server.Id))
                    .Select(server => server with
                    {
                        Transport = McpTransports.Normalize(server.Transport),
                        Command = server.Command ?? "",
                        Url = server.Url ?? "",
                        Args = [.. (server.Args ?? [])],
                        Environment = server.Environment is null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(
                                server.Environment, StringComparer.OrdinalIgnoreCase),
                        Headers = server.Headers is null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(
                                server.Headers, StringComparer.OrdinalIgnoreCase),
                    })],
            };
        }
        catch
        {
            return new McpSettingsDocument();
        }
    }

    private void SaveLocked()
    {
        try
        {
            JsonFile.Write(Path, _document);
        }
        catch
        {
            // A read-only profile must not stop Studio.
        }
    }
}
