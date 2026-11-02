using System.Threading.RateLimiting;

namespace SharpMind.Studio.Services;

/// <summary>A per-address budget for proxied phone requests.</summary>
public sealed class GatewayRateLimiter : IDisposable
{
    private const int PermitLimit = 600;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(
            key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitLimit,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            }),
            StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        using RateLimitLease lease = _limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan value)
            ? value
            : TimeSpan.FromSeconds(10);
        return false;
    }

    public void Dispose() => _limiter.Dispose();
}
