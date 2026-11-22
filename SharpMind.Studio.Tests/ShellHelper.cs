using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>The small console program used by the softshell tests.</summary>
internal static class ShellHelper
{
    public static string DllPath
    {
        get
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "ShellHelper",
                "SharpMind.Studio.Tests.ShellHelper.dll");
            Assert.True(
                File.Exists(path),
                $"The shell helper was not copied to '{path}'. Build the test project first.");
            return path;
        }
    }

    public static AllowedCommand Command() => new()
    {
        Name = "helper",
        Executable = "dotnet",
        ArgumentsPrefix = [DllPath],
        Description = "Test helper",
    };
}
