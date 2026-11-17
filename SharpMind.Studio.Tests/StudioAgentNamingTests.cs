using SharpMind.Studio.Core.Agents;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class StudioAgentNamingTests
{
    [Theory]
    [InlineData(0.2f, "Alpha")]
    [InlineData(0.4f, "Beta")]
    [InlineData(0.65f, "Gamma")]
    [InlineData(0.95f, "Delta")]
    [InlineData(1.4f, "Epsilon")]
    public void Temperature_PicksTheGreekTier(float temperature, string expected) =>
        Assert.Equal(expected, StudioAgentNaming.ForTemperature(temperature));

    [Fact]
    public void UnsetTemperature_KeepsTheCuiDefault()
    {
        Assert.Equal("Delta", StudioAgentNaming.ForTemperature(null));
        Assert.Equal("Delta", StudioAgentNaming.Default);
    }
}
