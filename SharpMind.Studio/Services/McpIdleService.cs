using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpMind.Studio.Core.Mcp;

namespace SharpMind.Studio.Services;

/// <summary>
/// Stops MCP server processes that have been idle past their own timeout. A
/// server starts on first use and keeps its tool list cached; without this
/// sweep every server Studio ever started would stay alive until the app
/// exited, and the per-server idle setting would do nothing.
/// </summary>
public sealed class McpIdleService : BackgroundService
{
    // The shortest selectable idle window is 15 minutes; a 30 second sweep
    // keeps the stop close enough without waking the process list constantly.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly McpServerHost _host;
    private readonly ILogger<McpIdleService> _log;

    public McpIdleService(McpServerHost host, ILogger<McpIdleService> log)
    {
        _host = host;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _host.StopIdleAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A server that will not stop is reported, not fatal: the
                    // next sweep tries again.
                    _log.LogWarning(ex, "Could not stop an idle MCP server.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
