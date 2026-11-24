using System.Text;
using SharpMind.Studio.Core.Vault;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Executes the parsed shell subset. Pipes are buffered between stages; every
/// stage is an explicit program start, redirections go through the file gate,
/// and the whole command is bounded by one timeout. It is a proxy, not a host
/// shell or a sandbox.
/// </summary>
public sealed class SoftShellRunner(
    WorkPathPolicy paths,
    CommandPolicy policy,
    Func<WorkToolAccess, Task<bool>> permission,
    ISecretResolver? secrets = null,
    WorkShellDirectory? directory = null,
    Func<string, bool>? sessionAllowed = null)
{
    private const int MaxOutput = 20_000;
    private const int MaxStdin = 2 * 1024 * 1024;

    private static readonly string[] SafeEnvironmentNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "COMSPEC",
        "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_CLI_HOME",
        "NUGET_PACKAGES", "JAVA_HOME", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
    ];

    private readonly ISecretResolver _secrets = secrets ?? NullSecretResolver.Instance;
    private readonly WorkShellDirectory _directory = directory
        ?? new WorkShellDirectory(new WorkShellDirectoryStore(), null, paths);
    private readonly Func<string, bool>? _sessionAllowed = sessionAllowed;

    public async Task<string> RunAsync(
        string command,
        string workingDirectory = ".",
        int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = _directory.Gate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(
                command, workingDirectory, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> RunCoreAsync(
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ShellScript script;
        try
        {
            script = ShellParser.Parse(command);
        }
        catch (ShellParseException ex)
        {
            return $"Shell syntax error: {ex.Message}";
        }

        string cwd;
        try
        {
            string baseDirectory = _directory.Current;
            string target = string.IsNullOrWhiteSpace(workingDirectory) || workingDirectory == "."
                ? baseDirectory
                : workingDirectory;
            cwd = paths.ResolveDirectory(target, baseDirectory);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        var state = new RunState(cwd);
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)));

        try
        {
            foreach (ShellList list in script.Lists)
            {
                timeout.Token.ThrowIfCancellationRequested();

                if (list.Operator == ShellListOperator.And && state.LastExit != 0) continue;
                if (list.Operator == ShellListOperator.Or && state.LastExit == 0) continue;

                PipelineResult result = await RunPipelineAsync(
                    list.Pipeline, state, timeout.Token).ConfigureAwait(false);

                state.LastExit = result.ExitCode;
                state.Stdout.Append(result.Stdout);
                state.Stderr.Append(result.Stderr);
                if (result.Canceled)
                {
                    state.LastExit = -1;
                    return Format(
                        state,
                        "Timed out; the process tree was killed.\n" + state.Stdout,
                        "");
                }

                if (result.Error is not null)
                    return SecretTemplate.Redact(result.Error, state.SecretValues);
            }
        }
        catch (OperationCanceledException)
        {
            state.LastExit = -1;
            return Format(
                state,
                "Timed out; the process tree was killed.\n" + state.Stdout,
                "");
        }

        return Format(state, state.Stdout.ToString(), state.Stderr.ToString());
    }

    private async Task<PipelineResult> RunPipelineAsync(
        ShellPipeline pipeline,
        RunState state,
        CancellationToken cancellationToken)
    {
        string input = "";
        string stdout = "";
        var stderr = new StringBuilder();
        int exitCode = 0;

        for (int index = 0; index < pipeline.Commands.Count; index++)
        {
            ShellCommand command = pipeline.Commands[index];
            bool last = index == pipeline.Commands.Count - 1;
            string executable = ShellWordRenderer.RenderVariables(
                command.Words[0], state.Environment);

            if (executable is "cd" or "pwd" or "echo")
            {
                if (!await AllowedAsync("RunShell", WorkToolCategory.Execute, state.Cwd, command.Source)
                        .ConfigureAwait(false))
                {
                    return PipelineResult.Failed(
                        $"Permission denied for '{executable}'. Set RunShell to Ask or Always "
                        + "under Enabled tools. If it is already Ask, set the context's Run "
                        + "permission to Ask or Always.");
                }

                if (executable == "cd")
                {
                    if (pipeline.Commands.Count > 1)
                        return PipelineResult.Failed("'cd' cannot be part of a pipeline.");
                    if (command.Redirects.Count > 0)
                        return PipelineResult.Failed("'cd' does not take redirections.");
                    if (command.Words.Count > 2)
                        return PipelineResult.Failed("'cd' takes at most one directory.");

                    string target = command.Words.Count > 1
                        ? RenderWord(command.Words[1], state, includeSecrets: false).Text
                        : ".";
                    try
                    {
                        state.Cwd = paths.ResolveDirectory(target, state.Cwd);
                        _directory.Set(state.Cwd);
                    }
                    catch (Exception ex)
                    {
                        return PipelineResult.Failed(ex.Message);
                    }

                    exitCode = 0;
                    state.LastExit = 0;
                    state.Environment["PWD"] = state.Cwd;
                    continue;
                }

                var builtin = new StringBuilder();
                if (executable == "pwd")
                {
                    builtin.Append(state.Cwd).Append('\n');
                }
                else
                {
                    for (int word = 1; word < command.Words.Count; word++)
                    {
                        if (word > 1) builtin.Append(' ');
                        builtin.Append(RenderWord(
                            command.Words[word], state, includeSecrets: true).Text);
                    }
                    if (builtin.Length > 0) builtin.Append('\n');
                }

                var builtinResult = new PipelineResult(
                    0,
                    builtin.ToString(),
                    "",
                    false,
                    null);
                builtinResult = await ApplyRedirectsAsync(
                    command, builtinResult, state, last).ConfigureAwait(false);
                if (builtinResult.Error is not null) return builtinResult;

                stdout = builtinResult.Stdout;
                stderr.Append(builtinResult.Stderr);
                exitCode = builtinResult.ExitCode;
                if (!last) input = builtinResult.Stdout;
                state.LastExit = exitCode;
                continue;
            }

            if (executable is "mkdir" or "rmdir" or "rename")
            {
                if (pipeline.Commands.Count > 1)
                    return PipelineResult.Failed($"'{executable}' cannot be part of a pipeline.");
                if (command.Redirects.Count > 0)
                    return PipelineResult.Failed($"'{executable}' does not take redirections.");

                string? error = await RunDirectoryBuiltinAsync(executable, command, state)
                    .ConfigureAwait(false);
                if (error is not null) return PipelineResult.Failed(error);

                exitCode = 0;
                state.LastExit = 0;
                continue;
            }

            if (command.Words.Count == 0)
                return PipelineResult.Failed("A command is required.");

            var arguments = new List<string>();
            for (int word = 1; word < command.Words.Count; word++)
            {
                arguments.Add(ShellWordRenderer.RenderVariables(
                    command.Words[word], state.Environment));
            }

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, ShellWord value) in command.Assignments)
            {
                environment[key] = ShellWordRenderer.RenderVariables(
                    value, state.Environment);
            }

            string? stdin = input;
            ShellRedirect? stdinRedirect = command.Redirects.FirstOrDefault(
                redirect => redirect.Kind == ShellRedirectKind.StandardInput);
            if (stdinRedirect?.Target is not null)
            {
                SecretExpansion expansion = RenderWord(
                    stdinRedirect.Target, state, includeSecrets: true);
                if (expansion.Missing.Count > 0)
                    return PipelineResult.Failed(
                        new SecretMissingException(expansion.Missing).Message);

                string path;
                try
                {
                    path = paths.ResolveFile(expansion.Text, state.Cwd);
                }
                catch (Exception ex)
                {
                    return PipelineResult.Failed(ex.Message);
                }

                if (!await AllowedAsync("RunShell", WorkToolCategory.Read, path)
                        .ConfigureAwait(false))
                {
                    return PipelineResult.Failed($"Permission denied reading '{expansion.Text}'.");
                }

                try
                {
                    stdin = await File.ReadAllTextAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    if (stdin.Length > MaxStdin) stdin = stdin[..MaxStdin];
                }
                catch (Exception ex)
                {
                    return PipelineResult.Failed($"Could not read '{expansion.Text}': {ex.Message}");
                }
            }

            var runner = new WorkCommandRunner(
                policy, paths, permission, _secrets, sessionAllowed: _sessionAllowed);
            WorkCommandResult result = await runner.RunAsync(
                "RunShell",
                executable,
                arguments,
                state.Cwd,
                environment,
                stdin,
                600,
                cancellationToken).ConfigureAwait(false);

            if (result.Error is not null) return PipelineResult.Failed(result.Error);
            if (result.TimedOut)
            {
                state.SecretValues.UnionWith(result.SecretValues);
                return new PipelineResult(-1, result.Stdout, result.Stderr, true, null);
            }

            state.SecretValues.UnionWith(result.SecretValues);
            var commandResult = new PipelineResult(
                result.ExitCode,
                result.Stdout,
                result.Stderr,
                false,
                null);
            commandResult = await ApplyRedirectsAsync(
                command, commandResult, state, last).ConfigureAwait(false);
            if (commandResult.Error is not null) return commandResult;

            stdout = commandResult.Stdout;
            stderr.Append(commandResult.Stderr);
            exitCode = commandResult.ExitCode;
            if (!last) input = commandResult.Stdout;
            state.LastExit = exitCode;
        }

        return new PipelineResult(exitCode, stdout, stderr.ToString(), false, null);
    }

    private async Task<string?> RunDirectoryBuiltinAsync(
        string commandName,
        ShellCommand command,
        RunState state)
    {
        List<string> arguments = [];
        foreach (ShellWord word in command.Words.Skip(1))
        {
            SecretExpansion expansion = RenderWord(word, state, includeSecrets: true);
            if (expansion.Missing.Count > 0)
                return new SecretMissingException(expansion.Missing).Message;
            arguments.Add(expansion.Text);
        }

        if (arguments.Count == 0)
            return $"'{commandName}' needs a path.";

        try
        {
            return commandName switch
            {
                "mkdir" => await MakeDirectoryAsync(arguments, command, state).ConfigureAwait(false),
                "rmdir" => await RemoveDirectoryAsync(arguments, command, state).ConfigureAwait(false),
                "rename" => await RenameDirectoryAsync(arguments, command, state).ConfigureAwait(false),
                _ => $"Unknown directory command '{commandName}'.",
            };
        }
        catch (Exception ex)
        {
            return $"{commandName}: {ex.Message}";
        }
    }

    private async Task<string?> MakeDirectoryAsync(
        List<string> arguments,
        ShellCommand command,
        RunState state)
    {
        bool parents = arguments[0] == "-p";
        if (parents) arguments.RemoveAt(0);
        if (arguments.Count == 0) return "'mkdir' needs a path.";

        foreach (string argument in arguments)
        {
            string path = paths.ResolveFile(argument, state.Cwd);
            if (!await AllowedAsync("RunShell", WorkToolCategory.Write, path, command.Source)
                    .ConfigureAwait(false))
            {
                return $"Permission denied writing '{argument}'. Set the context's Write permission "
                    + "to Ask or Always, and keep RunShell on Ask or Always.";
            }

            if (!parents)
            {
                string? parent = Path.GetDirectoryName(path);
                if (parent is not null && !Directory.Exists(parent))
                    return $"'mkdir' could not find the parent directory of '{argument}'. Use -p to create it.";
                if (Directory.Exists(path) || File.Exists(path))
                    return $"'{argument}' already exists.";
            }

            Directory.CreateDirectory(path);
        }

        return null;
    }

    private async Task<string?> RemoveDirectoryAsync(
        List<string> arguments,
        ShellCommand command,
        RunState state)
    {
        if (arguments.Count != 1) return "'rmdir' takes one directory.";

        string path = paths.ResolveDirectory(arguments[0], state.Cwd);
        if (!await AllowedAsync("RunShell", WorkToolCategory.Delete, path, command.Source)
                .ConfigureAwait(false))
        {
            return $"Permission denied deleting '{arguments[0]}'. Set the context's Delete permission "
                + "to Ask or Always, and keep RunShell on Ask or Always.";
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
            return $"'rmdir' can only remove an empty directory: '{arguments[0]}'.";

        Directory.Delete(path);
        return null;
    }

    private async Task<string?> RenameDirectoryAsync(
        List<string> arguments,
        ShellCommand command,
        RunState state)
    {
        if (arguments.Count != 2) return "'rename' takes a source and a destination.";

        string source = paths.ResolveDirectory(arguments[0], state.Cwd);
        string destination = paths.ResolveFile(arguments[1], state.Cwd);

        if (!await AllowedAsync("RunShell", WorkToolCategory.Delete, source, command.Source)
                .ConfigureAwait(false))
        {
            return $"Permission denied renaming '{arguments[0]}'. Set the context's Delete permission "
                + "to Ask or Always, and keep RunShell on Ask or Always.";
        }

        if (!await AllowedAsync("RunShell", WorkToolCategory.Write, destination, command.Source)
                .ConfigureAwait(false))
        {
            return $"Permission denied writing '{arguments[1]}'. Set the context's Write permission "
                + "to Ask or Always, and keep RunShell on Ask or Always.";
        }

        if (Directory.Exists(destination) || File.Exists(destination))
            return $"'{arguments[1]}' already exists.";

        string? parent = Path.GetDirectoryName(destination);
        if (parent is not null && !Directory.Exists(parent))
            return $"'rename' could not find the parent directory of '{arguments[1]}'.";

        Directory.Move(source, destination);
        return null;
    }

    private async Task<PipelineResult> ApplyRedirectsAsync(
        ShellCommand command,
        PipelineResult result,
        RunState state,
        bool last)
    {
        if (result.Error is not null) return result;

        string stdout = result.Stdout;
        string stderr = result.Stderr;

        ShellRedirect? stderrRedirect = command.Redirects.FirstOrDefault(redirect =>
            redirect.Kind is ShellRedirectKind.StandardError or ShellRedirectKind.StandardErrorAppend);
        ShellRedirect? stdoutRedirect = command.Redirects.FirstOrDefault(redirect =>
            redirect.Kind is ShellRedirectKind.StandardOutput or ShellRedirectKind.StandardOutputAppend);
        bool merge = command.Redirects.Any(redirect =>
            redirect.Kind == ShellRedirectKind.MergeStandardError);

        if (merge)
        {
            stdout += stderr;
            stderr = "";
        }

        if (stderrRedirect?.Target is not null && !merge)
        {
            string? error = await WriteFileAsync(
                stderrRedirect.Target, stderr,
                stderrRedirect.Kind == ShellRedirectKind.StandardErrorAppend,
                state).ConfigureAwait(false);
            if (error is not null) return PipelineResult.Failed(error);
            stderr = "";
        }

        if (stdoutRedirect?.Target is not null)
        {
            string? error = await WriteFileAsync(
                stdoutRedirect.Target, stdout,
                stdoutRedirect.Kind == ShellRedirectKind.StandardOutputAppend,
                state).ConfigureAwait(false);
            if (error is not null) return PipelineResult.Failed(error);
            stdout = "";
        }

        return new PipelineResult(
            result.ExitCode,
            last ? stdout : stdout,
            stderr,
            false,
            null);
    }

    private async Task<string?> WriteFileAsync(
        ShellWord target,
        string content,
        bool append,
        RunState state)
    {
        SecretExpansion expansion = RenderWord(target, state, includeSecrets: true);
        if (expansion.Missing.Count > 0)
            return new SecretMissingException(expansion.Missing).Message;

        string path;
        try
        {
            path = paths.ResolveFile(expansion.Text, state.Cwd);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (!await AllowedAsync("RunShell", WorkToolCategory.Write, path)
                .ConfigureAwait(false))
        {
            return $"Permission denied writing '{expansion.Text}'.";
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
            if (append) await File.AppendAllTextAsync(path, content).ConfigureAwait(false);
            else await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not write '{expansion.Text}': {ex.Message}";
        }
    }

    private SecretExpansion RenderWord(
        ShellWord word,
        RunState state,
        bool includeSecrets)
    {
        var environment = new Dictionary<string, string>(
            state.Environment, StringComparer.OrdinalIgnoreCase);
        SecretExpansion expansion = ShellWordRenderer.Render(
            word,
            environment,
            includeSecrets ? _secrets : NullSecretResolver.Instance);
        if (includeSecrets)
        {
            foreach (SecretUse use in expansion.Used)
                state.SecretValues.Add(use.Value);
        }
        return expansion;
    }

    private Task<bool> AllowedAsync(
        string tool,
        WorkToolCategory category,
        string path,
        string? detail = null) =>
        permission(new WorkToolAccess(tool, category, path) { Detail = detail });

    private static string Format(
        RunState state,
        string stdout,
        string stderr)
    {
        var text = new StringBuilder();
        text.Append($"Exit code {state.LastExit}.\n");
        if (!string.IsNullOrWhiteSpace(stdout))
            text.Append("\nstdout:\n").Append(stdout.TrimEnd()).Append('\n');
        if (!string.IsNullOrWhiteSpace(stderr))
            text.Append("\nstderr:\n").Append(stderr.TrimEnd()).Append('\n');
        if (text.Length > MaxOutput)
            text.Length = MaxOutput;

        return SecretTemplate.Redact(text.ToString().TrimEnd(), state.SecretValues);
    }

    private sealed class RunState(string cwd)
    {
        public string Cwd { get; set; } = cwd;

        private int _lastExit;
        public int LastExit
        {
            get => _lastExit;
            set
            {
                _lastExit = value;
                Environment["LASTEXITCODE"] = value.ToString();
                Environment["?"] = value.ToString();
            }
        }

        public StringBuilder Stdout { get; } = new();
        public StringBuilder Stderr { get; } = new();
        public HashSet<string> SecretValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Environment { get; } = BuildEnvironment(cwd);

        private static Dictionary<string, string> BuildEnvironment(string cwd)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in SafeEnvironmentNames)
            {
                string? value = System.Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) environment[name] = value;
            }

            environment["PROJECT_ROOT"] = cwd;
            environment["PWD"] = cwd;
            environment["HOME"] = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.UserProfile);
            environment["LASTEXITCODE"] = "0";
            environment["?"] = "0";
            return environment;
        }
    }

    private sealed record PipelineResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool Canceled,
        string? Error)
    {
        public static PipelineResult Failed(string error) =>
            new(-1, "", "", false, error);
    }
}
