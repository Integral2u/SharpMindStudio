using System.Globalization;
using System.Text.Json;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed class StudioSettings
{
    public string Theme { get; set; } = ThemeService.DefaultTheme;
    public string CustomColor { get; set; } = ThemeService.DefaultCustomColor;
    public string DefaultGenerator { get; set; } = ThemeService.DefaultGeneratorName;
    public string DefaultCache { get; set; } = ThemeService.DefaultCacheName;
    public string? LastChatModel { get; set; }
}

/// <summary>
/// A tiny server-side preference store. Theme is deliberately a file next to
/// the Studio project rather than browser state so a local multi-window setup
/// stays consistent.
/// </summary>
public sealed class ThemeService
{
    public const string DefaultTheme = "green";
    public const string DefaultCustomColor = "#5eead4";
    public const string DefaultGeneratorName = "StandardGeneratorBuilder";
    public const string DefaultCacheName = "KVCacherBuilder";
    public static readonly string[] AvailableThemes = ["green", "blue", "purple", "red", "custom"];

    private static readonly Dictionary<string, string> PresetRgb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"] = "94, 234, 212",
        ["blue"] = "96, 165, 250",
        ["purple"] = "192, 132, 252",
        ["red"] = "251, 113, 133",
    };

    public ThemeService()
    {
        StudioPaths.EnsureCreated();
        (Theme, CustomColor, DefaultGenerator, DefaultCache, LastChatModel) = LoadSettings();
    }

    public event Action? Changed;
    public string Theme { get; private set; }
    public string CustomColor { get; private set; }
    public string DefaultGenerator { get; private set; }
    public string DefaultCache { get; private set; }
    public string? LastChatModel { get; private set; }
    // --accent is declared on :root in app.css, so overriding only
    // --accent-rgb on .app-shell would leave the inherited, already-computed
    // --accent at the default green. Set both on the themed element.
    public string CssVariables =>
        $"--accent-rgb: {AccentRgb}; --accent: rgb({AccentRgb}); --accent-dim: rgba({AccentRgb}, .12);";
    public string AccentRgb => Theme.Equals("custom", StringComparison.OrdinalIgnoreCase)
        ? HexToRgb(CustomColor)
        : PresetRgb.GetValueOrDefault(Theme, PresetRgb[DefaultTheme]);

    public async Task SetThemeAsync(
        string? theme,
        string? customColor = null,
        CancellationToken cancellationToken = default)
    {
        string? normalized = AvailableThemes.FirstOrDefault(
            value => value.Equals(theme, StringComparison.OrdinalIgnoreCase));
        if (normalized is null) return;

        string color = NormalizeColor(customColor) ?? CustomColor;
        Theme = normalized;
        CustomColor = color;

        try
        {
            var settings = new StudioSettings {
                Theme = normalized,
                CustomColor = color,
                DefaultGenerator = DefaultGenerator,
                DefaultCache = DefaultCache,
                LastChatModel = LastChatModel,
            };
            await File.WriteAllTextAsync(
                StudioPaths.SettingsFile,
                JsonSerializer.Serialize(settings, StudioJson.File),
                cancellationToken);
        }
        catch
        {
            // A read-only profile should not break the UI; the in-memory theme still applies.
        }

        Changed?.Invoke();
    }


    public async Task SetInferenceDefaultsAsync(
        string? generator,
        string? cache,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(generator)) DefaultGenerator = generator.Trim();
        if (!string.IsNullOrWhiteSpace(cache)) DefaultCache = cache.Trim();

        await SaveSettingsAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task SetLastChatModelAsync(string? modelPath, CancellationToken cancellationToken = default)
    {
        LastChatModel = string.IsNullOrWhiteSpace(modelPath) ? null : modelPath.Trim();
        await SaveSettingsAsync(cancellationToken);
        Changed?.Invoke();
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = new StudioSettings
            {
                Theme = Theme,
                CustomColor = CustomColor,
                DefaultGenerator = DefaultGenerator,
                DefaultCache = DefaultCache,
                LastChatModel = LastChatModel,
            };
            await File.WriteAllTextAsync(
                StudioPaths.SettingsFile,
                JsonSerializer.Serialize(settings, StudioJson.File),
                cancellationToken);
        }
        catch
        {
            // A read-only profile should not break the UI; in-memory settings still apply.
        }
    }    public static string Label(string theme) =>
        theme.Equals("custom", StringComparison.OrdinalIgnoreCase)
            ? "Custom"
            : char.ToUpperInvariant(theme[0]) + theme[1..];

    public static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length is not (4 or 7)) return null;
        if (!text[1..].All(Uri.IsHexDigit)) return null;
        return text.ToLowerInvariant();
    }

    private static (string Theme, string CustomColor, string Generator, string Cache, string? LastChatModel) LoadSettings()
    {
        try
        {
            if (!File.Exists(StudioPaths.SettingsFile))
                return (DefaultTheme, DefaultCustomColor, DefaultGeneratorName, DefaultCacheName, null);

            var settings = JsonSerializer.Deserialize<StudioSettings>(
                File.ReadAllText(StudioPaths.SettingsFile), StudioJson.File);
            string theme = AvailableThemes.FirstOrDefault(
                value => value.Equals(settings?.Theme, StringComparison.OrdinalIgnoreCase)) ?? DefaultTheme;
            string color = NormalizeColor(settings?.CustomColor) ?? DefaultCustomColor;
            string generator = string.IsNullOrWhiteSpace(settings?.DefaultGenerator)
                ? DefaultGeneratorName
                : settings!.DefaultGenerator!;
            string cache = string.IsNullOrWhiteSpace(settings?.DefaultCache)
                ? DefaultCacheName
                : settings!.DefaultCache!;
            return (theme, color, generator, cache, string.IsNullOrWhiteSpace(settings?.LastChatModel) ? null : settings!.LastChatModel);
        }
        catch
        {
            return (DefaultTheme, DefaultCustomColor, DefaultGeneratorName, DefaultCacheName, null);
        }
    }

    private static string HexToRgb(string color)
    {
        string hex = color.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => $"{c}{c}"));

        if (hex.Length != 6 ||
            !int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) ||
            !int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) ||
            !int.TryParse(hex[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
            return PresetRgb[DefaultTheme];

        return $"{r}, {g}, {b}";
    }
}










