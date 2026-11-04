using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.OpenRouter;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Services;

public sealed class StudioChatService : IAsyncDisposable
{
    private readonly StudioWorkspaceService _workspace;
    private readonly ModelCatalogService _catalog;
    private readonly InferenceSettingsService _inference;
    private readonly OpenRouterSettingsService _openRouter;
    private readonly OpenRouterClient _openRouterClient;
    private readonly StudioModelHost _host;
    private readonly StudioWorkService _work;
    private readonly StudioDeviceAccess _device;
    private ChatSessionStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatSession? _session;
    private LoadedStudioModel? _loaded;
    private CancellationTokenSource? _streamCts;
    private ChatSessionDocument? _document;
    private ResolvedInference? _resolved;
    private ModelDescriptor? _model;
    private string _status = "Choose a model to start chatting.";
    private string? _error;
    private string? _cacheWarning;
    private bool _streaming;
    private bool _loading;
    private double? _loadProgress;
    private string? _loadDetail;
    private CancellationTokenSource? _loadCts;

    public StudioChatService(
        StudioWorkspaceService workspace,
        ModelCatalogService catalog,
        InferenceSettingsService inference,
        OpenRouterSettingsService openRouter,
        OpenRouterClient openRouterClient,
        StudioModelHost host,
        StudioWorkService work,
        StudioDeviceAccess device)
    {
        _workspace = workspace;
        _catalog = catalog;
        _inference = inference;
        _openRouter = openRouter;
        _openRouterClient = openRouterClient;
        _host = host;
        _work = work;
        _device = device;
        _store = new ChatSessionStore(workspace.Project.ChatsDirectory);
    }

    public event Action? Changed;

    public ChatSessionDocument? Document => _document;
    public ModelDescriptor? Model => _model;
    public ResolvedInference? Resolved => _resolved;
    public bool IsStreaming => _streaming;
    public bool IsLoading => _loading;
    public double? LoadProgress => _loadProgress;
    public string? LoadDetail => _loadDetail;
    public bool IsOpen => _document is not null;
    public bool IsRemote => string.Equals(_document?.Provider, "openrouter", StringComparison.OrdinalIgnoreCase);
    public string? RemoteModel => _document?.RemoteModel;
    public bool OpenRouterConfigured => _openRouter.IsConfigured;
    public string Status => _status;
    public string? Error => _error;
    public string? CacheWarning => _cacheWarning;
    public double? LastTokensPerSecond => _document?.LastTokensPerSecond;
    public string? PendingPrompt { get; set; }
    public IReadOnlyList<ChatSessionSummary> Sessions => _store.List();

    public string? TakePendingPrompt()
    {
        string? prompt = PendingPrompt;
        PendingPrompt = null;
        return prompt;
    }
    public IReadOnlyList<ChatMessage> DisplayMessages =>
        ChatTranscriptFilter.Visible(_document?.Messages ?? [], _streaming);

    public async Task NewChatAsync(ModelDescriptor model, string? systemPrompt, InferenceOverrides? overrides = null, string mode = "chat", string? tunePreset = null, string? tuneTarget = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            _document = _store.Create(
                model.Path, model.Family.Id, model.Family.DisplayName,
                typeof(IChatSession).Assembly.GetName().Version?.ToString(), systemPrompt, mode, tunePreset, tuneTarget);
            _document.Overrides = overrides ?? new InferenceOverrides();
            _document.Messages = [];
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                _document.Messages.Add(ChatMessage.System(systemPrompt.Trim()));
            _model = model;
            _resolved = ResolveInference(model.Family.Id, model.Path, _document.Overrides);
            _error = null;
            _status = "Ready.";
            _cacheWarning = null;
            Notify();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a chat backed by OpenRouter. No local weights are loaded; the
    /// document keeps the same transcript/history shape as a local chat.
    /// </summary>
    public async Task NewRemoteChatAsync(
        string modelId,
        string? systemPrompt,
        string mode = "chat",
        InferenceOverrides? overrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            _document = _store.CreateRemote(modelId.Trim(), systemPrompt, mode);
            _document.Overrides = overrides ?? new InferenceOverrides();
            _model = null;
            _resolved = null;
            _error = null;
            _status = "Ready (OpenRouter).";
            _cacheWarning = null;
            _openRouter.RememberModel(modelId);
            Notify();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OpenAsync(string id)
    {
        if (_document?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            ChatSessionDocument document = _store.Open(id);
            _document = document;
            if (string.Equals(document.Provider, "openrouter", StringComparison.OrdinalIgnoreCase))
            {
                _model = null;
                _resolved = null;
                _error = null;
                _status = "Ready (OpenRouter).";
                _cacheWarning = null;
                Notify();
                return;
            }

            _model = _catalog.Find(document.ModelPath)
                ?? throw new FileNotFoundException("The model for this chat was not found.", document.ModelPath);
            _resolved = ResolveInference(document.FamilyId, document.ModelPath, document.Overrides);
            _error = null;
            _status = "Ready.";
            _cacheWarning = null;
            Notify();
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
        if (string.IsNullOrWhiteSpace(text) || _document is null || _streaming) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRemote)
            {
                await SendRemoteCoreAsync(text.Trim()).ConfigureAwait(false);
                return;
            }

            if (_session is null)
            {
                ChatSessionSnapshot? snapshot = string.Equals(
                    _document.Mode, "work", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _store.BuildSnapshot(_document);
                await OpenCoreAsync(snapshot).ConfigureAwait(false);
                if (_session is null) return;
            }

            _streaming = true;
            _error = null;
            _status = "Thinking...";
            _streamCts = new CancellationTokenSource();
            _document.Messages.Add(ChatMessage.User(text.Trim(), "You"));
            ChatMessage assistant = ChatMessage.Agent("");
            _document.Messages.Add(assistant);
            Notify();
            await Task.Yield();

            var stopwatch = Stopwatch.StartNew();
            var lastNotify = TimeSpan.Zero;
            double? lastTps = null;
            try
            {
                await foreach (ChatStreamEntry entry in _session
                    .GetResponseStreamAsync(text.Trim(), ct: _streamCts.Token)
                    .WithCancellation(_streamCts.Token).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(entry.Error))
                        _error = entry.Error;
                    if (entry.TokensPerSecond is { } entryTps) lastTps = entryTps;

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

                    if (entry.IsComplete || lastNotify == TimeSpan.Zero ||
                        stopwatch.Elapsed - lastNotify > TimeSpan.FromMilliseconds(40))
                    {
                        lastNotify = stopwatch.Elapsed;
                        Notify();
                        await Task.Yield();
                    }
                }

                _document.Messages = [.. _session.History];
                _document.LastTokensPerSecond = lastTps ?? _session.TokensPerSecond;
                _status = "Ready.";
            }
            catch (OperationCanceledException)
            {
                _document.Messages = [.. _session.History];
                _document.LastTokensPerSecond = lastTps ?? _session.TokensPerSecond;
                _status = "Stopped.";
            }
            catch (Exception ex)
            {
                _document.Messages = [.. _session.History];
                _document.LastTokensPerSecond = lastTps ?? _session.TokensPerSecond;
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
        _work.CancelPermission();
        _work.CancelChoice();
        _session?.Interrupt();
    }

    public void Interrupt()
    {
        if (_loading)
            _loadCts?.Cancel();
        else
            Stop();
    }

    /// <summary>
    /// Runs the OpenRouter turn: stream assistant text, execute any tool calls
    /// against the Work agent, feed the results back, and repeat until the model
    /// answers or the per-turn tool budget is spent. The caller holds _gate.
    /// </summary>
    private async Task SendRemoteCoreAsync(string text)
    {
        if (_document is null) return;

        OpenRouterConnection connection = _openRouter.Connection;
        if (!connection.IsUsable)
        {
            _error = "OpenRouter is not configured. Add an API key in Settings.";
            _status = "OpenRouter is not configured.";
            Notify();
            return;
        }

        string model = string.IsNullOrWhiteSpace(_document.RemoteModel)
            ? _openRouter.DefaultModel ?? ""
            : _document.RemoteModel!;
        if (string.IsNullOrWhiteSpace(model))
        {
            _error = "Choose an OpenRouter model id first.";
            _status = "No OpenRouter model selected.";
            Notify();
            return;
        }

        IAgentBuilder? agent = string.Equals(_document.Mode, "work", StringComparison.OrdinalIgnoreCase)
            ? _work.CreateAgent("work")
            : null;
        if (agent is not null)
        {
            // OpenAI tool calling is native here; do not also teach a JSON call shape.
            agent.CallFormat = ToolCallFormat.None;
            agent.ToolListInPrompt = false;
        }

        IReadOnlyList<JsonObject>? tools = agent is null
            ? null
            : OpenRouterMessageBuilder.BuildTools(agent);

        _streaming = true;
        _error = null;
        _status = "Thinking...";
        _streamCts = new CancellationTokenSource();

        _document.Messages.Add(ChatMessage.User(text, "You"));
        ChatMessage assistant = ChatMessage.Agent("");
        _document.Messages.Add(assistant);
        Notify();
        await Task.Yield();

        List<JsonObject> messages = OpenRouterMessageBuilder.BuildMessages(
            [.. _document.Messages.Take(_document.Messages.Count - 1)],
            _document.SystemPrompt,
            agent);

        int maxToolCalls = 10;
        int toolCallsUsed = 0;
        var stopwatch = Stopwatch.StartNew();
        int? promptTokens = null;
        int? completionTokens = null;
        decimal? cost = null;
        double? lastTps = null;

        try
        {
            while (true)
            {
                var accumulators = new Dictionary<int, ToolCallAccumulator>();
                var lastNotify = TimeSpan.Zero;

                await foreach (OpenRouterStreamEvent entry in _openRouterClient.StreamAsync(
                    new OpenRouterRequest
                    {
                        Model = model,
                        Messages = messages,
                        Tools = tools,
                        Temperature = _document.Overrides.Temperature,
                        TopP = _document.Overrides.TopP,
                        TopK = _document.Overrides.TopK,
                        RepetitionPenalty = _document.Overrides.RepetitionPenalty,
                        MaxTokens = _document.Overrides.MaxNewTokens,
                        Seed = _document.Overrides.Seed,
                    },
                    connection,
                    _streamCts.Token).WithCancellation(_streamCts.Token).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Error))
                        throw new OpenRouterException(entry.Error);

                    if (entry.PromptTokens is int prompt) promptTokens = prompt;
                    if (entry.CompletionTokens is int completion) completionTokens = completion;
                    if (entry.Cost is decimal entryCost) cost = entryCost;

                    if (entry.ToolCalls is { Count: > 0 })
                    {
                        foreach (OpenRouterToolCallDelta delta in entry.ToolCalls)
                            AccumulateToolCall(accumulators, delta);
                    }

                    if (!string.IsNullOrEmpty(entry.Delta))
                    {
                        assistant.Content += entry.Delta;
                        _status = "Writing...";
                        if (lastNotify == TimeSpan.Zero ||
                            stopwatch.Elapsed - lastNotify > TimeSpan.FromMilliseconds(40))
                        {
                            lastNotify = stopwatch.Elapsed;
                            Notify();
                        }
                    }
                }

                if (accumulators.Count == 0) break;

                if (toolCallsUsed >= maxToolCalls)
                {
                    assistant.Content += "\n\n[Tool-call limit reached for this turn.]";
                    break;
                }

                List<OpenRouterCompletedToolCall> completed = [.. accumulators.Values
                    .OrderBy(call => call.Index)
                    .Select(call => call.ToCall())];
                JsonArray toolCallsJson = OpenRouterMessageBuilder.ToToolCallsJson(completed);

                AttachToolCalls(_document.Messages, assistant, toolCallsJson);
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrWhiteSpace(assistant.Content) ? null : assistant.Content,
                    ["tool_calls"] = toolCallsJson.DeepClone(),
                });

                foreach (OpenRouterCompletedToolCall call in completed)
                {
                    toolCallsUsed++;
                    _status = $"Running {call.Name}...";
                    Notify();

                    JsonObject arguments;
                    try
                    {
                        arguments = JsonNode.Parse(call.Arguments) as JsonObject ?? new JsonObject();
                    }
                    catch
                    {
                        arguments = new JsonObject();
                    }

                    JsonObject result = agent is null
                        ? new JsonObject
                        {
                            ["status"] = "error",
                            ["message"] = "Tools are only available in Work mode.",
                        }
                        : await Task.Run(
                            () => agent.CallToolAsync(
                                new JsonObject
                                {
                                    ["tool"] = call.Name,
                                    ["arguments"] = arguments,
                                },
                                _streamCts.Token),
                            _streamCts.Token).ConfigureAwait(false);

                    string resultJson = result.ToJsonString();
                    messages.Add(OpenRouterMessageBuilder.ToolResult(call.Id, resultJson));
                    _document.Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        Content = resultJson,
                        Metadata = new Dictionary<string, string>
                        {
                            [OpenRouterMetadata.ToolCallId] = call.Id,
                        },
                    });
                }

                assistant = ChatMessage.Agent("");
                _document.Messages.Add(assistant);
                Notify();
            }

            if (string.IsNullOrWhiteSpace(assistant.Content) && assistant.Metadata is null)
                _document.Messages.Remove(assistant);

            if (completionTokens is int generated && stopwatch.Elapsed.TotalSeconds > 0.1)
            {
                lastTps = generated / stopwatch.Elapsed.TotalSeconds;
            }
            else if (assistant.Content.Length > 0 && stopwatch.Elapsed.TotalSeconds > 0.1)
            {
                lastTps = (assistant.Content.Length / 4.0) / stopwatch.Elapsed.TotalSeconds;
            }

            _document.LastTokensPerSecond = lastTps;
            _status = cost is { } spent
                ? $"Ready. OpenRouter cost {spent:0.000000}."
                : promptTokens is int promptUsed && completionTokens is int completionUsed
                    ? $"Ready. {promptUsed:N0} prompt / {completionUsed:N0} completion tokens."
                    : "Ready.";
        }
        catch (OperationCanceledException)
        {
            RemoveEmptyAssistant(assistant);
            _status = "Stopped.";
        }
        catch (OpenRouterException ex)
        {
            RemoveEmptyAssistant(assistant);
            _error = ex.Message;
            _status = "OpenRouter request failed.";
        }
        catch (Exception ex)
        {
            RemoveEmptyAssistant(assistant);
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Generation failed.";
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            _streaming = false;
            _store.Save(_document);
            Notify();
        }
    }

    private static void AccumulateToolCall(
        Dictionary<int, ToolCallAccumulator> accumulators,
        OpenRouterToolCallDelta delta)
    {
        if (!accumulators.TryGetValue(delta.Index, out ToolCallAccumulator? call))
        {
            call = new ToolCallAccumulator { Index = delta.Index };
            accumulators[delta.Index] = call;
        }

        if (!string.IsNullOrWhiteSpace(delta.Id)) call.Id = delta.Id;
        if (!string.IsNullOrWhiteSpace(delta.Name)) call.Name = delta.Name;
        if (!string.IsNullOrEmpty(delta.Arguments)) call.Arguments.Append(delta.Arguments);
    }

    /// <summary>
    /// ChatMessage.Metadata is init-only, so attach the tool-call payload by
    /// replacing the row in place. The transcript filter hides the empty
    /// assistant row; the message builder reads the metadata on reload.
    /// </summary>
    private void RemoveEmptyAssistant(ChatMessage assistant)
    {
        if (_document is null) return;
        if (string.IsNullOrWhiteSpace(assistant.Content) && assistant.Metadata is null)
            _document.Messages.Remove(assistant);
    }

    private static void AttachToolCalls(
        List<ChatMessage> messages,
        ChatMessage assistant,
        JsonArray toolCalls)
    {
        int index = messages.IndexOf(assistant);
        if (index < 0) return;

        var metadata = assistant.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(assistant.Metadata);
        metadata[OpenRouterMetadata.ToolCalls] = toolCalls.ToJsonString();

        messages[index] = new ChatMessage
        {
            Role = assistant.Role,
            Content = assistant.Content,
            Name = assistant.Name,
            Artifacts = assistant.Artifacts,
            Timestamp = assistant.Timestamp,
            Metadata = metadata,
            IsPinned = assistant.IsPinned,
            Ignore = assistant.Ignore,
        };
    }

    private sealed class ToolCallAccumulator
    {
        public required int Index { get; init; }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();

        public OpenRouterCompletedToolCall ToCall() => new(
            Index,
            string.IsNullOrWhiteSpace(Id) ? $"call_{Guid.NewGuid():n}" : Id,
            Name,
            Arguments.ToString());
    }

    public async Task ApplyOptionsAsync(InferenceOverrides overrides)
    {
        if (_device.IsMobile || _document is null || _streaming) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _document.Overrides = overrides;
            if (IsRemote)
            {
                _store.Save(_document);
                _error = null;
                _status = "Options applied.";
                return;
            }

            ResolvedInference next = ResolveInference(_document.FamilyId, _document.ModelPath, overrides);
            ResolvedInference previous = _resolved ?? next;
            _resolved = next;
            _store.Save(_document);

            if (_session is null)
            {
                _error = null;
                _status = "Options applied.";
                return;
            }

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
        if (_device.IsMobile || _document is null || _streaming) return false;
        if (!IsRemote && _session is null) return false;
        if (_document.Messages.Any(message => message.Role != ChatRole.System))
            return false;

        _document.SystemPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        _document.Messages.RemoveAll(message => message.Role == ChatRole.System);
        if (_document.SystemPrompt is not null)
            _document.Messages.Add(ChatMessage.System(_document.SystemPrompt));
        _store.Save(_document);

        if (IsRemote)
        {
            _error = null;
            _status = "System prompt applied.";
            Notify();
            return true;
        }

        EndSession();
        _resolved = ResolveInference(_document.FamilyId, _document.ModelPath, _document.Overrides);
        await RebuildAsync(snapshot: null).ConfigureAwait(false);
        await SaveSnapshotAsync().ConfigureAwait(false);
        Notify();
        return true;
    }

    public async Task DeleteAsync(string id)
    {
        if (_device.IsMobile) return;
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

    public async Task RenameAsync(string id, string name)
    {
        if (_device.IsMobile) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _store.Rename(id, name);
            if (_document?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
                _document.Name = name.Trim();
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task ClearAllAsync()
    {
        if (_device.IsMobile) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            _document = null;
            _resolved = null;
            _model = null;
            _store.Clear();
            _error = null;
            _status = "All chats cleared.";
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

    public async Task SuspendAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null)
                await SaveSnapshotAsync().ConfigureAwait(false);
            EndSession();
            _document = null;
            _resolved = null;
            _model = null;
            _error = null;
            _status = "Ready.";
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }
    public async Task UseProjectAsync()
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
            _store = new ChatSessionStore(_workspace.Project.ChatsDirectory);
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task ReloadWorkContextAsync()
    {
        if (IsRemote) return;
        if (_session is null || _document is null ||
            !string.Equals(_document.Mode, "work", StringComparison.OrdinalIgnoreCase))
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<ChatMessage> history = [.. _session.History
                .Where(message => message.Role != ChatRole.System)];
            EndSession();
            _resolved = ResolveInference(_document.FamilyId, _document.ModelPath, _document.Overrides);
            await RebuildWorkSessionAsync(history).ConfigureAwait(false);
            await SaveSnapshotAsync().ConfigureAwait(false);
            _status = "Project settings applied.";
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not apply project settings.";
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
        if (IsRemote)
        {
            _session = null;
            _loaded = null;
            _error = null;
            _status = "Ready (OpenRouter).";
            _loadDetail = "Ready";
            _loadProgress = 1;
            Notify();
            return;
        }

        if (_document is null || _model is null || _resolved is null) return;

        _loading = true;
        _loadProgress = 0;
        _loadDetail = "Reading model";
        _loadCts = new CancellationTokenSource();
        _status = $"Loading {_model.FileName}...";
        Notify();
        try
        {
            var progress = new Progress<ModelLoadProgress>(report =>
            {
                _status = report.Message;
                _loadDetail = report.Message;
                _loadProgress = report.Fraction;
                Notify();
            });

            LoadedStudioModel loaded = await _host.LoadAsync(
                _model, _resolved, progress, _loadCts.Token).ConfigureAwait(false);
            _loaded = loaded;

            _loadDetail = "Initializing chat";
            _loadProgress = 0.95;
            _status = "Initializing chat...";
            Notify();
            _session = string.Equals(_document.Mode, "work", StringComparison.OrdinalIgnoreCase)
                ? await BuildWorkSessionAsync(loaded, _resolved,
                    [.. _document.Messages.Where(message => message.Role != ChatRole.System)]).ConfigureAwait(false)
                : await BuildSessionAsync(loaded, _resolved, snapshot).ConfigureAwait(false);
            _status = "Ready.";
            _loadDetail = "Ready";
            _loadProgress = 1;
            _error = null;
            await SaveSnapshotAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _error = null;
            _status = "Load interrupted.";
            EndSession();
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not load the model.";
            EndSession();
        }
        finally
        {
            _loading = false;
            _loadCts?.Dispose();
            _loadCts = null;
            _loadProgress = null;
            Notify();
        }
    }
    private async Task RebuildWorkSessionAsync(IReadOnlyList<ChatMessage> history)
    {
        if (_document is null || _model is null || _resolved is null) return;
        LoadedStudioModel loaded = await _host.LoadAsync(_model, _resolved).ConfigureAwait(false);
        _loaded = loaded;
        try
        {
            _session = await BuildWorkSessionAsync(loaded, _resolved, history).ConfigureAwait(false);
        }
        catch
        {
            _host.Release(loaded);
            _loaded = null;
            throw;
        }
    }

    private async Task<IChatSession> BuildWorkSessionAsync(
        LoadedStudioModel loaded,
        ResolvedInference profile,
        IReadOnlyList<ChatMessage> history)
    {
        IChatSession session = await Task.Run(
            () => CreateSession(loaded, profile)).ConfigureAwait(false);
        try
        {
            Configure(session, loaded, profile);
            await Task.Run(() => session.InitializeChat()).ConfigureAwait(false);
            foreach (ChatMessage message in history)
                session.AddMessage(message);
            await Task.Run(session.WarmupPrefillAsync).ConfigureAwait(false);
            return session;
        }
        catch
        {
            DisposeSession(session);
            throw;
        }
    }
    private async Task RebuildAsync(ChatSessionSnapshot? snapshot)
    {
        if (_document is null || _model is null || _resolved is null) return;
        LoadedStudioModel loaded = await _host.LoadAsync(_model, _resolved).ConfigureAwait(false);
        _loaded = loaded;
        try
        {
            _session = string.Equals(_document.Mode, "work", StringComparison.OrdinalIgnoreCase)
                ? await BuildWorkSessionAsync(loaded, _resolved,
                    [.. _document.Messages.Where(message => message.Role != ChatRole.System)]).ConfigureAwait(false)
                : await BuildSessionAsync(loaded, _resolved, snapshot).ConfigureAwait(false);
        }
        catch
        {
            _host.Release(loaded);
            _loaded = null;
            throw;
        }
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
        var agent = _work.CreateAgent(_document?.Mode ?? "chat");
        return ChatSessionFactory.CreateChatSession(
            generator.BuilderType,
            cacheType,
            loaded.Model,
            loaded.Tokenizer,
            loaded.Meta,
            agentBuilder: agent,
            formatter: ChatFormatterFactory.Create(profile.Formatter, loaded.Meta),
            seed: profile.Seed);
    }

    private ResolvedInference ResolveInference(
        string? familyId,
        string modelPath,
        InferenceOverrides? overrides) =>
        _inference.Resolve(
            familyId,
            modelPath,
            overrides,
            workMode: string.Equals(_document?.Mode, "work", StringComparison.OrdinalIgnoreCase));
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
        session.ToolCallRecovery = profile.ToolCallRecovery;
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
        if (_document is null) return;
        if (IsRemote)
        {
            _store.Save(_document);
            _cacheWarning = null;
            return;
        }
        if (_session is null) return;
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
        _work.CancelPermission();
        _work.CancelChoice();
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _streaming = false;
        DisposeSession(_session);
        _session = null;
        if (_loaded is not null)
        {
            _host.Release(_loaded);
            _loaded = null;
        }
    }

    private static void DisposeSession(IChatSession? session)
    {
        if (session is null) return;
        try { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { }
    }
    private void Notify() => Changed?.Invoke();
}










































