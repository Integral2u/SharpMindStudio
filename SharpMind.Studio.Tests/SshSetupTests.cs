using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class SshSetupTests
{
    [Fact]
    public void Target_DerivesFromTheApiUrlAndDefaults()
    {
        var provider = new ComputeProviderSettings
        {
            Id = "host",
            BaseUrl = "http://203.0.113.10:8080",
        };

        SshTarget? target = SshTargets.FromSettings(provider);

        Assert.NotNull(target);
        Assert.Equal("203.0.113.10", target!.Host);
        Assert.Equal(22, target.Port);
        Assert.Equal("root", target.User);
    }

    [Fact]
    public void Target_UsesTheExplicitSshHostAndPort()
    {
        var provider = new ComputeProviderSettings
        {
            Id = "host",
            BaseUrl = "http://127.0.0.1:8080",
            SshHost = "gpu.example.com",
            SshPort = 2222,
            SshUser = "ubuntu",
        };

        SshTarget target = Assert.IsType<SshTarget>(SshTargets.FromSettings(provider));

        Assert.Equal("gpu.example.com", target.Host);
        Assert.Equal(2222, target.Port);
        Assert.Equal("ubuntu", target.User);
    }

    [Fact]
    public void Target_IgnoresALoopbackBaseUrl()
    {
        var provider = new ComputeProviderSettings
        {
            Id = "local",
            BaseUrl = "http://127.0.0.1:8080",
            SshHost = "",
        };

        Assert.Null(SshTargets.FromSettings(provider));
        Assert.True(SshTargets.IsLoopback("localhost"));
        Assert.True(SshTargets.IsLoopback("127.0.0.1"));
        Assert.False(SshTargets.IsLoopback("203.0.113.10"));
    }

    [Fact]
    public void Probe_ParsesTheMachineReadableLines()
    {
        const string output = """
            SM_OS=Ubuntu 24.04 LTS
            SM_ARCH=x86_64
            SM_DOCKER=27.1.1
            SM_GPU=NVIDIA A16
            SM_TOOLKIT=yes
            SM_DISK=45G
            """;

        SshProbe probe = SshProbe.Parse(output);

        Assert.Equal("Ubuntu 24.04 LTS", probe.Os);
        Assert.Equal("x86_64", probe.Architecture);
        Assert.Equal("27.1.1", probe.DockerVersion);
        Assert.Equal("NVIDIA A16", probe.Gpu);
        Assert.True(probe.HasNvidiaToolkit);
        Assert.Equal("45G", probe.DiskFree);
        Assert.True(probe.HasDocker);
        Assert.True(probe.HasGpu);
        Assert.Contains("Docker 27.1.1", probe.Summary);
    }

    [Fact]
    public void Probe_TreatsMissingValuesAsNotInstalled()
    {
        SshProbe probe = SshProbe.Parse("SM_OS=Debian\nSM_DOCKER=\nSM_GPU=\nSM_TOOLKIT=no\n");

        Assert.False(probe.HasDocker);
        Assert.False(probe.HasGpu);
        Assert.False(probe.HasNvidiaToolkit);
        Assert.Contains("Docker not installed", probe.Summary);
    }

    [Fact]
    public void Quote_HandlesApostrophes()
    {
        Assert.Equal("'plain'", SshSetupScript.Quote("plain"));
        Assert.Equal("'it'\\''s'", SshSetupScript.Quote("it's"));
    }

    [Fact]
    public void RunScript_EmbedsValuesAndAddsGpuOnlyWhenRequested()
    {
        var request = new SshSetupRequest
        {
            Image = "me/sharpmind-server:gpu",
            Token = "tok'en",
            ContainerName = "smtest",
            UseGpu = true,
            ApiPort = 8080,
            StudioPort = 5117,
        };

        string gpu = SshSetupScript.Run(request);

        Assert.Contains("IMAGE='me/sharpmind-server:gpu'", gpu);
        Assert.Contains("TOKEN='tok'\\''en'", gpu);
        Assert.Contains("CONTAINER='smtest'", gpu);
        Assert.Contains("API_PORT='8080'", gpu);
        Assert.Contains("STUDIO_PORT='5117'", gpu);
        Assert.Contains("--gpus all", gpu);
        Assert.Contains("docker rm -f \"$CONTAINER\"", gpu);
        Assert.Contains("SHARPMIND_TOKEN=$TOKEN", gpu);

        request = request with { UseGpu = false, Token = "plain" };
        Assert.DoesNotContain("--gpus all", SshSetupScript.Run(request));
    }

    [Fact]
    public void RunScript_RejectsInvalidPorts()
    {
        var request = new SshSetupRequest
        {
            Image = "me/server:gpu",
            Token = "t",
            ApiPort = 0,
        };

        Assert.Throws<InvalidOperationException>(() => SshSetupScript.Run(request));
    }

    [Fact]
    public void InspectAndLogs_QuoteTheContainerName()
    {
        Assert.Contains("NAME='it'\\''s'", SshSetupScript.Inspect("it's"));
        Assert.Contains("docker logs --tail 120 'sm'", SshSetupScript.Logs("sm", 120));
        Assert.Contains("docker stop \"$NAME\"", SshSetupScript.Stop("sm"));
    }

    [Fact]
    public void ImageResolution_FallsBackToThePublishedImage()
    {
        var provider = new ComputeProviderSettings
        {
            Id = "host",
            DockerImage = ComputeProviderPresets.DefaultImage,
        };

        Assert.Equal(
            "me/sharpmind-server:gpu",
            SshSetupScript.ResolveImage(provider, "me/sharpmind-server:gpu"));
        Assert.Equal(
            ComputeProviderPresets.DefaultServerImage,
            SshSetupScript.ResolveImage(provider, null));

        provider.DockerImage = "other/image:cpu";
        Assert.Equal("other/image:cpu", SshSetupScript.ResolveImage(provider, "me/sharpmind-server:gpu"));

        // A local-only tag is replaced by the image this Studio published.
        provider.DockerImage = "sharpmind-server:gpu";
        Assert.Equal(
            "me/sharpmind-server:gpu",
            SshSetupScript.ResolveImage(provider, "me/sharpmind-server:gpu"));
    }

    [Fact]
    public void LooksUnpublished_RequiresARegistryName()
    {
        Assert.True(SshSetupScript.LooksUnpublished("sharpmind-server:gpu"));
        Assert.True(SshSetupScript.LooksUnpublished("sharpmind-compute:local"));
        Assert.False(SshSetupScript.LooksUnpublished("me/sharpmind-server:gpu"));
        Assert.False(SshSetupScript.LooksUnpublished("ghcr.io/me/sharpmind-server:gpu"));
    }

    [Fact]
    public void SshSettings_AreEncryptedAndRoundTrip()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        string key = temp.Combine("compute.key");
        var store = new ComputeSettingsStore(settings, key);
        string id = store.Providers[0].Id;

        Assert.True(store.SetSsh(
            id,
            "203.0.113.10",
            2222,
            "ubuntu",
            @"C:\keys\id_ed25519",
            "pw-secret",
            "pass-secret",
            "smtest",
            useGpu: true));

        string json = File.ReadAllText(settings);
        Assert.DoesNotContain("pw-secret", json);
        Assert.DoesNotContain("pass-secret", json);
        Assert.Equal("pw-secret", store.SshPassword(id));
        Assert.Equal("pass-secret", store.SshKeyPassphrase(id));

        var reopened = new ComputeSettingsStore(settings, key);
        ComputeProviderSettings provider = reopened.Find(id)!;
        Assert.Equal("203.0.113.10", provider.SshHost);
        Assert.Equal(2222, provider.SshPort);
        Assert.Equal("ubuntu", provider.SshUser);
        Assert.Equal(@"C:\keys\id_ed25519", provider.SshKeyPath);
        Assert.Equal("smtest", provider.SshContainerName);
        Assert.True(provider.UseGpu);
        Assert.Equal("pw-secret", reopened.SshPassword(id));
        Assert.Equal("pass-secret", reopened.SshKeyPassphrase(id));
    }

    [Fact]
    public void SshSettings_NullKeepsSecretsAndBlankClearsThem()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));
        string id = store.Providers[0].Id;

        store.SetSsh(id, "host", 22, "root", null, "pw", "pass", null, false);
        store.SetSsh(id, "host", 22, "root", null, null, null, "sharpmind", true);
        Assert.Equal("pw", store.SshPassword(id));
        Assert.Equal("pass", store.SshKeyPassphrase(id));

        store.SetSsh(id, "host", 22, "root", null, "", "", null, false);
        Assert.Null(store.SshPassword(id));
        Assert.Null(store.SshKeyPassphrase(id));
    }

    [Fact]
    public void SetBaseUrl_ReDerivesTheStudioUrl()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));
        string id = store.Providers[0].Id;

        Assert.True(store.SetBaseUrl(id, "http://203.0.113.10:8080/v1"));

        ComputeProviderSettings provider = store.Find(id)!;
        Assert.Equal("http://203.0.113.10:8080", provider.BaseUrl);
        Assert.Equal("http://203.0.113.10:5117", provider.StudioUrl);
    }
}
