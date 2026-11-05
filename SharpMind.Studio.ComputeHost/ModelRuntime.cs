using System.Runtime.CompilerServices;
using SharpMind.Core.Plugins;
using SharpMind.Inference;
using SharpMind.Inference.Chat;
using SharpMind.Model.Config;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.ComputeHost;

/// <summary>
/// Loads one packaged model and serves stateless chat completions from it.
/// Requests are serialized because an <see cref="IChatSession"/> owns a KV cache
/// and is not safe for concurrent generation.
/// </summary>
public sealed class ModelRuntime : IAsyncDisposable
{
    private readonly ComputeOptions _options;
    private readonly ComputeAccelerators _accelerators;
    private readonly StudioModelHost _host = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoadedStudioModel? _loaded;
    private IChatSession? _session;
    private string? _modelPath;
    private string? _acceleratorReason;

    public ModelRuntime(ComputeOptions options, ComputeAccelerators accelerators)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _accelerators = accelerators ?? throw new ArgumentNullException(nameof(accelerators));
        _modelPath = options.ModelPath;
    }

    /// <summary>Why the GPU inference path was not used, when a plugin declined.</summary>
    public string? AcceleratorReason => _acceleratorReason;

    public string ModelId =>
        string.IsNullOrWhiteSpace(_modelPath)
            ? "default"
            : Path.GetFileNameWithoutExtension(_modelPath);

    public bool IsLoaded => _session is not null;
    public string? ModelPath => _modelPath;

    /// <summary>
    /// Switches the active model and loads it eagerly so a bad file fails at
    /// activation time instead of on the next chat request.
    /// </summary>
    public async Task ActivateAsync(string? path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UnloadAsync().ConfigureAwait(false);
            _modelPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            _options.ModelPath = _modelPath;
            if (_modelPath is not null)
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadAsync()
    {
        if (_session is not null)
        {
            try { await _session.DisposeAsync().ConfigureAwait(false); } catch { }
            _session = null;
        }
        if (_loaded is not null)
        {
            try { _host.Release(_loaded); } catch { }
            _loaded = null;
        }
    }

    public async IAsyncEnumerable<string> GenerateAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IChatSession session = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            session.ClearHistory();
            session.ResetCaches();
            session.MaxNewTokens = Math.Max(1, request.TokenLimit ?? 512);
            session.Temperature = request.Temperature ?? 0.7f;
            session.TopP = request.TopP ?? 0.9f;

            ChatCompletionMessage? last = request.Messages.LastOrDefault();
            if (last is not null && last.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                foreach (ChatCompletionMessage message in request.Messages.Take(request.Messages.Count - 1))
                    AddMessage(session, message);

                await foreach (ChatStreamEntry entry in session
                    .GetResponseStreamAsync(last.Content, ct: cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Error))
                        throw new InvalidOperationException(entry.Error);
                    if (entry.Status == ChatStatus.Responding && entry.Token is not null)
                        yield return entry.Token;
                }
            }
            else
            {
                foreach (ChatCompletionMessage message in request.Messages)
                    AddMessage(session, message);

                await foreach (ChatStreamEntry entry in session
                    .ContinueResponseAsync(ct: cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Error))
                        throw new InvalidOperationException(entry.Error);
                    if (entry.Status == ChatStatus.Responding && entry.Token is not null)
                        yield return entry.Token;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChatSession> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_session is not null) return _session;

        if (string.IsNullOrWhiteSpace(_modelPath) || !File.Exists(_modelPath))
            throw new InvalidOperationException(
                "No model configured. Upload one to /v1/models/{name}, or put a .smm/.gguf in SHARPMIND_MODELS_DIR.");

        ModelDescriptor descriptor = ModelCatalog.Describe(_modelPath);
        ResolvedInference profile = InferenceResolver.Resolve(
            InferenceOverrides.SystemDefaults(), null, null, null);
        BuilderCatalogResult builders = BuilderCatalog.Discover(_options.PluginsFolder);

        GeneratorOption generator = builders.Generators.FirstOrDefault(option =>
            option.Name.Equals(profile.Generator, StringComparison.OrdinalIgnoreCase))
            ?? builders.Generators.First(option =>
                option.Name.Equals("StandardGeneratorBuilder", StringComparison.OrdinalIgnoreCase));
        CacheOption cache = builders.Caches.FirstOrDefault(option =>
            option.Name.Equals(profile.Cache, StringComparison.OrdinalIgnoreCase))
            ?? builders.Caches.First(option =>
                option.Name.Equals("KVCacherBuilder", StringComparison.OrdinalIgnoreCase));

        _loaded = await _host
            .LoadAsync(descriptor, profile, progress: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Type cacheType = generator.CacheType ?? cache.Type;
        IInferenceEngine? engine = CreateInferenceEngine(_loaded);
        IChatSession session = ChatSessionFactory.CreateChatSession(
            generator.BuilderType,
            cacheType,
            _loaded.Model,
            _loaded.Tokenizer,
            _loaded.Meta,
            agentBuilder: null,
            formatter: ChatFormatterFactory.Create(profile.Formatter, _loaded.Meta),
            seed: profile.Seed,
            engine: engine);

        int maxContext = profile.MaxContextTokens is > 0
            ? profile.MaxContextTokens.Value
            : _loaded.Config.MaxSeqLen;
        session.MaxTokens = Math.Clamp(maxContext, 64, Math.Max(64, _loaded.Config.MaxSeqLen));
        session.MaxNewTokens = 512;
        session.Temperature = profile.Temperature;
        session.TopK = profile.TopK;
        session.TopP = profile.TopP;
        session.RepetitionPenalty = profile.RepetitionPenalty;
        session.RepetitionWindow = profile.RepetitionWindow;
        session.InitializeChat();

        _session = session;
        return session;
    }

    /// <summary>
    /// Builds a device engine when an accelerator plugin offers inference for
    /// this model shape. A refusal is recorded rather than thrown so the host can
    /// keep serving on the CPU and say why in <c>/health</c>.
    /// </summary>
    private IInferenceEngine? CreateInferenceEngine(LoadedStudioModel loaded)
    {
        _acceleratorReason = null;
        if (string.Equals(_options.InferenceAccelerator, "CPU", StringComparison.OrdinalIgnoreCase))
            return null;

        int cacheLength = ModelConfig.ComputeMaxCacheLength(loaded.Config);
        var plugins = _accelerators.Plugins.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_options.InferenceAccelerator)
            && !string.Equals(_options.InferenceAccelerator, "auto", StringComparison.OrdinalIgnoreCase))
        {
            plugins = plugins.Where(plugin => plugin.Name.Equals(
                _options.InferenceAccelerator, StringComparison.OrdinalIgnoreCase));
        }

        foreach (IAcceleratorPlugin plugin in plugins)
        {
            foreach (object capability in plugin.Capabilities)
            {
                if (capability is not IInferenceEngineFactory factory) continue;
                try
                {
                    IInferenceEngine? engine = factory.TryCreate(
                        new InferenceEngineContext(loaded.Model, loaded.SharpConfig, cacheLength),
                        out string? reason);
                    if (engine is not null)
                    {
                        string? backend = plugin.Capabilities
                            .OfType<IBackendHintProvider>()
                            .Select(hint => hint.BackendHint())
                            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                        _acceleratorReason = backend is null
                            ? $"Inference on {plugin.Name}."
                            : $"Inference on {plugin.Name} ({backend}).";
                        return engine;
                    }

                    _acceleratorReason = $"{plugin.Name} declined: {reason ?? "not supported"}";
                }
                catch (Exception ex)
                {
                    _acceleratorReason = $"{plugin.Name} failed: {ex.GetBaseException().Message}";
                }
            }
        }

        return null;
    }

    private static void AddMessage(IChatSession session, ChatCompletionMessage message)
    {
        ChatRole role = message.Role.ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Agent,
            "tool" or "function" => ChatRole.Tool,
            _ => ChatRole.User,
        };
        session.AddMessage(role, message.Content);
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
