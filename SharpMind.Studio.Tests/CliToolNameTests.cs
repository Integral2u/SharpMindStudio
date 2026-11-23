using SharpMind.Core;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class CliToolNameTests
{
    [Fact]
    public void Wrappers_UseTheDocumentedSnakeCaseNames()
    {
        Type[] types = [typeof(GitCliTools), typeof(DotnetCliTools), typeof(RipgrepCliTools)];

        string[] names = [.. types
            .SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttributes(typeof(ToolDescAttribute), inherit: false).Length > 0)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            ["dotnet_build", "dotnet_test", "git_diff", "git_log", "git_status", "rg_search"],
            names);
    }
}
