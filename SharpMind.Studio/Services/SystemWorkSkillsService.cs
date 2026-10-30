using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Services;

/// <summary>System-wide Work skills inherited by every project.</summary>
public sealed class SystemWorkSkillsService
{
    private readonly WorkSkillRepository _repository;
    private IReadOnlyList<WorkSkill> _skills = [];

    public SystemWorkSkillsService()
    {
        StudioPaths.EnsureCreated();
        _repository = new WorkSkillRepository(StudioPaths.Skills, StudioPaths.WorkSkillsFile);
        Reload();
    }

    public event Action? Changed;

    public string Folder => _repository.Folder;
    public IReadOnlyList<WorkSkill> Skills => _skills;

    public void Reload()
    {
        try
        {
            _skills = _repository.List();
        }
        catch
        {
            _skills = [];
        }
    }

    public IReadOnlyList<WorkSkill> Save(IEnumerable<WorkSkill> skills)
    {
        _skills = _repository.ReplaceAll(skills);
        Changed?.Invoke();
        return _skills;
    }

    public void Delete(string id)
    {
        _repository.Delete(id);
        Reload();
        Changed?.Invoke();
    }
}


