using System.Text;
using System.Text.RegularExpressions;
using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Services;

/// <summary>
/// Turns generated content into downloadable artifacts: data-URL images/audio and
/// fenced code blocks. The model or a tool can also attach artifacts directly;
/// this only fills the gap for plain text responses.
/// </summary>
public sealed partial class StudioArtifactPostProcessor : IPromptPostProcessor
{
    private const int MaxArtifactBytes = 16 * 1024 * 1024;

    public string Name => "Studio artifacts";
    public string Description => "Attaches data-URL media and fenced code blocks to the response.";

    public Task ProcessAsync(
        ChatMessage agentMessage,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentMessage.Content)) return Task.CompletedTask;

        var artifacts = new List<ChatArtifact>(agentMessage.Artifacts ?? []);
        int added = 0;

        foreach (Match match in DataUrlRegex().Matches(agentMessage.Content))
        {
            try
            {
                string kind = match.Groups["kind"].Value.ToLowerInvariant();
                string subtype = match.Groups["subtype"].Value.ToLowerInvariant();
                string payload = Regex.Replace(match.Groups["data"].Value, @"\s+", "");
                byte[] bytes = Convert.FromBase64String(payload);
                if (bytes.Length == 0 || bytes.Length > MaxArtifactBytes) continue;

                artifacts.Add(new ChatArtifact
                {
                    Type = $"{kind}/{subtype}",
                    Content = bytes,
                    FileName = $"generated-{artifacts.Count + 1}.{Extension(kind, subtype)}",
                });
                added++;
            }
            catch (FormatException)
            {
            }
        }

        foreach (Match match in CodeFenceRegex().Matches(agentMessage.Content))
        {
            string language = match.Groups["language"].Value.Trim().ToLowerInvariant();
            string code = match.Groups["code"].Value.Trim();
            if (code.Length == 0 || code.Length > MaxArtifactBytes) continue;

            artifacts.Add(new ChatArtifact
            {
                Type = language is "json" ? "json" : "code",
                Language = language.Length == 0 ? null : language,
                Content = Encoding.UTF8.GetBytes(code + "\n"),
                FileName = $"response-{artifacts.Count + 1}.{CodeExtension(language)}",
            });
            added++;
        }

        if (added > 0)
            agentMessage.Artifacts = [.. artifacts];

        return Task.CompletedTask;
    }

    private static string Extension(string kind, string subtype) => kind switch
    {
        "image" => subtype switch
        {
            "jpeg" or "jpg" => "jpg",
            "svg+xml" => "svg",
            _ => subtype,
        },
        "audio" => subtype switch
        {
            "mpeg" => "mp3",
            "mp4" => "m4a",
            _ => subtype,
        },
        _ => "bin",
    };

    private static string CodeExtension(string language) => language switch
    {
        "json" => "json",
        "html" or "htm" => "html",
        "css" => "css",
        "js" or "javascript" => "js",
        "ts" or "typescript" => "ts",
        "xml" => "xml",
        "yaml" or "yml" => "yaml",
        "sql" => "sql",
        "python" or "py" => "py",
        "csharp" or "cs" => "cs",
        "bash" or "sh" or "shell" => "sh",
        "csv" => "csv",
        _ => "txt",
    };

    [GeneratedRegex(
        @"data:(?<kind>image|audio)/(?<subtype>[a-zA-Z0-9.+-]+);base64,(?<data>[A-Za-z0-9+/=\s]+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex DataUrlRegex();

    [GeneratedRegex(
        @"```(?<language>[^\r\n`]*)\r?\n(?<code>[\s\S]*?)```",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex CodeFenceRegex();
}
