using System.Globalization;
using System.Text.Json;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed class StudioSettings
{
    public string Theme { get; set; } = ThemeService.DefaultTheme;
    public string Appearance { get; set; } = ThemeService.DefaultAppearance;
    public string CustomColor { get; set; } = ThemeService.DefaultCustomColor;
    public string DefaultGenerator { get; set; } = ThemeService.DefaultGeneratorName;
    public string DefaultCache { get; set; } = ThemeService.DefaultCacheName;
    public string? LastChatModel { get; set; }
    public string? LastRemoteProvider { get; set; }
    public string? PluginCompactor { get; set; }
    public string StudioHost { get; set; } = ThemeService.DefaultHost;
    public int StudioPort { get; set; } = ThemeService.DefaultPort;
    public bool CloseToTray { get; set; } = true;
}

/// <summary>
/// A tiny server-side preference store. Theme is deliberately a file next to
/// the Studio project rather than browser state so a local multi-window setup
/// stays consistent.
/// </summary>
public sealed class ThemeService
{
    public const string DefaultTheme = "green";
    public const string DefaultAppearance = "dark";
    public const string DefaultCustomColor = "#5eead4";
    public const string DefaultGeneratorName = "StandardGeneratorBuilder";
    public const string DefaultCacheName = "KVCacherBuilder";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 5117;
    public static readonly string[] AvailableThemes = ["green", "blue", "purple", "red", "custom"];
    public static readonly string[] AvailableAppearances = ["dark", "light"];

    // Dark accents are the light Tailwind tints; light accents are the 600
    // shades, which keep enough contrast on a white surface.
    private static readonly Dictionary<string, (string Dark, string Light)> PresetRgb =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["green"] = ("94, 234, 212", "13, 148, 136"),
            ["blue"] = ("96, 165, 250", "37, 99, 235"),
            ["purple"] = ("192, 132, 252", "124, 58, 237"),
            ["red"] = ("251, 113, 133", "225, 29, 72"),
        };

    public ThemeService()
    {
        StudioPaths.EnsureCreated();
        StudioSettings settings = LoadSettings();
        Theme = settings.Theme;
        Appearance = settings.Appearance;
        CustomColor = settings.CustomColor;
        DefaultGenerator = settings.DefaultGenerator;
        DefaultCache = settings.DefaultCache;
        LastChatModel = settings.LastChatModel;
        LastRemoteProvider = settings.LastRemoteProvider;
        PluginCompactor = settings.PluginCompactor;
        StudioHost = settings.StudioHost;
        StudioPort = settings.StudioPort;
        CloseToTray = settings.CloseToTray;

        // A custom colour saved for one appearance may be unusable in the other
        // (near-white on a white surface, near-black on a dark one). Move it to
        // the nearest allowed value once and keep the file in step.
        string fitted = ClampColor(CustomColor, Appearance);
        if (!string.Equals(fitted, CustomColor, StringComparison.OrdinalIgnoreCase))
        {
            CustomColor = fitted;
            TryWriteSettings();
        }
    }

    public event Action? Changed;
    public string Theme { get; private set; }
    public string Appearance { get; private set; }
    public string CustomColor { get; private set; }
    public string DefaultGenerator { get; private set; }
    public string DefaultCache { get; private set; }
    public string? LastChatModel { get; private set; }
    public string? LastRemoteProvider { get; private set; }
    public string? PluginCompactor { get; private set; }
    public string StudioHost { get; private set; }
    public int StudioPort { get; private set; }
    public bool CloseToTray { get; private set; }

    // --accent is declared on :root in app.css, so overriding only
    // --accent-rgb on the themed element would leave the inherited, already-computed
    // --accent at the default green. Set both on the themed element.
    // The series colours are derived from the accent hue so a chart line never
    // lands on the same colour as a custom accent: they sit 180 and 90 degrees
    // away with a lightness that suits the appearance.
    public string CssVariables
    {
        get
        {
            (double h, double s, _) = RgbCsvToHsl(AccentRgb);
            string second = FromHsl((h + 0.5) % 1, Math.Max(s, 0.45), IsLight ? 0.40 : 0.66);
            string third = FromHsl((h + 0.25) % 1, Math.Max(s, 0.40), IsLight ? 0.46 : 0.58);

            return $"--accent-rgb: {AccentRgb}; --accent: rgb({AccentRgb}); " +
                   $"--accent-dim: rgba({AccentRgb}, .12); " +
                   $"--series-1: rgb({AccentRgb}); --series-2: {second}; --series-3: {third};";
        }
    }

    public string AccentRgb
    {
        get
        {
            if (Theme.Equals("custom", StringComparison.OrdinalIgnoreCase))
                return HexToRgb(ClampColor(CustomColor, Appearance));

            (string dark, string light) = PresetRgb.GetValueOrDefault(Theme, PresetRgb[DefaultTheme]);
            return IsLight ? light : dark;
        }
    }

    public bool IsLight => Appearance.Equals("light", StringComparison.OrdinalIgnoreCase);

    public async Task SetThemeAsync(
        string? theme,
        string? customColor = null,
        string? appearance = null,
        CancellationToken cancellationToken = default)
    {
        string? normalized = AvailableThemes.FirstOrDefault(
            value => value.Equals(theme, StringComparison.OrdinalIgnoreCase));
        if (normalized is null) return;

        string targetAppearance = AvailableAppearances.FirstOrDefault(
            value => value.Equals(appearance, StringComparison.OrdinalIgnoreCase)) ?? Appearance;

        Theme = normalized;
        Appearance = targetAppearance;
        CustomColor = ClampColor(NormalizeColor(customColor) ?? CustomColor, targetAppearance);

        await SaveSettingsAsync(cancellationToken);
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

    /// <summary>
    /// The remote provider used by the newest chat, or null when the last chat
    /// was local or a Compute host. New chats start on the same provider and its
    /// most recent model.
    /// </summary>
    public async Task SetLastRemoteProviderAsync(
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        LastRemoteProvider = string.IsNullOrWhiteSpace(providerId)
            ? null
            : providerId.Trim();
        await SaveSettingsAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>The plugin context compactor selected in Settings; blank means none.</summary>
    public async Task SetPluginCompactorAsync(string? name, CancellationToken cancellationToken = default)
    {
        PluginCompactor = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        await SaveSettingsAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task SetServerAsync(string? host, int port, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(host)) StudioHost = host.Trim();
        if (port is > 0 and <= 65535) StudioPort = port;
        await SaveSettingsAsync(cancellationToken);
        Changed?.Invoke();
    }

    public Task ResetServerAsync(CancellationToken cancellationToken = default) =>
        SetServerAsync(DefaultHost, DefaultPort, cancellationToken);

    public async Task SetCloseToTrayAsync(bool closeToTray, CancellationToken cancellationToken = default)
    {
        CloseToTray = closeToTray;
        await SaveSettingsAsync(cancellationToken);
        Changed?.Invoke();
    }

    private StudioSettings Snapshot() => new()
    {
        Theme = Theme,
        Appearance = Appearance,
        CustomColor = CustomColor,
        DefaultGenerator = DefaultGenerator,
        DefaultCache = DefaultCache,
        LastChatModel = LastChatModel,
        LastRemoteProvider = LastRemoteProvider,
        PluginCompactor = PluginCompactor,
        StudioHost = StudioHost,
        StudioPort = StudioPort,
        CloseToTray = CloseToTray,
    };

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(
                StudioPaths.SettingsFile,
                JsonSerializer.Serialize(Snapshot(), StudioJson.File),
                cancellationToken);
        }
        catch
        {
            // A read-only profile should not break the UI; in-memory settings still apply.
        }
    }

    private void TryWriteSettings()
    {
        try
        {
            File.WriteAllText(
                StudioPaths.SettingsFile,
                JsonSerializer.Serialize(Snapshot(), StudioJson.File));
        }
        catch
        {
        }
    }

    public static string Label(string theme) =>
        theme.Equals("custom", StringComparison.OrdinalIgnoreCase)
            ? "Custom"
            : char.ToUpperInvariant(theme[0]) + theme[1..];

    public static string LabelAppearance(string appearance) =>
        appearance.Equals("light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";

    public static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length is not (4 or 7)) return null;
        if (!text[1..].All(Uri.IsHexDigit)) return null;
        return text.ToLowerInvariant();
    }

    /// <summary>
    /// Keeps an accent readable for the appearance. On light surfaces a
    /// near-white accent disappears, and on dark surfaces a near-black one does;
    /// the hue and saturation are preserved and only the lightness moves to the
    /// nearest allowed value.
    /// </summary>
    public static string ClampColor(string color, string appearance)
    {
        string? normalized = NormalizeColor(color);
        if (normalized is null) return DefaultCustomColor;

        (double h, double s, double l) = ToHsl(normalized);
        (double min, double max) = appearance.Equals("light", StringComparison.OrdinalIgnoreCase)
            ? (0.28, 0.60)
            : (0.35, 0.85);

        double clamped = Math.Clamp(l, min, max);
        return Math.Abs(clamped - l) < 0.001 ? normalized : FromHsl(h, s, clamped);
    }

    private static StudioSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(StudioPaths.SettingsFile)) return new StudioSettings();

            StudioSettings? settings = JsonSerializer.Deserialize<StudioSettings>(
                File.ReadAllText(StudioPaths.SettingsFile), StudioJson.File);
            if (settings is null) return new StudioSettings();

            settings.Theme = AvailableThemes.FirstOrDefault(
                value => value.Equals(settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? DefaultTheme;
            settings.Appearance = AvailableAppearances.FirstOrDefault(
                value => value.Equals(settings.Appearance, StringComparison.OrdinalIgnoreCase)) ?? DefaultAppearance;
            settings.CustomColor = NormalizeColor(settings.CustomColor) ?? DefaultCustomColor;

            if (string.IsNullOrWhiteSpace(settings.DefaultGenerator)) settings.DefaultGenerator = DefaultGeneratorName;
            if (string.IsNullOrWhiteSpace(settings.DefaultCache)) settings.DefaultCache = DefaultCacheName;
            if (string.IsNullOrWhiteSpace(settings.StudioHost)) settings.StudioHost = DefaultHost;
            if (settings.StudioPort is <= 0 or > 65535) settings.StudioPort = DefaultPort;
            if (string.IsNullOrWhiteSpace(settings.LastChatModel)) settings.LastChatModel = null;
            if (string.IsNullOrWhiteSpace(settings.LastRemoteProvider)) settings.LastRemoteProvider = null;
            if (string.IsNullOrWhiteSpace(settings.PluginCompactor)) settings.PluginCompactor = null;
            return settings;
        }
        catch
        {
            return new StudioSettings();
        }
    }

    private static string HexToRgb(string color)
    {
        if (!TryParseRgb(color, out int r, out int g, out int b))
            return PresetRgb[DefaultTheme].Dark;
        return $"{r}, {g}, {b}";
    }

    private static bool TryParseRgb(string color, out int r, out int g, out int b)
    {
        r = g = b = 0;
        string? normalized = NormalizeColor(color);
        if (normalized is null) return false;

        string hex = normalized.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => $"{c}{c}"));

        return int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(hex[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private static (double H, double S, double L) ToHsl(string color)
    {
        if (!TryParseRgb(color, out int r, out int g, out int b)) return (0, 0, 0.5);
        return ToHsl(r, g, b);
    }

    private static (double H, double S, double L) RgbCsvToHsl(string rgb)
    {
        string[] parts = rgb.Split(',');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0].Trim(), out int r) ||
            !int.TryParse(parts[1].Trim(), out int g) ||
            !int.TryParse(parts[2].Trim(), out int b))
        {
            return (0, 0, 0.5);
        }

        return ToHsl(r, g, b);
    }

    private static (double H, double S, double L) ToHsl(int r, int g, int b)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn));
        double min = Math.Min(rn, Math.Min(gn, bn));
        double l = (max + min) / 2;
        double delta = max - min;
        double h = 0, s = 0;

        if (delta > 0)
        {
            s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
            if (max == rn) h = (gn - bn) / delta + (gn < bn ? 6 : 0);
            else if (max == gn) h = (bn - rn) / delta + 2;
            else h = (rn - gn) / delta + 4;
            h /= 6;
        }

        return (h, s, l);
    }

    private static string FromHsl(double h, double s, double l)
    {
        double r, g, b;
        if (s <= 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        return $"#{ToByte(r):x2}{ToByte(g):x2}{ToByte(b):x2}";
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static int ToByte(double value) =>
        Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
