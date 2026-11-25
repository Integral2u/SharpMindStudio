using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Services;

/// <summary>
/// Runs a defined or spawned agent in its own session. Same-model agents borrow
/// the parent's loaded weights; a named model is loaded through the host and
/// released with the agent's TTL. The agent gets the project's file/web tools
/// under its own Ask/Allow/Deny policy.
/// </summary>
public sealed class StudioAgentRunner
{
    private readonly StudioModelHost _host;
    private readonly ModelCatalogService _catalog;
    private readonly InferenceSettingsService _inference;
    private readonly StudioWorkspaceService _workspace;
    private readonly StudioWorkService _work;
    private readonly StudioAgentService _agents;
    private readonly StudioPluginService _plugins;
    private readonly ThemeService _theme;

    public StudioAgentRunner(
        StudioModelHost host,
        ModelCatalogService catalog,
        InferenceSettingsService inference,
        StudioWorkspaceService workspace,
        StudioWorkService work,
        StudioAgentService agents,
        StudioPluginService plugins,
        ThemeService theme)
    {
        _host = host;
        _catalog = catalog;
        _inference = inference;
        _workspace = workspace;
        _work = work;
        _agents = agents;
        _plugins = plugins;
        _theme = theme;
    }

    public async Task<string> RunAsync(
        StudioAgentDefinition agent,
        string task,
        LoadedStudioModel? current,
        ResolvedInference? currentProfile,
        CancellationToken cancellationToken = default,
        int depth = 0,
        string? sessionId = null,
        CommandAccessLayer? sessionCommands = null,
        bool workMode = true)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);

        (LoadedStudioModel loaded, ResolvedInference profile, bool borrowed) =
            await ResolveModelAsync(agent, current, currentProfile, cancellationToken)
                .ConfigureAwait(false);

        try
        {
            IAgentBuilder? tools = _work.CreateAgentTools(
                agent,
                sessionId: sessionId,
                familyId: loaded.Descriptor.Family.Id,
                modelPath: loaded.Descriptor.Path,
                sessionCommands: sessionCommands);
            if (tools is AgentBuilder builder)
            {
                builder.WithAdditionalSystemPrompt(AgentPromptText.DelegatedTask);
                builder.PluginCompactors = _plugins.Compactors;
                builder.PluginPreProcessors = _plugins.PreProcessors;
                builder.PluginPostProcessors = _plugins.PostProcessors;
                builder.Compactor = CompactorResolver.Resolve(profile.Compactor, _plugins.Compactors)
                    ?? _plugins.FindCompactor(_theme.PluginCompactor);
            }

            AddNestedDelegate(
                tools, agent, loaded, profile, depth, cancellationToken,
                sessionId, sessionCommands, workMode);
            IChatSession session = CreateSession(loaded, profile, agent, tools);
            try
            {
                session.InitializeChat();
                if (tools is null)
                {
                    string rules = string.IsNullOrWhiteSpace(agent.Rules)
                        ? $"You are {agent.Name}. {agent.Description}"
                        : agent.Rules;
                    session.AddMessage(
                        ChatRole.System, $"{rules.Trim()}\n\n{AgentPromptText.DelegatedTask}");
                }

                var text = new System.Text.StringBuilder();
                await foreach (ChatStreamEntry entry in session
                    .GetResponseStreamAsync(task, ct: cancellationToken)
                    .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(entry.Error))
                        throw new InvalidOperationException(entry.Error);
                    if (entry.Status == ChatStatus.Responding && entry.Token is not null)
                        text.Append(entry.Token);
                    else if (entry.Artifact is { } artifact)
                        text.Append($"[{artifact.Type} artifact {artifact.FileName}]");
                }

                string answer = text.ToString().Trim();
                return answer.Length == 0
                    ? $"Agent '{agent.Name}' produced no text."
                    : answer;
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (!borrowed)
            {
                _host.Release(loaded, TimeSpan.FromMinutes(
                    agent.TtlMinutes > 0 ? agent.TtlMinutes : 30));
            }
        }
    }

    /// <summary>
    /// Gives a delegated agent the other enabled specialists as a delegate tool,
    /// up to the project's max depth. The child borrows this agent's loaded model
    /// unless it names its own model, so nested runs do not reload the weights.
    /// </summary>
    private void AddNestedDelegate(
        IAgentBuilder? tools,
        StudioAgentDefinition parent,
        LoadedStudioModel loaded,
        ResolvedInference profile,
        int depth,
        CancellationToken cancellationToken,
        string? sessionId,
        CommandAccessLayer? sessionCommands,
        bool workMode)
    {
        if (tools is null || depth >= _agents.MaxDepth || !_work.IsToolEnabled("delegate"))
            return;

        List<StudioAgentDefinition> candidates = [.. _agents.EnabledAgents.Where(agent =>
            (workMode ? agent.AvailableInWork : agent.AvailableInChat) &&
            !string.Equals(agent.Id, parent.Id, StringComparison.Ordinal) &&
            !string.Equals(agent.Name, parent.Name, StringComparison.OrdinalIgnoreCase))];
        if (candidates.Count == 0) return;

        if (tools is AgentBuilder builder)
            builder.WithAdditionalSystemPrompt(AgentPromptText.Parent(candidates));

        var names = candidates
            .Select(agent => (JsonNode)JsonValue.Create(agent.Name)!)
            .ToList();
        var purposes = candidates.Select(agent =>
            string.IsNullOrWhiteSpace(agent.Description)
                ? agent.Name
                : $"{agent.Name}: {agent.Description}");
        tools.WithTool(
            "delegate",
            "Hands a self-contained sub-task to another specialist and returns its answer.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["agent"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray([.. names]),
                        ["description"] = string.Join("; ", purposes),
                    },
                    ["task"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The complete sub-task, with all the context the agent needs.",
                    },
                },
                ["required"] = new JsonArray("agent", "task"),
            },
            async arguments =>
            {
                string name = arguments["agent"]?.GetValue<string>() ?? "";
                string task = arguments["task"]?.GetValue<string>() ?? "";
                StudioAgentDefinition? child = candidates.FirstOrDefault(agent =>
                    string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                    return $"[agent '{name}' is not defined]";
                if (string.IsNullOrWhiteSpace(task))
                    return "[delegate needs a task]";
                return await RunAsync(
                    child,
                    task,
                    loaded,
                    profile,
                    cancellationToken,
                    depth + 1,
                    sessionId,
                    sessionCommands,
                    workMode)
                    .ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Picks the first usable model in the agent chain. Local targets that are
    /// missing from the catalog are skipped so the next target (often Inherit)
    /// can run instead of making the agent unavailable.
    /// </summary>
    private async Task<(LoadedStudioModel Loaded, ResolvedInference Profile, bool Borrowed)>
        ResolveModelAsync(
            StudioAgentDefinition agent,
            LoadedStudioModel? current,
            ResolvedInference? currentProfile,
            CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        IReadOnlyList<ModelDescriptor>? models = null;

        foreach (AgentModelChoice choice in agent.EffectiveModelChain())
        {
            if (choice.Kind == AgentModelKind.Inherit)
            {
                if (current is null)
                {
                    failures.Add("inherit (no current model)");
                    continue;
                }

                ResolvedInference inheritProfile = currentProfile ?? _inference.Resolve(
                    current.Descriptor.Family.Id, current.Descriptor.Path, session: null);
                return (current, inheritProfile, true);
            }

            if (choice.Kind != AgentModelKind.Local || string.IsNullOrWhiteSpace(choice.Model))
            {
                failures.Add($"{choice.Kind.ToString().ToLowerInvariant()} " +
                    $"{choice.Model ?? choice.ProviderId ?? "?"} (not available here)");
                continue;
            }

            models ??= await _catalog.RefreshAsync(force: false, cancellationToken)
                .ConfigureAwait(false);
            string needle = choice.Model.Trim();
            ModelDescriptor? descriptor = models.FirstOrDefault(model =>
                string.Equals(model.Path, needle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.FileName, needle, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null || !File.Exists(descriptor.Path))
            {
                failures.Add($"local {needle} (missing)");
                continue;
            }

            if (current is not null &&
                string.Equals(current.Descriptor.Path, descriptor.Path, StringComparison.OrdinalIgnoreCase))
            {
                ResolvedInference currentResolved = currentProfile ?? _inference.Resolve(
                    current.Descriptor.Family.Id, current.Descriptor.Path, session: null);
                return (current, currentResolved, true);
            }

            try
            {
                ResolvedInference profile = _inference.Resolve(
                    descriptor.Family.Id, descriptor.Path, session: null);
                LoadedStudioModel loaded = await _host.LoadAsync(
                    descriptor, profile, progress: null, cancellationToken).ConfigureAwait(false);
                return (loaded, profile, false);
            }
            catch (Exception ex)
            {
                failures.Add($"local {needle} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        string tried = failures.Count == 0 ? "no targets" : string.Join(", ", failures);
        throw new InvalidOperationException(
            $"Agent '{agent.Name}' has no available model target. Tried: {tried}.");
    }

    private IChatSession CreateSession(
        LoadedStudioModel loaded,
        ResolvedInference profile,
        StudioAgentDefinition agent,
        IAgentBuilder? tools)
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
        IChatSession session = ChatSessionFactory.CreateChatSession(
            generator.BuilderType,
            cacheType,
            loaded.Model,
            loaded.Tokenizer,
            loaded.Meta,
            agentBuilder: tools,
            preProcessor: _plugins.PreProcessor,
            postProcessor: _plugins.PostProcessor,
            formatter: ChatFormatterFactory.Create(profile.Formatter, loaded.Meta),
            seed: agent.Seed ?? profile.Seed);

        int maxContext = profile.MaxContextTokens is > 0
            ? profile.MaxContextTokens.Value
            : loaded.Config.MaxSeqLen;
        session.MaxTokens = Math.Clamp(maxContext, 64, Math.Max(64, loaded.Config.MaxSeqLen));
        session.MaxNewTokens = Math.Max(1, profile.MaxNewTokens);
        session.Temperature = agent.Temperature ?? profile.Temperature;
        session.TopK = profile.TopK;
        session.TopP = profile.TopP;
        session.RepetitionPenalty = profile.RepetitionPenalty;
        session.RepetitionWindow = profile.RepetitionWindow;
        session.MaxToolCallsPerTurn = agent.MaxToolCalls > 0
            ? agent.MaxToolCalls
            : int.MaxValue;
        return session;
    }
}
