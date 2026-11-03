using SharpMind.Studio.Core;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class StudioEndpointTests
{
    [Theory]
    [InlineData("127.0.0.1", StudioEndpointKind.Local)]
    [InlineData("localhost", StudioEndpointKind.Local)]
    [InlineData("::1", StudioEndpointKind.Local)]
    [InlineData("192.168.1.20", StudioEndpointKind.Remote)]
    [InlineData("studio.example.com", StudioEndpointKind.Remote)]
    [InlineData("", StudioEndpointKind.Local)]
    public void Classify_FollowsTheLoopbackBoundary(string host, StudioEndpointKind expected)
        => Assert.Equal(expected, StudioEndpoint.Classify(host));

    [Fact]
    public void Labels_AreReadable()
    {
        Assert.Equal("LOCAL HOST", StudioEndpoint.Label(StudioEndpointKind.Local));
        Assert.Equal("REMOTE HOST", StudioEndpoint.Label(StudioEndpointKind.Remote));
        Assert.Equal("CLOUD HOST", StudioEndpoint.Label(StudioEndpointKind.Cloud));
    }
}
