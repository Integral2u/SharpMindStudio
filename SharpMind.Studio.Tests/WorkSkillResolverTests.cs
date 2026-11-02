using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// Project skills sit on top of the shared repository: same-name skills replace
/// the repository version, different names add to it, and either side can be
/// disabled without deleting anything.
/// </summary>
public sealed class WorkSkillResolverTests
{
    [Fact]
    public void Resolve_ProjectSkillOverridesARepositorySkillWithTheSameName()
    {
        IReadOnlyList<WorkSkill> result = WorkSkillResolver.Resolve(
            [Skill("Review", "system guidance")],
            _ => true,
            [Skill("Review", "project guidance")],
            _ => true);

        WorkSkill review = Assert.Single(result);
        Assert.Equal("project guidance", review.Content);
    }

    [Fact]
    public void Resolve_AddsProjectSkillsAlongsideRepositorySkills()
    {
        IReadOnlyList<WorkSkill> result = WorkSkillResolver.Resolve(
            [Skill("Review", "system")],
            _ => true,
            [Skill("Tone", "project")],
            _ => true);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, skill => skill.Name == "Review" && skill.Content == "system");
        Assert.Contains(result, skill => skill.Name == "Tone" && skill.Content == "project");
    }

    [Fact]
    public void Resolve_SkipsDisabledSkillsOnEitherSide()
    {
        IReadOnlyList<WorkSkill> result = WorkSkillResolver.Resolve(
            [Skill("Review", "system")],
            skill => skill.Name != "Review",
            [Skill("Tone", "project"), Skill("Style", "off")],
            skill => skill.Name != "Style");

        WorkSkill tone = Assert.Single(result);
        Assert.Equal("Tone", tone.Name);
    }

    [Fact]
    public void Resolve_KeepsAProjectOverrideWhenTheRepositorySkillIsDisabled()
    {
        IReadOnlyList<WorkSkill> result = WorkSkillResolver.Resolve(
            [Skill("Review", "system")],
            _ => false,
            [Skill("Review", "project")],
            _ => true);

        WorkSkill review = Assert.Single(result);
        Assert.Equal("project", review.Content);
    }

    private static WorkSkill Skill(string name, string content) => new()
    {
        Id = name.ToLowerInvariant(),
        Name = name,
        Content = content,
    };
}
