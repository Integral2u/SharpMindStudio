using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed class InferenceSettingsService
{
    private readonly InferenceSettingsStore _store;

    public InferenceSettingsService()
    {
        StudioPaths.EnsureCreated();
        _store = new InferenceSettingsStore(StudioPaths.InferenceFile);
    }

    public event Action? Changed;

    public InferenceOverrides System => _store.Document.System;

    public IReadOnlyDictionary<string, InferenceOverrides> Families
    {
        get
        {
            lock (_store)
                return new Dictionary<string, InferenceOverrides>(
                    _store.Document.Families, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyDictionary<string, InferenceOverrides> Models
    {
        get
        {
            lock (_store)
                return new Dictionary<string, InferenceOverrides>(
                    _store.Document.Models, StringComparer.OrdinalIgnoreCase);
        }
    }

    public InferenceOverrides? FamilyOverrides(string? familyId) =>
        string.IsNullOrWhiteSpace(familyId) ? null : _store.FamilyOverrides(familyId);

    public InferenceOverrides? ModelOverrides(string? modelPath) =>
        string.IsNullOrWhiteSpace(modelPath) ? null : _store.ModelOverrides(modelPath);

    public ResolvedInference Resolve(string? familyId, string? modelPath, InferenceOverrides? session) =>
        InferenceResolver.Resolve(
            System,
            FamilyOverrides(familyId),
            ModelOverrides(modelPath),
            session);

    public void SetSystem(InferenceOverrides overrides)
    {
        _store.SetSystem(overrides);
        Changed?.Invoke();
    }

    public void SetFamily(string familyId, InferenceOverrides overrides)
    {
        _store.SetFamily(familyId, overrides);
        Changed?.Invoke();
    }

    public void DeleteFamily(string familyId)
    {
        _store.DeleteFamily(familyId);
        Changed?.Invoke();
    }

    public void SetModel(string modelPath, InferenceOverrides overrides)
    {
        _store.SetModel(modelPath, overrides);
        Changed?.Invoke();
    }

    public void DeleteModel(string modelPath)
    {
        _store.DeleteModel(modelPath);
        Changed?.Invoke();
    }
}
