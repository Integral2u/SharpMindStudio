using System.Text;
using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Core.A2ui;

/// <summary>Reads and writes the Studio A2UI wrapper as a chat artifact.</summary>
public static class A2uiArtifact
{
    public const string Type = A2uiProtocol.ArtifactType;
    public const string MimeType = A2uiProtocol.ArtifactMimeType;

    public static bool IsA2ui(ChatArtifact? artifact) =>
        artifact is not null &&
        string.Equals(artifact.Type, Type, StringComparison.OrdinalIgnoreCase);

    public static ChatArtifact Create(A2uiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string name = document.SurfaceId ?? "surface";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return new ChatArtifact
        {
            Type = Type,
            MimeType = MimeType,
            FileName = $"surface-{name}.json",
            Content = Encoding.UTF8.GetBytes(document.ToJson()),
        };
    }

    public static bool TryRead(
        ChatArtifact? artifact,
        out A2uiDocument document,
        out IReadOnlyList<string> errors)
    {
        document = new A2uiDocument();
        errors = [];
        if (!IsA2ui(artifact))
        {
            errors = ["The artifact is not an A2UI surface."];
            return false;
        }

        string json;
        try
        {
            json = Encoding.UTF8.GetString(artifact!.Content ?? []);
        }
        catch (Exception ex)
        {
            errors = [$"Could not read the A2UI artifact: {ex.Message}"];
            return false;
        }

        return A2uiDocument.TryParse(json, out document, out errors);
    }
}
