using SharpMind.Studio.Core.Training;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class TrainingTokenizationTests
{
    [Fact]
    public void AppendBoundary_AddsEosToTheEnd()
    {
        Assert.Equal([1, 2, 9], TrainingTokenization.AppendBoundary([1, 2], 9));
        Assert.Equal([9], TrainingTokenization.AppendBoundary([], 9));
    }

    [Fact]
    public void AppendBoundary_DoesNotDoubleAnExistingEos()
    {
        Assert.Equal([1, 2, 9], TrainingTokenization.AppendBoundary([1, 2, 9], 9));
    }

    [Fact]
    public void AppendBoundary_KeepsADifferentFinalToken()
    {
        Assert.Equal([1, 2, 3, 9], TrainingTokenization.AppendBoundary([1, 2, 3], 9));
    }
}
