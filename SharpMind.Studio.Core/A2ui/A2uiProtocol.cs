using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.A2ui;

/// <summary>
/// Protocol constants and the small catalog subset the native renderer
/// implements. The catalog id is the official A2UI 1.0 basic catalog so an
/// external A2UI server can target the same contract; unsupported components
/// render as a neutral placeholder instead of taking down the surface.
/// </summary>
public static partial class A2uiProtocol
{
    public const string Version = "1.0";
    public const string BasicCatalogId =
        "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json";
    public const string DocumentSchema = "studio-a2ui/1";
    public const string ArtifactType = "a2ui";
    public const string ArtifactMimeType = "application/vnd.sharpmind.a2ui+json";

    public const int MaxPayloadBytes = 512 * 1024;
    public const int MaxMessages = 256;
    public const int MaxComponentsPerSurface = 256;
    public const int MaxSurfaces = 16;
    public const int MaxDepth = 32;
    public const int MaxTextLength = 16 * 1024;
    public const int MaxDataModelBytes = 128 * 1024;

    private static readonly HashSet<string> Components = new(StringComparer.Ordinal)
    {
        "Text",
        "Column",
        "Row",
        "Card",
        "Divider",
        "List",
        "Button",
        "TextField",
        "CheckBox",
        "ChoicePicker",
    };

    public static IReadOnlyCollection<string> SupportedComponents => Components;

    public static bool IsSupportedComponent(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Components.Contains(name);

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);

    /// <summary>Surface ids are routing keys, not catalog entities. The A2UI
    /// examples use hyphenated ids such as <c>recipe-card</c>, so allow a safe
    /// key set without whitespace, quotes or path separators.</summary>
    public static bool IsValidSurfaceId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && SurfaceIdPattern().IsMatch(id);

    public static string NormalizeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? Version
            : version.Trim().TrimStart('v', 'V');

    public static bool IsSupportedVersion(string? version) =>
        string.Equals(NormalizeVersion(version), Version, StringComparison.Ordinal);

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SurfaceIdPattern();
}
