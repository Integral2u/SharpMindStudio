namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Merges the shared skill repository with a project's own skills. Project
/// skills are stored with the project; a project skill whose name matches a
/// repository skill replaces that skill for this project instead of duplicating
/// the guidance in the prompt.
/// </summary>
public static class WorkSkillResolver
{
    public static IReadOnlyList<WorkSkill> Resolve(
        IEnumerable<WorkSkill> systemSkills,
        Func<WorkSkill, bool> isSystemEnabled,
        IEnumerable<WorkSkill> projectSkills,
        Func<WorkSkill, bool> isProjectEnabled)
    {
        ArgumentNullException.ThrowIfNull(systemSkills);
        ArgumentNullException.ThrowIfNull(isSystemEnabled);
        ArgumentNullException.ThrowIfNull(projectSkills);
        ArgumentNullException.ThrowIfNull(isProjectEnabled);

        var projects = projectSkills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name) && isProjectEnabled(skill))
            .GroupBy(skill => skill.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

        var overridden = new HashSet<string>(
            projects.Select(skill => skill.Name.Trim()), StringComparer.OrdinalIgnoreCase);

        var result = new List<WorkSkill>();
        foreach (WorkSkill skill in systemSkills)
        {
            if (string.IsNullOrWhiteSpace(skill.Name) || !isSystemEnabled(skill)) continue;
            if (overridden.Contains(skill.Name.Trim())) continue;
            result.Add(skill);
        }

        result.AddRange(projects);
        return result;
    }
}
