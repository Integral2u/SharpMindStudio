namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Whether a command layer enforces the allowlist. A null layer mode inherits
/// the mode from the layer above; System is never null.
/// </summary>
public enum CommandAccessMode
{
    Restricted,
    Unrestricted,
}

/// <summary>
/// One command-access layer. Commands are additive down the chain; a layer may
/// also disable commands it inherited. The nearest definition of a command wins.
/// </summary>
public sealed record CommandAccessLayer
{
    /// <summary>Null inherits the parent mode.</summary>
    public CommandAccessMode? Mode { get; init; }

    public List<AllowedCommand> Commands { get; init; } = [];

    /// <summary>Names removed from this layer and everything below it.</summary>
    public List<string> DisabledCommands { get; init; } = [];

    public bool IsEmpty =>
        Mode is null &&
        (Commands is null || Commands.Count == 0) &&
        (DisabledCommands is null || DisabledCommands.Count == 0);

    public CommandAccessLayer Normalize() => this with
    {
        Commands = Commands is null ? [] : [.. Commands],
        DisabledCommands = DisabledCommands is null ? [] : [.. DisabledCommands],
    };
}

/// <summary>The resolved command mode and allowlist for one session.</summary>
public sealed record EffectiveCommandAccess(
    CommandAccessMode Mode,
    IReadOnlyList<AllowedCommand> Commands,
    IReadOnlySet<string> DisabledCommands)
{
    public bool IsUnrestricted => Mode == CommandAccessMode.Unrestricted;

    public AllowedCommand? Find(string name) =>
        Commands.FirstOrDefault(command =>
            command.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Combines System, Project, Family, Model and Session command layers.
/// Parent commands flow down, lower layers may add local commands and disable
/// inherited ones, and a disabled ancestor cannot be re-enabled below it.
/// The nearest definition wins when the same name appears twice.
/// </summary>
public static class CommandAccessResolver
{
    public static EffectiveCommandAccess Resolve(
        CommandAccessLayer? system,
        CommandAccessLayer? project = null,
        CommandAccessLayer? family = null,
        CommandAccessLayer? model = null,
        CommandAccessLayer? session = null)
    {
        CommandAccessMode mode =
            session?.Mode ??
            model?.Mode ??
            family?.Mode ??
            project?.Mode ??
            system?.Mode ??
            CommandAccessMode.Restricted;

        var commands = new Dictionary<string, AllowedCommand>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CommandAccessLayer? rawLayer in
                 new[] { system, project, family, model, session })
        {
            if (rawLayer is null) continue;
            CommandAccessLayer layer = rawLayer.Normalize();

            foreach (string name in layer.DisabledCommands)
            {
                string normalized = name.Trim();
                if (normalized.Length == 0) continue;
                disabled.Add(normalized);
                commands.Remove(normalized);
            }

            foreach (AllowedCommand command in layer.Commands)
            {
                string name = string.IsNullOrWhiteSpace(command.Name)
                    ? Path.GetFileNameWithoutExtension(command.Executable)
                    : command.Name.Trim();
                if (name.Length == 0) name = command.Executable.Trim();
                if (name.Length == 0 || disabled.Contains(name)) continue;

                commands[name] = command with
                {
                    Name = name,
                    Executable = command.Executable.Trim(),
                    ArgumentsPrefix = [.. (command.ArgumentsPrefix ?? [])],
                };
            }
        }

        return new EffectiveCommandAccess(
            mode,
            [.. commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)],
            disabled);
    }
}
