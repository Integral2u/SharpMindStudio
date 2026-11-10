using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
#if SHARPMIND_AUDIO
using SharpMind.Model.Audio;
#endif
#if SHARPMIND_SOURCE
using SharpMind.Model.Vision;
#endif
using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.OpenRouter;
using SharpMind.Studio.Core.Runs;
using SharpMind.Studio.Core.Work;
using SharpMind.Studio.Services.Chat;

namespace SharpMind.Studio.Services;

public sealed class StudioChatService : IAsyncDisposable
{
    private readonly StudioWorkspaceService _workspace;
    private readonly ModelCatalogService _catalog;
    private readonly InferenceSettingsService _inference;
    private readonly StudioAgentService _agents;
    private readonly StudioAgentRunner _agentRunner;
    private readonly OpenRouterSettingsService _openRouter;
    private readonly OpenRouterChatBackend _openRouterBackend;
    private readonly ComputeProviderService _compute;
    private readonly ComputeChatBackend _computeBackend;
    private readonly StudioModelHost _host;
    private readonly StudioWorkService _work;
    private readonly StudioDeviceAccess _device;
    private readonly StudioPluginService _plugins;
    private readonly ThemeService _theme;
    private ChatSessionStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatSession? _session;
    private LoadedStudioModel? _loaded;
#if SHARPMIND_AUDIO
    private Lfm3AudioRuntime? _audioRuntime;
    private Lfm3TtsRuntime? _ttsRuntime;
    private bool? _supportsAudio;
    private bool? _supportsTts;
    private bool? _isTtsModel;
    private bool? _isAudioModel;
#endif
#if SHARPMIND_SOURCE
    private bool? _supportsVision;
#endif
    private readonly SemaphoreSlim _audioGate = new(1, 1);
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
        OpenRouterChatBackend openRouterBackend,
        ComputeProviderService compute,
        ComputeChatBackend computeBackend,
        StudioModelHost host,
        StudioWorkService work,
        StudioAgentService agents,
        StudioAgentRunner agentRunner,
        StudioDeviceAccess device,
        StudioPluginService plugins,
        ThemeService theme)
    {
        _workspace = workspace;
        _catalog = catalog;
        _inference = inference;
        _agents = agents;
        _agentRunner = agentRunner;
        _openRouter = openRouter;
        _openRouterBackend = openRouterBackend;
        _compute = compute;
        _computeBackend = computeBackend;
        _host = host;
        _work = work;
        _device = device;
        _plugins = plugins;
        _theme = theme;
        _store = new ChatSessionStore(workspace.Project.ChatsDirectory);
        _work.AgentToolExecutor = ExecuteAgentToolAsync;
    }

    public event Action? Changed;

    public ChatSessionDocument? Document => _document;
    public ModelDescriptor? Model => _model;
    public ResolvedInference? Resolved => _resolved;
    public bool IsStreaming => _streaming;
    public bool IsLoading => _loading;
    public bool IsOpenRouter => string.Equals(_document?.Provider, "openrouter", StringComparison.OrdinalIgnoreCase);
    public bool IsCompute => string.Equals(_document?.Provider, "compute", StringComparison.OrdinalIgnoreCase);
    public bool IsRemote => IsOpenRouter || IsCompute;
#if SHARPMIND_AUDIO
    public bool SupportsAudio => _supportsAudio ??= ComputeSupportsAudio();
    public bool SupportsTts => _supportsTts ??= ComputeSupportsTts();
#else
    public bool SupportsAudio => false;
    public bool SupportsTts => false;
#endif
#if SHARPMIND_SOURCE
    public bool SupportsVision => _supportsVision ??= ComputeSupportsVision();
#else
    public bool SupportsVision => false;
#endif
    public string? ComputeProviderId => _document?.RemoteProvider;
    public string? RemoteProviderName => IsCompute
        ? _compute.Find(_document?.RemoteProvider)?.Name ?? "SharpMind Compute"
        : IsOpenRouter ? "OpenRouter" : null;
    public bool IsWorkMode => string.Equals(_document?.Mode, "work", StringComparison.OrdinalIgnoreCase);
    public bool IsChatActive => (_loading || _streaming) && !IsWorkMode;
    public bool IsWorkActive => (_loading || _streaming) && IsWorkMode;
    public double? LoadProgress => _loadProgress;
    public string? LoadDetail => _loadDetail;
    public bool IsOpen => _document is not null;
    public string? RemoteModel => _document?.RemoteModel;
    public bool OpenRouterConfigured => _openRouter.IsConfigured;
    public string Status => _status;
    public string? Error => _error;
    public string? CacheWarning => _cacheWarning;
    public double? LastTokensPerSecond => _document?.LastTokensPerSecond;
    public decimal? LastCost => _document?.LastCost;
    public int? LastPromptTokens => _document?.LastPromptTokens;
    public int? LastCompletionTokens => _document?.LastCompletionTokens;

#if SHARPMIND_AUDIO
    private bool ComputeSupportsAudio()
    {
        // Resolve from the selected model and its projector, not from the loaded
        // transformer: audio-only turns must be sendable before the first load.
        if (IsRemote || _model is null) return false;
        return Lfm3AudioRuntime.FindProjector(_model.Path) is not null;
    }

    private bool ComputeSupportsTts()
    {
        if (IsRemote || _model is null) return false;
        string? projector = Lfm3AudioRuntime.FindProjector(_model.Path);
        return projector is not null && Lfm3TtsRuntime.IsTtsProjector(projector);
    }

    /// <summary>
    /// True when the model path has an LFM3-Audio projector. The welcome composer
    /// uses this before NewChatAsync has created the document.
    /// </summary>
    public bool SupportsAudioFor(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return false;
        return Lfm3AudioRuntime.FindProjector(modelPath) is not null;
    }

    /// <summary>
    /// True for an audio model whose projector carries the TTS weights. This only
    /// looks at the model path and the mmproj, so the first text prompt can be
    /// recognized before the model is loaded.
    /// </summary>
    private bool IsTtsModel() => _isTtsModel ??= ComputeIsTtsModel();

    private bool ComputeIsTtsModel()
    {
        if (IsRemote || _model is null) return false;
        string? projector = Lfm3AudioRuntime.FindProjector(_model.Path);
        return projector is not null && Lfm3TtsRuntime.IsTtsProjector(projector);
    }

    /// <summary>True when the model has an audio encoder/projector, so attached audio can be transcribed.</summary>
    private bool IsAudioModel() => _isAudioModel ??= ComputeIsAudioModel();

    private bool ComputeIsAudioModel()
    {
        if (IsRemote || _model is null) return false;
        string? projector = Lfm3AudioRuntime.FindProjector(_model.Path);
        return projector is not null && Lfm3AudioRuntime.IsAudioProjector(projector);
    }
#endif
#if SHARPMIND_SOURCE
    private bool ComputeSupportsVision()
    {
        if (IsRemote || _model is null) return false;
        return Lfm3VlRuntime.FindProjector(_model.Path) is not null;
    }
#endif

    /// <summary>
    /// Transcribes a WAV artifact with the native LFM3-Audio encoder. The first call
    /// loads the projector; later calls reuse it until the model is released.
    /// </summary>
    public async Task<string> TranscribeAsync(
        ChatArtifact artifact,
        string language = "English",
        CancellationToken cancellationToken = default)
    {
#if SHARPMIND_AUDIO
        ArgumentNullException.ThrowIfNull(artifact);
        if (IsRemote || _model is null)
            throw new InvalidOperationException("Load a local LFM3-Audio model before transcribing.");
        string projector = Lfm3AudioRuntime.FindProjector(_model.Path)
            ?? throw new NotSupportedException(
                "No LFM3-Audio projector (mmproj) was found next to this model. " +
                "Put mmproj-*.gguf in the model folder or its LFM3-Audio sibling folder.");

        // An audio-only first turn arrives before any text turn has loaded the
        // backbone. Load it here so the transcription can run.
        if (_loaded is null)
        {
            _resolved ??= ResolveInference(
                _model.Family.Id, _model.Path, _document?.Overrides);
            await OpenCoreAsync(null).ConfigureAwait(false);
        }
        if (_loaded is null)
            throw new InvalidOperationException("The audio model could not be loaded.");
        if (!_loaded.Config.Architecture.StartsWith("lfm3", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This model is not an LFM3 text backbone.");

        await _audioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Each runtime dequantizes the projector; keep only one alive at a time.
            _ttsRuntime?.Dispose();
            _ttsRuntime = null;
            _audioRuntime ??= new Lfm3AudioRuntime(
                _loaded.Model, _loaded.Tokenizer, _loaded.Config, projector);
            _status = "Transcribing audio...";
            _error = null;
            Notify();

            string transcript = await Task.Run(
                () => _audioRuntime.TranscribeWav(artifact.Content, language, 256, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            _status = string.IsNullOrWhiteSpace(transcript) ? "No speech detected." : "Transcript ready.";
            return transcript;
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not transcribe audio.";
            throw;
        }
        finally
        {
            _audioGate.Release();
            Notify();
        }
#else
        await Task.CompletedTask;
        throw new NotSupportedException(
            "Audio transcription needs the SharpMind source build (the sibling repository) or the 1.1.1 packages.");
#endif
    }
    /// <summary>
    /// Synthesizes text with the native LFM3-Audio depth decoder and LiquidCodec. The first
    /// call loads the projector; later calls reuse it until the model or the other audio
    /// runtime is released.
    /// </summary>
    public async Task<ChatArtifact> SynthesizeAsync(
        string text,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
#if SHARPMIND_AUDIO
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (IsRemote || _loaded is null || _model is null)
            throw new InvalidOperationException("Load a local LFM3-Audio model before synthesizing speech.");
        if (!_loaded.Config.Architecture.StartsWith("lfm3", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This model is not an LFM3 text backbone.");
        string projector = Lfm3AudioRuntime.FindProjector(_model.Path)
            ?? throw new NotSupportedException(
                "No LFM3-Audio projector (mmproj) was found next to this model. " +
                "Put mmproj-*.gguf in the model folder or its LFM3-Audio sibling folder.");
        if (!Lfm3TtsRuntime.IsTtsProjector(projector))
            throw new NotSupportedException(
                "This projector has no speech-output weights (the depth decoder and LiquidCodec).");

        await _audioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Each runtime dequantizes the projector; keep only one alive at a time.
            _audioRuntime?.Dispose();
            _audioRuntime = null;
            _ttsRuntime ??= new Lfm3TtsRuntime(
                _loaded.Model, _loaded.Tokenizer, _loaded.Config, projector);
            ApplyTtsOptions(_ttsRuntime);
            _ttsRuntime.FrameSampled = (index, _) =>
            {
                _status = $"Synthesizing speech... frame {index}";
                Notify();
            };
            _ttsRuntime.EndOfSpeech = (index, _) =>
            {
                _status = $"Synthesizing speech... {index} frames";
                Notify();
            };
            _status = "Synthesizing speech...";
            _error = null;
            Notify();

            float[] audio = await Task.Run(
                () => _ttsRuntime.Synthesize(text, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (audio.Length == 0)
                throw new InvalidOperationException("The model produced no speech for this text.");

            using var buffer = new MemoryStream();
            WavFile.Write(buffer, audio, Lfm3TtsRuntime.SampleRate);
            _status = "Speech ready.";
            return new ChatArtifact
            {
                Type = "audio",
                FileName = fileName ?? "speech.wav",
                Content = buffer.ToArray(),
            };
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not synthesize speech.";
            throw;
        }
        finally
        {
            _audioGate.Release();
            Notify();
        }
#else
        await Task.CompletedTask;
        throw new NotSupportedException(
            "Speech synthesis needs the SharpMind source build (the sibling repository).");
#endif
    }

#if SHARPMIND_AUDIO
    /// <summary>Applies the chat's speech overrides to the cached TTS runtime.</summary>
    private void ApplyTtsOptions(Lfm3TtsRuntime runtime)
    {
        InferenceOverrides overrides = _document?.Overrides ?? new InferenceOverrides();
        runtime.Voice = string.IsNullOrWhiteSpace(overrides.TtsVoice)
            ? "female"
            : overrides.TtsVoice!;
        runtime.Language = string.IsNullOrWhiteSpace(overrides.TtsLanguage)
            ? "American English"
            : overrides.TtsLanguage!;
        runtime.MaxFrames = Math.Clamp(overrides.TtsMaxFrames ?? 128, 1, 512);
        runtime.Temperature = overrides.TtsTemperature is { } temperature
            ? Math.Clamp(temperature, 0.1f, 3f)
            : 1.0f;
        runtime.TopK = Math.Clamp(overrides.TtsTopK ?? 128, 1, 1024);
        runtime.Seed = overrides.TtsSeed;
    }
#endif

    private static bool HasAudioArtifact(IReadOnlyList<ChatArtifact>? artifacts) =>
        artifacts?.Any(item =>
            item.Type.Contains("audio", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Audio in, text out: the transcription becomes the assistant reply, and the
    /// attached audio stays on the user message for playback.
    /// </summary>
    private Task SendTranscriptionTurnAsync(
        string typedText, string transcript, IReadOnlyList<ChatArtifact>? artifacts)
    {
        ChatSessionDocument document = _document!;
        _error = null;
        ChatMessage user = ChatMessage.User(
            string.IsNullOrWhiteSpace(typedText) ? "Audio" : typedText.Trim(), "You");
        user.Artifacts = artifacts?.ToArray();
        document.Messages.Add(user);
        document.Messages.Add(ChatMessage.Agent(transcript.Trim()));
        _status = "Transcribed.";
        _store.Save(document);
        Notify();
        return Task.CompletedTask;
    }

    /// <summary>
    /// An LFM3-Audio text prompt is a text-to-speech request: the user's words are
    /// synthesized into the assistant's audio artifact, with no text reply and no
    /// manual Speak step.
    /// </summary>
    private async Task SendSpeechTurnAsync(string text, IReadOnlyList<ChatArtifact>? artifacts)
    {
        string speech = SpeechText.ToSpeech(text);
        if (speech.Length == 0)
        {
            _error = "Nothing to synthesize: the message has no speakable text.";
            _status = "Enter some words to generate audio.";
            Notify();
            return;
        }

        if (_model is null)
        {
            _error = "Load a local LFM3-Audio model before generating speech.";
            _status = "No audio model is loaded.";
            Notify();
            return;
        }

        if (_loaded is null)
        {
            _resolved ??= ResolveInference(
                _model.Family.Id, _model.Path, _document?.Overrides);
            await OpenCoreAsync(null).ConfigureAwait(false);
            if (_loaded is null)
            {
                _error = "The audio model could not be loaded.";
                _status = "Audio model load failed.";
                Notify();
                return;
            }
        }

        ChatSessionDocument document = _document!;
        _streaming = true;
        _error = null;
        _status = "Synthesizing speech...";
        _streamCts = new CancellationTokenSource();

        ChatMessage userMessage = ChatMessage.User(text, "You");
        userMessage.Artifacts = artifacts?.ToArray();
        document.Messages.Add(userMessage);
        ChatMessage assistant = ChatMessage.Agent("");
        document.Messages.Add(assistant);
        Notify();
        await Task.Yield();

        try
        {
            ChatArtifact audio = await SynthesizeAsync(
                speech, $"speech-{DateTime.Now:HHmmss}.wav", _streamCts.Token)
                .ConfigureAwait(false);
            assistant.Artifacts = [audio];
            _status = "Audio ready.";
        }
        catch (OperationCanceledException)
        {
            _status = "Stopped.";
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Could not synthesize speech.";
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            _streaming = false;
            _store.Save(document);
            Notify();
        }
    }

    /// <summary>Synthesizes a reply and attaches the WAV artifact to the message.</summary>
    public async Task<ChatArtifact?> SpeakAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role is not (ChatRole.Agent or ChatRole.User)) return null;
        if (string.IsNullOrWhiteSpace(message.Content)) return null;
        string speech = SpeechText.ToSpeech(message.Content);
        if (speech.Length == 0) return null;
        string fileName = $"speech-{DateTime.Now:HHmmss}.wav";
        ChatArtifact artifact = await SynthesizeAsync(speech, fileName, cancellationToken).ConfigureAwait(false);
        message.Artifacts = [.. (message.Artifacts ?? []), artifact];
        if (_document is not null) _store.Save(_document);
        Notify();
        return artifact;
    }



    public int? LastToolCalls => _document?.LastToolCalls;
    public string? PendingPrompt { get; set; }

    /// <summary>Attachments for the first turn of a chat started from the welcome composer.</summary>
    public IReadOnlyList<ChatArtifact> PendingArtifacts { get; set; } = [];

    public IReadOnlyList<ChatSessionSummary> Sessions => _store.List();

    /// <summary>Full-text search across saved chat/work sessions.</summary>
    public IReadOnlyList<ChatSessionSummary> SearchSessions(string query) =>
        _store.Search(query);

    public string? TakePendingPrompt()
    {
        string? prompt = PendingPrompt;
        PendingPrompt = null;
        return prompt;
    }

    public IReadOnlyList<ChatArtifact> TakePendingArtifacts()
    {
        IReadOnlyList<ChatArtifact> artifacts = PendingArtifacts;
        PendingArtifacts = [];
        return artifacts;
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
            _openRouter.SetDefaultModel(modelId.Trim());
            Notify();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a chat backed by a SharpMind Compute host. The host streams an
    /// OpenAI-compatible completion; no local weights are loaded.
    /// </summary>
    public async Task NewComputeChatAsync(
        string providerId,
        string modelId,
        string? systemPrompt,
        string mode = "chat",
        InferenceOverrides? overrides = null)
    {
        ComputeProvider provider = _compute.Resolve(providerId)
            ?? throw new InvalidOperationException("The Compute host was not found.");

        string model = string.IsNullOrWhiteSpace(modelId)
            ? provider.DefaultModel ?? provider.Models.FirstOrDefault() ?? ""
            : modelId.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The Compute host has no model yet. Add one in Settings → Providers.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            _document = _store.CreateCompute(
                provider.Id, provider.Name, model, systemPrompt, mode);
            _document.Overrides = overrides ?? new InferenceOverrides();
            _model = null;
            _resolved = null;
            _error = null;
            _status = $"Ready ({provider.Name}).";
            _cacheWarning = null;
            _compute.RememberModel(provider.Id, model);
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

            if (string.Equals(document.Provider, "compute", StringComparison.OrdinalIgnoreCase))
            {
                _model = null;
                _resolved = null;
                _error = null;
                _status = $"Ready ({_compute.Find(document.RemoteProvider)?.Name ?? "Compute"}).";
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

    public async Task SendAsync(string text, IReadOnlyList<ChatArtifact>? artifacts = null)
    {
        bool audioAttached = artifacts?.Any(item =>
            item.Type.Contains("audio", StringComparison.OrdinalIgnoreCase)) == true;
        bool audioOnly = string.IsNullOrWhiteSpace(text) &&
            audioAttached && !IsRemote && SupportsAudio;
        if ((string.IsNullOrWhiteSpace(text) && !audioOnly) || _document is null || _streaming)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string typedText = text;
            string? audioTranscript = null;
            if (IsRemote && artifacts is { Count: > 0 })
            {
                _error = "Attachments are only supported for local models right now.";
                _status = "Attachments are not available on remote chats yet.";
                Notify();
                return;
            }

            if (artifacts is { Count: > 0 } && SupportsAudio)
            {
                ChatArtifact? audio = artifacts.FirstOrDefault(
                    item => item.Type.Contains("audio", StringComparison.OrdinalIgnoreCase));
                if (audio is not null)
                {
                    try
                    {
                        string transcript = await TranscribeAsync(audio).ConfigureAwait(false);
                        audioTranscript = string.IsNullOrWhiteSpace(transcript) ? null : transcript;
                        if (audioTranscript is not null)
                        {
                            text = string.IsNullOrWhiteSpace(text)
                                ? transcript
                                : transcript + "\n\n" + text;
                        }
                    }
                    catch
                    {
                        // TranscribeAsync records the error; do not send an audio-only prompt.
                        return;
                    }

                    if (audioTranscript is null && string.IsNullOrWhiteSpace(typedText))
                    {
                        _status = "No speech detected.";
                        Notify();
                        return;
                    }
                }
            }

#if SHARPMIND_AUDIO
            // Audio in, text out: an audio model's transcription is the answer,
            // not a prompt for the text generator (which would just answer ":").
            if (audioTranscript is not null && IsAudioModel())
            {
                await SendTranscriptionTurnAsync(typedText, audioTranscript, artifacts)
                    .ConfigureAwait(false);
                return;
            }
#endif

#if SHARPMIND_SOURCE
            if (SupportsVision && artifacts?.FirstOrDefault(
                    item => item.Type.Contains("image", StringComparison.OrdinalIgnoreCase)) is { } image)
            {
                await SendVisionTurnAsync(text.Trim(), image).ConfigureAwait(false);
                return;
            }
#endif

            if (IsOpenRouter)
            {
                await SendRemoteTurnAsync(text.Trim(), _openRouterBackend).ConfigureAwait(false);
                return;
            }

            if (IsCompute)
            {
                await SendRemoteTurnAsync(text.Trim(), _computeBackend).ConfigureAwait(false);
                return;
            }

            // An LFM3-Audio model is a speech model: a text prompt generates audio
            // directly instead of a text reply plus a manual Speak button. Audio
            // attachments still go through transcription above.
#if SHARPMIND_AUDIO
            if (IsTtsModel() && !IsWorkMode && !HasAudioArtifact(artifacts))
            {
                await SendSpeechTurnAsync(text.Trim(), artifacts).ConfigureAwait(false);
                return;
            }
#endif

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
            ChatMessage userMessage = ChatMessage.User(text.Trim(), "You");
            userMessage.Artifacts = artifacts?.ToArray();
            _document.Messages.Add(userMessage);
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
                    .GetResponseStreamAsync(
                        text.Trim(), artifacts?.ToArray(), ct: _streamCts.Token)
                    .WithCancellation(_streamCts.Token).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(entry.Error))
                        _error = entry.Error;
                    if (entry.TokensPerSecond is { } entryTps) lastTps = entryTps;

                    if (entry.Artifact is { } artifact)
                    {
                        assistant.Artifacts = [.. (assistant.Artifacts ?? []), artifact];
                        _status = "Artifact ready.";
                    }
                    else if (entry.Status == ChatStatus.Responding && entry.Token is not null)
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

#if SHARPMIND_SOURCE
    /// <summary>
    /// Runs one native LFM3-VL turn. The image is decoded and described directly instead
    /// of going through ChatSession, which is a text-only prompt pipeline; the transcript
    /// keeps the image artifact and the answer, and the next text turn rebuilds the session
    /// from the document so the vision turn stays in context.
    /// </summary>
    private async Task SendVisionTurnAsync(string text, ChatArtifact image)
    {
        if (_document is null || _model is null) return;

        _streaming = true;
        _error = null;
        _status = "Reading image...";
        _streamCts = new CancellationTokenSource();
        ChatMessage userMessage = ChatMessage.User(text, "You");
        userMessage.Artifacts = [image];
        _document.Messages.Add(userMessage);
        ChatMessage assistant = ChatMessage.Agent("");
        _document.Messages.Add(assistant);
        Notify();

        LoadedStudioModel? loaded = _loaded;
        bool borrowed = false;
        try
        {
            if (loaded is null)
            {
                ResolvedInference resolved = _resolved
                    ?? ResolveInference(_model.Family.Id, _model.Path, _document.Overrides);
                loaded = await _host.LoadAsync(_model, resolved, cancellationToken: _streamCts.Token)
                    .ConfigureAwait(false);
                borrowed = true;
            }

            LoadedStudioModel model = loaded
                ?? throw new InvalidOperationException("The vision model is not loaded.");
            string projector = Lfm3VlRuntime.FindProjector(_model.Path)
                ?? throw new NotSupportedException(
                    "No LFM3-VL vision projector (mmproj) was found next to this model. " +
                    "Put mmproj-*.gguf in the model folder.");
            InferenceOverrides visionOverrides = _document.Overrides;
            int maxSide = Math.Clamp(visionOverrides.VisionMaxSide ?? 512, 128, 1536);
            int maxAnswerTokens = Math.Clamp(visionOverrides.VisionMaxTokens ?? 128, 16, 1024);
            float[] chw = StudioImageInput.Decode(image.Content, maxSide, out int height, out int width);
            _status = $"Describing image ({height}x{width})...";
            Notify();

            CancellationToken token = _streamCts.Token;
            string answer = await Task.Run(() =>
            {
                using var runtime = new Lfm3VlRuntime(
                    model.Model, model.Tokenizer, model.Config, projector);
                return runtime.Describe(chw, height, width, text, maxAnswerTokens, token);
            }, token).ConfigureAwait(false);

            assistant.Content = answer.Trim();
            _status = "Ready.";
        }
        catch (OperationCanceledException)
        {
            _status = "Stopped.";
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            _status = "Image understanding failed.";
        }
        finally
        {
            if (borrowed && loaded is not null) _host.Release(loaded);
            _streamCts?.Dispose();
            _streamCts = null;
            _streaming = false;
            DisposeSession(_session);
            _session = null;
            await SaveSnapshotAsync().ConfigureAwait(false);
            Notify();
        }
    }
#endif

    /// <summary>
    /// Shared turn setup for the remote backends: validate, append the user row
    /// and the empty assistant row, stream through the provider, then persist.
    /// </summary>
    private async Task SendRemoteTurnAsync(string text, IChatBackend backend)
    {
        if (_document is null) return;

        string? validation = backend.Validate(_document);
        if (validation is not null)
        {
            _error = validation;
            _status = validation;
            Notify();
            return;
        }

        _streaming = true;
        _error = null;
        _status = "Thinking...";
        _streamCts = new CancellationTokenSource();

        _document.Messages.Add(ChatMessage.User(text, "You"));
        ChatMessage assistant = ChatMessage.Agent("");
        _document.Messages.Add(assistant);
        Notify();
        await Task.Yield();

        var context = new ChatBackendContext
        {
            Document = _document,
            Assistant = assistant,
            UserText = text,
            Agent = _work.CreateAgent(_document?.Mode ?? "chat"),
            SetStatus = status => _status = status,
            Notify = Notify,
        };

        try
        {
            await backend.RunTurnAsync(context, _streamCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RemoveEmptyAssistant(assistant);
            _status = "Stopped.";
        }
        catch (Exception ex)
        {
            RemoveEmptyAssistant(assistant);
            _error = ex.Message;
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

    /// <summary>
    /// Removes the placeholder row when a turn produced neither text nor tool
    /// metadata. ChatMessage.Metadata is init-only, so the row is replaced in
    /// place when tool calls are attached.
    /// </summary>
    private void RemoveEmptyAssistant(ChatMessage assistant)
    {
        if (_document is null) return;
        if (string.IsNullOrWhiteSpace(assistant.Content) && assistant.Metadata is null)
            _document.Messages.Remove(assistant);
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
        _audioGate.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task OpenCoreAsync(ChatSessionSnapshot? snapshot)
    {
        DisposeAudioRuntime();
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
        DisposeAudioRuntime();
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
        DisposeAudioRuntime();
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
        if (agent is AgentBuilder builder)
        {
            builder.PluginCompactors = _plugins.Compactors;
            builder.PluginPreProcessors = _plugins.PreProcessors;
            builder.PluginPostProcessors = _plugins.PostProcessors;
            builder.Compactor = _plugins.FindCompactor(_theme.PluginCompactor);
        }

        IChatSession session = ChatSessionFactory.CreateChatSession(
            generator.BuilderType,
            cacheType,
            loaded.Model,
            loaded.Tokenizer,
            loaded.Meta,
            agentBuilder: agent,
            preProcessor: _plugins.PreProcessor,
            postProcessor: _plugins.PostProcessor,
            formatter: ChatFormatterFactory.Create(profile.Formatter, loaded.Meta),
            seed: profile.Seed);
        session.ProcessToolRequest = HandleAgentToolAsync;
        return session;
    }

    /// <summary>
    /// Routes agent tools to <see cref="StudioAgentRunner"/>.
    /// <c>delegate</c> runs a defined agent, including one that borrows the current
    /// model. <c>spawn_agent</c> creates a temporary specialist from the call's
    /// name/purpose/rules/model/tool list; neither path registers the delegate tool
    /// on the nested run, so an agent cannot recurse through itself.
    /// </summary>
    private async Task<ToolRequestResult> HandleAgentToolAsync(
        string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (string.Equals(toolName, "delegate", StringComparison.OrdinalIgnoreCase))
            return await HandleDelegateAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (string.Equals(toolName, "spawn_agent", StringComparison.OrdinalIgnoreCase))
            return await HandleSpawnAgentAsync(arguments, cancellationToken).ConfigureAwait(false);
        return ToolRequestResult.Defer();
    }

    /// <summary>
    /// The backend-neutral executor used by OpenRouter and Compute sessions, whose
    /// tool loop calls the registered tool directly instead of ProcessToolRequest.
    /// </summary>
    private async Task<string> ExecuteAgentToolAsync(
        string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        ToolRequestResult result = await HandleAgentToolAsync(
            toolName, arguments, cancellationToken).ConfigureAwait(false);
        return result.Result ?? "";
    }

    private async Task<ToolRequestResult> HandleDelegateAsync(
        JsonObject arguments, CancellationToken cancellationToken)
    {
        string name = arguments["agent"]?.GetValue<string>() ?? "";
        string task = arguments["task"]?.GetValue<string>() ?? "";
        StudioAgentDefinition? definition = _agents.Find(name);
        if (definition is null)
            return ToolRequestResult.Handled($"[agent '{name}' is not defined]");
        if (!definition.Enabled)
            return ToolRequestResult.Handled($"[agent '{definition.Name}' is disabled]");
        if (string.IsNullOrWhiteSpace(task))
            return ToolRequestResult.Handled("[delegate needs a task]");

        return await RunAgentAsync(definition, task, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolRequestResult> HandleSpawnAgentAsync(
        JsonObject arguments, CancellationToken cancellationToken)
    {
        string name = arguments["name"]?.GetValue<string>() ?? "";
        string rules = arguments["rules"]?.GetValue<string>() ?? "";
        string task = arguments["task"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rules) ||
            string.IsNullOrWhiteSpace(task))
        {
            return ToolRequestResult.Handled(
                "[spawn_agent needs a name, rules and task]");
        }

        string? model = arguments["model"]?.GetValue<string>();
        if (string.Equals(model, "current", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model, "inherit", StringComparison.OrdinalIgnoreCase))
        {
            model = null;
        }

        List<string> allowedTools = [];
        if (arguments["allowed_tools"] is JsonArray tools)
        {
            foreach (JsonNode? node in tools)
            {
                if (node?.GetValue<string>() is { Length: > 0 } tool)
                    allowedTools.Add(tool);
            }
        }

        var definition = new StudioAgentDefinition
        {
            Id = $"spawn-{Guid.NewGuid():n}",
            Name = name.Trim(),
            Description = arguments["purpose"]?.GetValue<string>() ?? "",
            Rules = rules.Trim(),
            ModelPath = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            AllowedTools = allowedTools,
            // A spawned specialist keeps the same safe Ask defaults as a defined agent.
            Permissions = new WorkPermissions
            {
                Read = WorkPermission.Ask,
                Write = WorkPermission.Ask,
                Delete = WorkPermission.Ask,
                Web = WorkPermission.Ask,
            },
        };

        return await RunAgentAsync(definition, task, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolRequestResult> RunAgentAsync(
        StudioAgentDefinition definition,
        string task,
        CancellationToken cancellationToken)
    {
        try
        {
            string result = await _agentRunner
                .RunAsync(definition, task, _loaded, _resolved, cancellationToken)
                .ConfigureAwait(false);
            return ToolRequestResult.Handled(result);
        }
        catch (Exception ex)
        {
            return ToolRequestResult.Handled(
                $"[agent {definition.Name} failed] {ex.GetType().Name}: {ex.Message}");
        }
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
#if SHARPMIND_SOURCE
        session.PromptLookupDraft = profile.PromptLookupDraft;
#endif
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

    private void DisposeAudioRuntime()
    {
#if SHARPMIND_AUDIO
        _audioRuntime?.Dispose();
        _audioRuntime = null;
        _ttsRuntime?.Dispose();
        _ttsRuntime = null;
        _supportsAudio = null;
        _supportsTts = null;
        _isTtsModel = null;
        _isAudioModel = null;
#endif
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
        DisposeAudioRuntime();
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










































