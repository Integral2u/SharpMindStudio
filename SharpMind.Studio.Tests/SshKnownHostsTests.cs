using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SshKnownHostsTests
{
    [Fact]
    public void FirstConnectPinsAndLaterConnectsMatch()
    {
        using var temp = new TempDirectory();
        var store = new SshKnownHosts(temp.Combine("known-hosts.json"));
        string fingerprint = SshKnownHosts.Fingerprint([1, 2, 3]);

        Assert.Equal(
            SshHostKeyStatus.New,
            store.Verify("host.example.com", 22, "ssh-ed25519", fingerprint, out SshKnownHost? first));
        Assert.NotNull(first);
        Assert.Equal(fingerprint, first!.Fingerprint);

        Assert.Equal(
            SshHostKeyStatus.Known,
            store.Verify("HOST.example.com", 22, "ssh-ed25519", fingerprint, out _));

        var reopened = new SshKnownHosts(temp.Combine("known-hosts.json"));
        Assert.Equal(fingerprint, reopened.Find("host.example.com", 22)?.Fingerprint);
    }

    [Fact]
    public void ChangedKeyIsRejectedAndKeepsTheOriginal()
    {
        using var temp = new TempDirectory();
        var store = new SshKnownHosts(temp.Combine("known-hosts.json"));
        string original = SshKnownHosts.Fingerprint([1, 2, 3]);
        string replacement = SshKnownHosts.Fingerprint([4, 5, 6]);

        store.Verify("host.example.com", 22, "ssh-ed25519", original, out _);

        Assert.Equal(
            SshHostKeyStatus.Changed,
            store.Verify("host.example.com", 22, "ssh-ed25519", replacement, out SshKnownHost? known));
        Assert.Equal(original, known!.Fingerprint);
        Assert.Equal(original, store.Find("host.example.com", 22)?.Fingerprint);
    }

    [Fact]
    public void ForgetRemovesOnlyThatEndpoint()
    {
        using var temp = new TempDirectory();
        var store = new SshKnownHosts(temp.Combine("known-hosts.json"));
        string fingerprint = SshKnownHosts.Fingerprint([9, 9, 9]);

        store.Verify("one.example.com", 22, "ssh-ed25519", fingerprint, out _);
        store.Verify("two.example.com", 2222, "ssh-rsa", fingerprint, out _);

        Assert.True(store.Forget("one.example.com", 22));
        Assert.Null(store.Find("one.example.com", 22));
        Assert.NotNull(store.Find("two.example.com", 2222));
        Assert.False(store.Forget("one.example.com", 22));
    }

    [Fact]
    public void FingerprintUsesOpenSshSha256Format()
    {
        string fingerprint = SshKnownHosts.Fingerprint([0, 1, 2, 3]);

        Assert.StartsWith("SHA256:", fingerprint);
        Assert.DoesNotContain("=", fingerprint);
        Assert.Matches("^SHA256:[A-Za-z0-9+/]+$", fingerprint);
    }
}
