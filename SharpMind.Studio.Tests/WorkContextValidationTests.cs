using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Context locations are permission boundaries, so the editor must reject an
/// unusable path and call out ambiguity before the file tools see it.
/// </summary>
public sealed class WorkContextValidationTests
{
    [Fact]
    public void Validate_AcceptsDistinctExistingFolders()
    {
        using var temp = new TempDirectory();
        string project = temp.Combine("project");
        string notes = temp.Combine("notes");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(notes);

        string? error = WorkContextValidation.Validate(
            [Context("."), Context(notes)], project, out IReadOnlyList<string> warnings);

        Assert.Null(error);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_RejectsTwoContextsThatResolveToTheSameFolder()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);

        string? error = WorkContextValidation.Validate(
            [Context("."), Context(temp.Path)], temp.Path, out _);

        Assert.NotNull(error);
        Assert.Contains("same folder", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_WarnsAboutMissingAndOverlappingFolders()
    {
        using var temp = new TempDirectory();
        string project = temp.Combine("project");
        string nested = Path.Combine(project, "src");
        Directory.CreateDirectory(project);

        string? error = WorkContextValidation.Validate(
            [Context("."), Context("src"), Context("missing")],
            project,
            out IReadOnlyList<string> warnings);

        Assert.Null(error);
        Assert.Contains(warnings, warning => warning.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_UsesTheProjectRootForRelativePaths()
    {
        using var temp = new TempDirectory();

        string resolved = WorkContextValidation.Resolve(Context("src"), temp.Path);

        Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, "src")), resolved);
    }

    private static WorkContext Context(string path) => new() { Id = path, Path = path };
}
