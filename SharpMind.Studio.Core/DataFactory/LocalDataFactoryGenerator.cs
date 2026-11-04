using System.Text;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Core.DataFactory;

/// <summary>
/// Data Factory generator backed by a local packaged model. The model is loaded
/// once per run; each document resets the session history and cache, adds the
/// recipe's system prompt, and streams one answer.
/// </summary>
public sealed class LocalDataFactoryGenerator : IDataFactoryGenerator
{
    private readonly StudioModelHost _host;
    private readonly LoadedStudioModel _loaded;
    private readonly IChatSession _session;
    private readonly ResolvedInference _profile;

    private LocalDataFactoryGenerator(
        StudioModelHost host,
        LoadedStudioModel loaded,
        IChatSession session,
        ResolvedInference profile)
    {
        _host = host;
        _loaded = loaded;
        _session = session;
        _profile = profile;
    }

    public string Description => $"Local · {_loaded.Descriptor.FileName}";

    public static async Task<LocalDataFactoryGenerator> CreateAsync(
        StudioModelHost host,
        ModelDescriptor descriptor,
        ResolvedInference profile,
        BuilderCatalogResult builders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(builders);

        LoadedStudioModel loaded = await host
            .LoadAsync(descriptor, profile, progress: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            GeneratorOption generator = builders.Generators.FirstOrDefault(option =>
                option.Name.Equals(profile.Generator, StringComparison.OrdinalIgnoreCase))
                ?? builders.Generators.First(option =>
                    option.Name.Equals("StandardGeneratorBuilder", StringComparison.OrdinalIgnoreCase));
            CacheOption cache = builders.Caches.FirstOrDefault(option =>
                option.Name.Equals(profile.Cache, StringComparison.OrdinalIgnoreCase))
                ?? builders.Caches.First(option =>
                    option.Name.Equals("KVCacherBuilder", StringComparison.OrdinalIgnoreCase));

            Type cacheType = generator.CacheType ?? cache.Type;
            IChatSession session = ChatSessionFactory.CreateChatSession(
                generator.BuilderType,
                cacheType,
                loaded.Model,
                loaded.Tokenizer,
                loaded.Meta,
                agentBuilder: null,
                formatter: ChatFormatterFactory.Create(profile.Formatter, loaded.Meta),
                seed: profile.Seed);

            int maxContext = profile.MaxContextTokens is > 0
                ? profile.MaxContextTokens.Value
                : loaded.Config.MaxSeqLen;
            session.MaxTokens = Math.Clamp(
                maxContext, 64, Math.Max(64, loaded.Config.MaxSeqLen));
            session.MaxNewTokens = 512;
            session.Temperature = profile.Temperature;
            session.TopK = profile.TopK;
            session.TopP = profile.TopP;
            session.RepetitionPenalty = profile.RepetitionPenalty;
            session.RepetitionWindow = profile.RepetitionWindow;
            session.InitializeChat();

            return new LocalDataFactoryGenerator(host, loaded, session, profile);
        }
        catch
        {
            host.Release(loaded);
            throw;
        }
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        DataFactoryRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        _session.ClearHistory();
        _session.ResetCaches();
        _session.MaxNewTokens = Math.Max(1, recipe.MaxTokens);
        _session.Temperature = recipe.Temperature;
        _session.TopP = _profile.TopP;
        _session.TopK = _profile.TopK;
        _session.RepetitionPenalty = _profile.RepetitionPenalty;
        _session.RepetitionWindow = _profile.RepetitionWindow;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            _session.AddMessage(ChatRole.System, systemPrompt);

        var text = new StringBuilder();
        await foreach (ChatStreamEntry entry in _session
            .GetResponseStreamAsync(userPrompt, ct: cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(entry.Error))
                throw new InvalidOperationException(entry.Error);
            if (entry.Status == ChatStatus.Responding && entry.Token is not null)
                text.Append(entry.Token);
        }

        return text.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        _host.Release(_loaded);
    }
}
