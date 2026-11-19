using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ThemeColorTests
{
    [Fact]
    public void Light_RejectsANearWhiteAccent()
    {
        string clamped = ThemeService.ClampColor("#ffffff", "light");

        Assert.NotEqual("#ffffff", clamped);
        Assert.StartsWith("#", clamped);
    }

    [Fact]
    public void Dark_RejectsANearBlackAccent()
    {
        string clamped = ThemeService.ClampColor("#000000", "dark");

        Assert.NotEqual("#000000", clamped);
    }

    [Fact]
    public void AReadableAccentIsLeftAlone()
    {
        // Mid-tone teal: inside the dark range, and inside the light range.
        Assert.Equal("#0d9488", ThemeService.ClampColor("#0d9488", "light"));
        Assert.Equal("#5eead4", ThemeService.ClampColor("#5eead4", "dark"));
    }

    [Fact]
    public void ClampingKeepsTheReturnedValueValid()
    {
        foreach (string appearance in ThemeService.AvailableAppearances)
        {
            foreach (string color in new[] { "#ffffff", "#000000", "#ff0000", "#00ff00" })
            {
                string clamped = ThemeService.ClampColor(color, appearance);
                Assert.NotNull(ThemeService.NormalizeColor(clamped));
            }
        }
    }
}
