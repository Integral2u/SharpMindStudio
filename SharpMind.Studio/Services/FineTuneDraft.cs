namespace SharpMind.Studio.Services;

/// <summary>
/// The Improve form's values. The pretrain editor keeps its draft on
/// <see cref="StudioTrainingService.Draft"/>; a fine-tune builds its job from
/// separate fields, so those are kept here and restored after navigation.
/// </summary>
public sealed class FineTuneDraft
{
    public string BaseModelPath { get; set; } = "";
    public string CorpusPath { get; set; } = "";
    public bool Sft { get; set; }
    public string SftFormatter { get; set; } = "auto";
    public int SftSeqLen { get; set; } = 512;
    public string Name { get; set; } = "";
    public int Steps { get; set; } = 100;
    public double ValidationPercent { get; set; } = 10;
    public double TestPercent { get; set; } = 10;
    public int Rank { get; set; } = 8;
    public float Alpha { get; set; } = 16f;
    public float LearningRate { get; set; } = 2e-4f;
    public string Export { get; set; } = "";
    public string Output { get; set; } = "merged";
    public string AdapterPath { get; set; } = "";
    public string Resume { get; set; } = "";
    public bool StartFresh { get; set; } = true;
    public List<string> Targets { get; init; } = [];
    public string? PlanId { get; set; }
    public string? RuleSetVersion { get; set; }
}
