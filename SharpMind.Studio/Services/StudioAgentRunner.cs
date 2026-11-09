using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

/// <summary>
/// Runs a model-specific agent on its own model. The parent chat keeps its model;
/// the target is loaded through <see cref="StudioModelHost"/>, used for one
/// self-contained task and released. The host caches it until the next load or
/// release evicts the idle entry.
/// </summary>
public sealed class StudioAgentRunner
{
    private readonly StudioModelHost _host;
    private readonly ModelCatalogService _catalog;
    private readonly InferenceSettingsService _inference;
    private readonly StudioWorkspaceService _workspace;

    public StudioAgentRunner(
        StudioModelHost host,
        ModelCatalogService catalog,
        InferenceSettingsService inference,
        StudioWorkspaceService workspace)
    {
        _host = host;
        _catalog = catalog;
        _inference = inference;
        _workspace = workspace;
    }

    public async Task<string> RunAsync(
        StudioAgentDefinition agent,
        string task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        if (string.IsNullOrWhiteSpace(agent.ModelPath))
            throw new InvalidOperationException($"Agent '{agent.Name}' has no model path.");

        IReadOnlyList<ModelDescriptor> models = await _catalog.RefreshAsync(
            force: false, cancellationToken).ConfigureAwait(false);
        ModelDescriptor descriptor = models.FirstOrDefault(model =>
            string.Equals(model.Path, agent.ModelPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(model.FileName, agent.ModelPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Agent '{agent.Name}' targets a model that is no longer in the catalog: {agent.ModelPath}");

        ResolvedInference profile = _inference.Resolve(
            descriptor.Family.Id, descriptor.Path, session: null);
        LoadedStudioModel loaded = await _host.LoadAsync(
            descriptor, profile, progress: null, cancellationToken).ConfigureAwait(false);
        try
        {
            IChatSession session = CreateSession(loaded, profile, agent);
            try
            {
                session.InitializeChat();
                string rules = string.IsNullOrWhiteSpace(agent.Rules)
                    ? $"You are {agent.Name}. {agent.Description}"
                    : agent.Rules;
                session.AddMessage(ChatRole.System, rules);

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
            _host.Release(loaded, TimeSpan.FromMinutes(
                agent.TtlMinutes > 0 ? agent.TtlMinutes : 30));
        }
    }

    private IChatSession CreateSession(
        LoadedStudioModel loaded,
        ResolvedInference profile,
        StudioAgentDefinition agent)
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
            agentBuilder: null,
            postProcessor: new StudioArtifactPostProcessor(),
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
        return session;
    }
}
