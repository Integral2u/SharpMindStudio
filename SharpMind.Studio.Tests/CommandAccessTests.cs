using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class CommandAccessTests
{
    private static AllowedCommand Command(string name, string executable = "dotnet", params string[] prefix) =>
        new()
        {
            Name = name,
            Executable = executable,
            ArgumentsPrefix = [.. prefix],
        };

    [Fact]
    public void Resolve_InheritsCommandsDownTheChainAndLetsTheNearestDefinitionWin()
    {
        var system = new CommandAccessLayer
        {
            Mode = CommandAccessMode.Restricted,
            Commands = [Command("git"), Command("dotnet", "dotnet", "--info")],
        };
        var project = new CommandAccessLayer
        {
            Commands = [Command("dotnet", "dotnet", "--project")],
        };
        var session = new CommandAccessLayer
        {
            Commands = [Command("rg", "rg")],
        };

        EffectiveCommandAccess effective = CommandAccessResolver.Resolve(
            system, project, session: session);

        Assert.Equal(CommandAccessMode.Restricted, effective.Mode);
        Assert.Equal(3, effective.Commands.Count);
        Assert.Equal(
            ["--project"],
            effective.Find("dotnet")!.ArgumentsPrefix);
        Assert.NotNull(effective.Find("git"));
        Assert.NotNull(effective.Find("rg"));
    }

    [Fact]
    public void Resolve_LowerLayerMayWidenOrNarrowTheMode()
    {
        var system = new CommandAccessLayer { Mode = CommandAccessMode.Restricted };

        Assert.Equal(
            CommandAccessMode.Unrestricted,
            CommandAccessResolver.Resolve(
                system,
                session: new CommandAccessLayer { Mode = CommandAccessMode.Unrestricted }).Mode);

        Assert.Equal(
            CommandAccessMode.Restricted,
            CommandAccessResolver.Resolve(
                new CommandAccessLayer { Mode = CommandAccessMode.Unrestricted },
                model: new CommandAccessLayer { Mode = CommandAccessMode.Restricted }).Mode);
    }

    [Fact]
    public void Resolve_AnAncestorDisabledCommandCannotBeReEnabledBelowIt()
    {
        var system = new CommandAccessLayer
        {
            Mode = CommandAccessMode.Restricted,
            Commands = [Command("git")],
        };
        var family = new CommandAccessLayer
        {
            DisabledCommands = ["git"],
        };
        var session = new CommandAccessLayer
        {
            Commands = [Command("git")],
        };

        EffectiveCommandAccess effective = CommandAccessResolver.Resolve(
            system, family: family, session: session);

        Assert.Null(effective.Find("git"));
        Assert.Contains("git", effective.DisabledCommands);
    }

    [Fact]
    public void Resolve_AChildDisablesAnInheritedCommandWithoutRemovingItAbove()
    {
        var system = new CommandAccessLayer
        {
            Mode = CommandAccessMode.Restricted,
            Commands = [Command("git"), Command("rg", "rg")],
        };
        var project = new CommandAccessLayer { DisabledCommands = ["git"] };

        EffectiveCommandAccess effective = CommandAccessResolver.Resolve(system, project);

        Assert.Null(effective.Find("git"));
        Assert.NotNull(effective.Find("rg"));
    }
}
