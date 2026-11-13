namespace SharpMind.Studio.Core.OpenRouter;

/// <summary>
/// A remote model provider the app can talk to. The wire protocol is the
/// OpenAI-compatible chat-completions API, so a new provider is usually just a
/// preset and a sign-up link.
/// </summary>
public sealed record RemoteProviderPreset(
    string Id,
    string Name,
    string DefaultBaseUrl,
    string KeyHint,
    string? SignupUrl = null,
    bool Beta = false,
    bool AttributionHeaders = false,
    string? DefaultModelId = null)
{
    public bool HasSignup => !string.IsNullOrWhiteSpace(SignupUrl);
}

/// <summary>The remote providers Studio ships with.</summary>
public static class RemoteProviders
{
    public const string OpenRouter = "openrouter";
    public const string EuroRouter = "eurouter";
    public const string OpenRouterDefaultBaseUrl = "https://openrouter.ai/api/v1";
    public const string EuroRouterDefaultBaseUrl = "https://api.eurouter.ai";

    public static readonly RemoteProviderPreset OpenRouterPreset = new(
        OpenRouter,
        "OpenRouter",
        OpenRouterDefaultBaseUrl,
        "sk-or-...",
        SignupUrl: "https://openrouter.ai/",
        AttributionHeaders: true,
        DefaultModelId: "openrouter/free");

    public static readonly RemoteProviderPreset EuroRouterPreset = new(
        EuroRouter,
        "EuroRouter",
        EuroRouterDefaultBaseUrl,
        "eur_...",
        SignupUrl: "https://www.eurouter.ai?ref=ZLSG4A2M",
        Beta: true);

    public static IReadOnlyList<RemoteProviderPreset> All { get; } =
        [OpenRouterPreset, EuroRouterPreset];

    public static RemoteProviderPreset Default => OpenRouterPreset;

    public static RemoteProviderPreset Find(string? id) =>
        All.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Default;

    public static string NormalizeId(string? id) => Find(id).Id;
}
