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
        return instance.OwnerProviderId is { Length: > 0 } id
            ? Activity(id, trainingProviderId, trainingRunning, chatProviderId, chatBusy)
            : null;
    }

    public static string? Activity(
        string providerId,
        string? trainingProviderId,
        bool trainingRunning,
        string? chatProviderId,
        bool chatBusy)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;

        if (trainingRunning
            && string.Equals(trainingProviderId, providerId, StringComparison.OrdinalIgnoreCase))
        {
            return "training";
        }

        if (chatBusy
            && string.Equals(chatProviderId, providerId, StringComparison.OrdinalIgnoreCase))
        {
            return "chat";
        }

        return null;
    }
}
