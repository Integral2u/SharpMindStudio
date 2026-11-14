using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services;

/// <summary>
/// Decides whether a compute host is busy right now. Training, rehearsal and
/// exam all run through <c>StudioTrainingService</c>, so its current remote
/// provider identifies the host. Chat counts only while a turn is loading or
/// streaming from that host.
/// </summary>
public static class ComputeUsage
{
    public static string? Activity(
        VultrComputeInstance instance,
        string? trainingProviderId,
        bool trainingRunning,
        string? chatProviderId,
        bool chatBusy)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.OwnerProviderId is not { Length: > 0 } id) return null;

        if (trainingRunning
            && string.Equals(trainingProviderId, id, StringComparison.OrdinalIgnoreCase))
        {
            return "training";
        }

        if (chatBusy
            && string.Equals(chatProviderId, id, StringComparison.OrdinalIgnoreCase))
        {
            return "chat";
        }

        return null;
    }
}
