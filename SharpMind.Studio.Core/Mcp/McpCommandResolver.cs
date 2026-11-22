using System.Diagnostics;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>
/// Resolves a server command the way Windows will run it, including PATH and
/// PATHEXT, and wraps .cmd/.bat in cmd.exe because CreateProcess cannot launch
/// those directly with redirected stdio.
/// </summary>
public static class McpCommandResolver
{
    public static string? Resolve(
        string command,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        string value = command.Trim().Trim('"');
        if (value.Length == 0) return null;

        if (Path.IsPathRooted(value) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(value) ? Path.GetFullPath(value) : null;
        }

        string path = environment is not null &&
                      environment.TryGetValue("PATH", out string? customPath)
            ? customPath
            : Environment.GetEnvironmentVariable("PATH") ?? "";

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (string folder in path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(folder, value + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            string exact = Path.Combine(folder, value);
            if (File.Exists(exact)) return Path.GetFullPath(exact);
        }

        return null;
    }

    public static ProcessStartInfo CreateStartInfo(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        string? resolved = Resolve(command, environment);
        string executable = resolved ?? command;
        string extension = Path.GetExtension(executable).ToLowerInvariant();

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows() && extension is ".cmd" or ".bat")
        {
            // cmd /s /c needs one quoted command string, with an extra outer
            // pair of quotes, or a path containing spaces is split at the first
            // space. ArgumentList cannot express that shape reliably.
            string commandLine = QuoteForCommand(executable);
            foreach (string argument in args)
                commandLine += " " + QuoteForCommand(argument);

            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{commandLine}\"";
        }
        else if (OperatingSystem.IsWindows() && extension == ".ps1")
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(executable);
            foreach (string argument in args) startInfo.ArgumentList.Add(argument);
        }
        else
        {
            startInfo.FileName = executable;
            foreach (string argument in args) startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in environment)
            startInfo.Environment[key] = value;

        return startInfo;
    }

    private static string QuoteForCommand(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";
}
