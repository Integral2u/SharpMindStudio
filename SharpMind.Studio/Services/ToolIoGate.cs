using System.Text.Json.Nodes;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Services;

/// <summary>
/// One pair of IO gates shared by every path that runs a tool: the local chat
/// session, the remote provider loop, and the plugin runtime. Sharing the
/// instances is what makes a plugin's <see cref="PluginRuntime.Files"/> and
/// <see cref="PluginRuntime.Http"/> meaningful outside a local turn.
/// </summary>
public sealed class ToolIoGate
{
    private readonly StudioWorkService _work;

    public ToolIoGate(StudioWorkService work)
    {
        _work = work;
        PluginRuntime.Set(Files, Network);
    }

    public InterceptingFileSystem Files { get; } = new();

    public InterceptingNetworkHandler Network { get; } = new();

    /// <summary>Gates the IO a tool does through the shared instances.</summary>
    public async Task<T> RunAsync<T>(string tool, JsonObject arguments, Func<Task<T>> action)
    {
        IoPermissionCheck check = async (name, category, resource, args) =>
            await _work.CheckToolPermissionAsync(new ToolPermissionContext
            {
                ToolName = name,
                Category = category,
                Resource = resource,
                Arguments = args,
            }).ConfigureAwait(false) == ToolPermission.Always;

        Files.BeginToolIo(tool, arguments, check);
        Network.BeginToolIo(tool, arguments, check);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Files.EndToolIo();
            Network.EndToolIo();
        }
    }
}
