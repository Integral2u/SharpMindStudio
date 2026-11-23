using SharpMind.Studio.Core.Mcp;

namespace SharpMind.Studio.Core.Work;

/// <summary>The resolved executable, its allowlist entry and any fixed prefix.</summary>
public sealed record AllowedCommandMatch(
    AllowedCommand Command,
    string Executable,
    IReadOnlyList<string> PrefixArguments);

/// <summary>
/// Resolves a command against the project's allowlist. Names are resolved
/// through PATH/PATHEXT and compared by full path, so a different executable
/// that happens to share a name is refused.
/// </summary>
public sealed class CommandPolicy
{
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "bash", "bash.exe", "sh", "sh.exe", "wsl", "wsl.exe",
        "cscript", "cscript.exe", "wscript", "wscript.exe",
        "mshta", "mshta.exe", "rundll32", "rundll32.exe",
        "regsvr32", "regsvr32.exe",
    };

    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python.exe", "python3", "python3.exe", "py", "py.exe",
        "node", "node.exe", "ruby", "ruby.exe", "perl", "perl.exe",
        "php", "php.exe", "lua", "lua.exe",
    };

    private static readonly HashSet<string> ShellShims = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".ps1",
    };

    private readonly IReadOnlyList<AllowedCommand> _commands;
    private readonly string _baseDirectory;

    public CommandPolicy(
        IReadOnlyList<AllowedCommand> commands,
        string? baseDirectory = null)
    {
        _commands = commands ?? [];
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(baseDirectory);
    }

    public bool IsEmpty => _commands.Count == 0;

    public IReadOnlyList<AllowedCommand> Commands => _commands;

    public string AllowedNames => string.Join(
        ", ",
        _commands.Select(command => command.Name));

    public AllowedCommandMatch? Resolve(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        string requested = executable.Trim();

        // A configured name is an alias for its executable. This is how a
        // friendly wrapper such as "helper" maps to dotnet + a fixed prefix.
        AllowedCommand? alias = _commands.FirstOrDefault(command =>
            command.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
        {
            string aliasPath = ResolvePath(alias.Executable);
            if (aliasPath.Length == 0) return null;
            if (IsShellShim(aliasPath)) return null;
            string aliasFile = Path.GetFileName(aliasPath);
            if (Shells.Contains(aliasFile)) return null;
            if (Interpreters.Contains(aliasFile) && !alias.AllowInterpreter) return null;
            return new AllowedCommandMatch(alias, aliasPath, alias.ArgumentsPrefix ?? []);
        }

        string requestedPath = ResolvePath(requested);
        if (requestedPath.Length == 0) return null;
        if (IsShellShim(requestedPath)) return null;

        string fileName = Path.GetFileName(requestedPath);
        if (Shells.Contains(fileName)) return null;

        foreach (AllowedCommand command in _commands)
        {
            string entryPath = ResolvePath(command.Executable);
            if (entryPath.Length == 0) continue;
            if (!entryPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Interpreters.Contains(fileName) && !command.AllowInterpreter)
                return null;

            return new AllowedCommandMatch(
                command,
                entryPath,
                command.ArgumentsPrefix ?? []);
        }

        return null;
    }

    public bool Contains(string executable) => Resolve(executable) is not null;

    /// <summary>True when the allowlist names this command but it cannot be resolved
    /// to a runnable native executable. A shell or shim is blocked rather than
    /// unresolved, so it keeps the generic not-allowed message.</summary>
    public bool HasUnresolvedEntry(string nameOrExecutable)
    {
        foreach (AllowedCommand command in _commands)
        {
            if (!command.Name.Equals(nameOrExecutable, StringComparison.OrdinalIgnoreCase) &&
                !command.Executable.Equals(nameOrExecutable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string executable = Path.GetFileName(command.Executable);
            if (Shells.Contains(executable) || ShellShims.Contains(Path.GetExtension(executable)))
                continue;
            if (Interpreters.Contains(executable) && !command.AllowInterpreter)
                continue;

            if (ResolvePath(command.Executable).Length == 0) return true;
        }

        return false;
    }

    private static bool IsShellShim(string path) =>
        ShellShims.Contains(Path.GetExtension(path));

    private string ResolvePath(string executable)
    {
        string value = executable.Trim().Trim('"');
        if (value.Length == 0) return "";

        if (Path.IsPathRooted(value) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            try
            {
                string full = Path.GetFullPath(value, _baseDirectory);
                return File.Exists(full) ? full : "";
            }
            catch
            {
                return "";
            }
        }

        return McpCommandResolver.Resolve(value) ?? "";
    }
}
