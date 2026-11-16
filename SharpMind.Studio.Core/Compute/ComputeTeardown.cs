namespace SharpMind.Studio.Core.Compute;

/// <summary>
/// Ownership rule for destructive actions. A Vultr instance is ours only when a
/// saved host tracks its id; a tagged instance from another Studio install or a
/// deleted host entry is never torn down automatically.
/// </summary>
public static class ComputeTeardown
{
    public static bool IsOwned(VultrComputeInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.OwnerProviderId is { Length: > 0 };
    }
}
