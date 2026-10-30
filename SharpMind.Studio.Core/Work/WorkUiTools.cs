using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpMind.Core;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Agent-to-UI and small utility tools for Work. The choice callback is supplied
/// by the Studio circuit so a model can ask the person a question without
/// guessing what they meant.
/// </summary>
public sealed class WorkUiTools(
    Func<string, IReadOnlyList<string>, bool, Task<string>> requestChoice)
{
    [ToolDesc("Shows the person a list of options to choose from, optionally allowing a typed answer. Use this when the person should pick between concrete choices.")]
    public async Task<string> UIShowOptionSelection(
        [ToolDesc("A short question shown above the options.")] string prompt,
        [ToolDesc("Options the person can choose.")] List<string> options,
        [ToolDesc("Also allow a free-typed answer.")] bool allowFreeText = false)
    {
        if (options is null || options.Count == 0)
            return "At least one option is required.";
        return await requestChoice(prompt, options, allowFreeText);
    }

    [ToolDesc("Reports how much memory is currently available on this machine in megabytes.")]
    public static string UIGetFreeMemory()
    {
        var info = GC.GetGCMemoryInfo();
        long totalMb = info.TotalAvailableMemoryBytes / (1024 * 1024);
        long usedMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        return $"Total available memory: ~{totalMb} MB. This process uses ~{usedMb} MB.";
    }

    [ToolDesc("Reports the operating system, processor architecture and logical core count.")]
    public static string UIGetSystemSpecs() =>
        $"OS: {RuntimeInformation.OSDescription}. Architecture: {RuntimeInformation.OSArchitecture} " +
        $"({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}). Logical cores: {Environment.ProcessorCount}.";

    [ToolDesc("Returns the current local date and time.")]
    public static string GetCurrentDateTime() =>
        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

    [ToolDesc("Repeats the supplied text unchanged. Useful for verifying tool calls.")]
    public static string Echo([ToolDesc("Text to repeat.")] string text) => text;
}
