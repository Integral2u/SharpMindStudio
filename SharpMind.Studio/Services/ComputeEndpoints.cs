using Microsoft.AspNetCore.Http;
using SharpMind.Studio.Services;

namespace SharpMind.Studio.Services;

/// <summary>
/// Loopback-only endpoints used by the desktop wrapper. The web UI calls the
/// dashboard service directly; the wrapper needs JSON over HTTP because it lives
/// in a separate process.
/// </summary>
public static class ComputeEndpoints
{
    public static IEndpointRouteBuilder MapComputeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/compute/api/running", async (
            HttpContext context,
            ComputeDashboardService compute) =>
        {
            if (!GatewayEndpoints.IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                await compute.RefreshAsync(context.RequestAborted, force: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    count = 0,
                    hourlyCost = 0d,
                    estimatedCost = 0d,
                    error = ex.Message,
                    instances = Array.Empty<object>(),
                });
            }

            return Results.Json(new
            {
                count = compute.RunningCount,
                hourlyCost = compute.HourlyCost,
                estimatedCost = compute.EstimatedCost,
                error = compute.Error,
                instances = compute.Running.Select(instance => new
                {
                    instanceId = instance.InstanceId,
                    label = instance.Label,
                    providerName = instance.OwnerName,
                    status = instance.StatusLabel,
                    mainIp = instance.MainIp,
                    hourlyCost = instance.HourlyCost,
                    estimatedCost = instance.EstimatedCost,
                    uptimeMinutes = instance.Uptime?.TotalMinutes ?? 0,
                    orphan = instance.IsOrphan,
                }),
            });
        });

        app.MapPost("/compute/api/destroy-running", async (
            HttpContext context,
            ComputeDashboardService compute) =>
        {
            if (!GatewayEndpoints.IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            (int destroyed, IReadOnlyList<string> failures) = await compute
                .DestroyRunningAsync(context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(new { destroyed, failures });
        });

        return app;
    }
}
