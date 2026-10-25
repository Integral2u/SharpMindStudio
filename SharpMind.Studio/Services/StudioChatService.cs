using System.Diagnostics;
using System.Text;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Services;

public sealed class StudioChatService : IAsyncDisposable
{
    private readonly StudioWorkspaceService _workspace;
    private readonly ModelCatalogService _catalog;
    private readonly InferenceSettingsService _inference;
    private readonly StudioModelHost _host;
    private readonly ChatSessionStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatSession? _session;
    private CancellationTokenSource? _streamCts;
    private ChatSessionDocument? _document;
    private ResolvedInference? _resolved;
    private ModelDescriptor? _model;
    private string _status = "Choose a model to start chatting.";
    private string? _error;
    private string? _cacheWarning;
    private bool _streaming;

    public StudioChatService(
        StudioWorkspaceService workspace,
        ModelCatalogService catalog,
        InferenceSettingsService inference,
        StudioModelHost host)
    {
        _workspace = workspace;
        _catalog = catalog;
        _inference = inference;
        _host = host;
        _store = new ChatSessionStore(workspace.Project.ChatsDirectory);
    }

    public event Action? Changed;

    public ChatSessionDocument? Document => _document;
    public ModelDescriptor? Model => _model;
    public ResolvedInference? Resolved => _resolved;
    public bool IsStreaming => _streaming;
    public bool IsOpen => _document is not null;
    public string Status => _status;
    public string? Error => _error;
    public string? CacheWarning => _cacheWarning;
    public IReadOnlyList<ChatSessionSummary> Sessions => _store.List();
    public IReadOnlyList<ChatMessage> DisplayMessages => [.. _document?.Messages
        .Where(message => message.Role != ChatRole.System) ?? []];

    public async Task NewChatAsync(ModelDescriptor model, string? systemPrompt, InferenceOverrides? overrides = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            _document = _store.Create(
                model.Path, model.Family.Id, model.Family.DisplayName,
                typeof(IChatSession).Assembly.GetName().Version?.ToString(), systemPrompt);
            _document.Overrides = overrides ?? new InferenceOverrides();
            _document.Messages = [];
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                _document.Messages.Add(ChatMessage.System(systemPrompt.Trim()));
            _model = model;
            _resolved = _inference.Resolve(model.Family.Id, model.Path, _document.Overrides);
            _error = null;
            Notify();
            await OpenCoreAsync(snapshot: null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OpenAsync(string id)
    {
        if (_document?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true && _session is not null)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            ChatSessionDocument document = _store.Open(id);
            _document = document;
            _model = _catalog.Find(document.ModelPath)
                ?? throw new FileNotFoundException("The model for this chat was not found.", document.ModelPath);
            _resolved = _inference.Resolve(document.FamilyId, document.ModelPath, document.Overrides);
            _error = null;
            Notify();
            await OpenCoreAsync(_store.BuildSnapshot(document)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not open chat.";
            EndSession();
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _session is null || _document is null || _streaming) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _streaming = true;
            _error = null;
            _status = "Thinking...";
            _streamCts = new CancellationTokenSource();
            _document.Messages.Add(ChatMessage.User(text.Trim(), "You"));
            ChatMessage assistant = ChatMessage.Agent("");
            _document.Messages.Add(assistant);
            Notify();

            var stopwatch = Stopwatch.StartNew();
            var lastNotify = TimeSpan.Zero;
            try
            {
                await foreach (ChatStreamEntry entry in _session
                    .GetResponseStreamAsync(text.Trim(), ct: _streamCts.Token)
                    .WithCancellation(_streamCts.Token).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(entry.Error))
                        _error = entry.Error;

                    if (entry.Status == ChatStatus.Responding && entry.Token is not null)
                    {
                        assistant.Content += entry.Token;
                        _status = entry.TokensPerSecond is { } tps
                            ? $"{tps:F1} tok/s"
                            : "Writing...";
                    }
                    else if (entry.Status == ChatStatus.Thinking)
                    {
                        _status = "Thinking...";
                    }
                    else if (entry.Status == ChatStatus.Executing)
                    {
                        _status = $"Running {entry.Token ?? "tool"}...";
                    }

                    if (entry.IsComplete || stopwatch.Elapsed - lastNotify > TimeSpan.FromMilliseconds(55))
                    {
                        lastNotify = stopwatch.Elapsed;
                        Notify();
                    }
                }

                _document.Messages = [.. _session.History];
                _status = "Ready.";
            }
            catch (OperationCanceledException)
            {
                _document.Messages = [.. _session.History];
                _status = "Stopped.";
            }
            catch (Exception ex)
            {
                _document.Messages = [.. _session.History];
                _error = $"{ex.GetType().Name}: {ex.Message}";
                _status = "Generation failed.";
            }
            finally
            {
                _streamCts?.Dispose();
                _streamCts = null;
                _streaming = false;
                await SaveSnapshotAsync().ConfigureAwait(false);
                Notify();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _streamCts?.Cancel();
        _session?.Interrupt();
    }

    public async Task ApplyOptionsAsync(InferenceOverrides overrides)
    {
        if (_document is null || _session is null || _streaming) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _document.Overrides = overrides;
            ResolvedInference next = _inference.Resolve(_document.FamilyId, _document.ModelPath, overrides);
            ResolvedInference previous = _resolved ?? next;
            _resolved = next;
            _store.Save(_document);

            if (LoadAffectingChanged(previous, next))
            {
                ChatSessionSnapshot snapshot = _session.GetSnapshot();
                EndSession();
                await RebuildAsync(snapshot).ConfigureAwait(false);
                await SaveSnapshotAsync().ConfigureAwait(false);
            }
            else
            {
                ApplyLive(_session, next);
            }

            _error = null;
            _status = "Options applied.";
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not apply options.";
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task<bool> UpdateSystemPromptAsync(string? prompt)
    {
        if (_document is null || _session is null || _streaming) return false;
        if (_document.Messages.Any(message => message.Role != ChatRole.System))
            return false;

        _document.SystemPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        _document.Messages.RemoveAll(message => message.Role == ChatRole.System);
        if (_document.SystemPrompt is not null)
            _document.Messages.Add(ChatMessage.System(_document.SystemPrompt));
        _store.Save(_document);
        EndSession();
        _resolved = _inference.Resolve(_document.FamilyId, _document.ModelPath, _document.Overrides);
        await RebuildAsync(snapshot: null).ConfigureAwait(false);
        await SaveSnapshotAsync().ConfigureAwait(false);
        Notify();
        return true;
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_document?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
            {
                EndSession();
                _document = null;
                _resolved = null;
                _model = null;
                _status = "Choose a model to start chatting.";
            }

            _store.Delete(id);
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            _document = null;
            _resolved = null;
            _model = null;
            _error = null;
            _status = "Choose a model to start chatting.";
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        EndSession();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task OpenCoreAsync(ChatSessionSnapshot? snapshot)
    {
        if (_document is null || _model is null || _resolved is null) return;

        _status = $"Loading {_model.FileName}...";
        Notify();
        try
        {
            LoadedStudioModel loaded = await _host.LoadAsync(
                _model, _resolved, new Progress<string>(message =>
                {
                    _status = message;
                    Notify();
                })).ConfigureAwait(false);

            _session = await BuildSessionAsync(loaded, _resolved, snapshot).ConfigureAwait(false);
            _status = "Ready.";
            _error = null;
            await SaveSnapshotAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not load the model.";
            _session = null;
        }
    }

    private async Task RebuildAsync(ChatSessionSnapshot? snapshot)
    {
        if (_document is null || _model is null || _resolved is null) return;
        LoadedStudioModel loaded = await _host.LoadAsync(_model, _resolved).ConfigureAwait(false);
        _session = await BuildSessionAsync(loaded, _resolved, snapshot).ConfigureAwait(false);
    }

    private async Task<IChatSession> BuildSessionAsync(
        LoadedStudioModel loaded,
        ResolvedInference profile,
        ChatSessionSnapshot? snapshot)
    {
        IChatSession session = await Task.Run(
            () => CreateSession(loaded, profile)).ConfigureAwait(false);
        try
        {
            Configure(session, loaded, profile);
            await Task.Run(() => session.InitializeChat()).ConfigureAwait(false);

            if (snapshot is not null && snapshot.History.Count > 0)
            {
                session.LoadSnapshot(snapshot);
                try
                {
                    await Task.Run(session.WarmupPrefillAsync).ConfigureAwait(false);
                }
                catch when (snapshot.KVCache is not null)
                {
                    DisposeSession(session);
                    session = await Task.Run(() => CreateSession(loaded, profile)).ConfigureAwait(false);
                    Configure(session, loaded, profile);
                    await Task.Run(() => session.InitializeChat()).ConfigureAwait(false);
                    session.LoadSnapshot(new ChatSessionSnapshot { History = [.. snapshot.History] });
                    await Task.Run(session.WarmupPrefillAsync).ConfigureAwait(false);
                }
            }

            if (snapshot is null && !string.IsNullOrWhiteSpace(_document?.SystemPrompt))
            {
                session.AddMessage(ChatRole.System, _document!.SystemPrompt!);
            }

            return session;
        }
        catch
        {
            DisposeSession(session);
            throw;
        }
    }

    private IChatSession CreateSession(LoadedStudioModel loaded, ResolvedInference profile)
    {
        var builders = _workspace.Builders;
        GeneratorOption generator = builders.Generators.FirstOrDefault(option =>
            option.Name.Equals(profile.Generator, StringComparison.OrdinalIgnoreCase))
            ?? builders.Generators.First(option =>
                option.Name.Equals("StandardGeneratorBuilder", StringComparison.OrdinalIgnoreCase));
        CacheOption cache = builders.Caches.FirstOrDefault(option =>
            option.Name.Equals(profile.Cache, StringComparison.OrdinalIgnoreCase))
            ?? builders.Caches.First(option =>
                option.Name.Equals("KVCacherBuilder", StringComparison.OrdinalIgnoreCase));

        Type cacheType = generator.CacheType ?? cache.Type;
        return ChatSessionFactory.CreateChatSession(
            generator.BuilderType,
            cacheType,
            loaded.Model,
            loaded.Tokenizer,
            loaded.Meta,
            formatter: ChatFormatterFactory.Create(profile.Formatter, loaded.Meta),
            seed: profile.Seed);
    }

    private static void Configure(IChatSession session, LoadedStudioModel loaded, ResolvedInference profile)
    {
        int maxContext = profile.MaxContextTokens is > 0
            ? profile.MaxContextTokens.Value
            : loaded.Config.MaxSeqLen;
        session.MaxTokens = Math.Clamp(maxContext, 64, Math.Max(64, loaded.Config.MaxSeqLen));
        ApplyLive(session, profile);
    }

    private static void ApplyLive(IChatSession session, ResolvedInference profile)
    {
        session.MaxNewTokens = Math.Max(1, profile.MaxNewTokens);
        session.Temperature = profile.Temperature;
        session.TopK = profile.TopK;
        session.TopP = profile.TopP;
        session.RepetitionPenalty = profile.RepetitionPenalty;
        session.RepetitionWindow = profile.RepetitionWindow;
        session.ShowThinking = profile.ShowThinking;
        session.EnableThinking = profile.EnableThinking;
    }

    private static bool LoadAffectingChanged(ResolvedInference a, ResolvedInference b) =>
        !a.Generator.Equals(b.Generator, StringComparison.OrdinalIgnoreCase) ||
        !a.Cache.Equals(b.Cache, StringComparison.OrdinalIgnoreCase) ||
        !a.Formatter.Equals(b.Formatter, StringComparison.OrdinalIgnoreCase) ||
        !a.LoadMode.Equals(b.LoadMode, StringComparison.OrdinalIgnoreCase) ||
        !a.HardwareTier.Equals(b.HardwareTier, StringComparison.OrdinalIgnoreCase) ||
        a.MaxParallelLoadDegree != b.MaxParallelLoadDegree ||
        a.UseParallelKernels != b.UseParallelKernels ||
        a.MaxContextTokens != b.MaxContextTokens ||
        a.Seed != b.Seed;

    private async Task SaveSnapshotAsync()
    {
        if (_document is null || _session is null) return;
        try
        {
            ChatSessionSnapshot snapshot = _session.GetSnapshot();
            _document.Messages = [.. _session.History];
            ChatSessionDocument document = _document;
            await Task.Run(() => _store.SaveSnapshot(document, snapshot)).ConfigureAwait(false);
            long cacheSize = _store.SnapshotSize(document.Id);
            _cacheWarning = cacheSize > 50L * 1024 * 1024
                ? $"KV cache snapshot is {cacheSize / (1024.0 * 1024.0):F0} MB. It resumes faster but uses disk."
                : null;
        }
        catch (Exception ex)
        {
            _error = $"Session save failed: {ex.Message}";
        }
    }

    private void EndSession()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _streaming = false;
        DisposeSession(_session);
        _session = null;
    }

    private static void DisposeSession(IChatSession? session)
    {
        if (session is null) return;
        try { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { }
    }
    private void Notify() => Changed?.Invoke();
}






