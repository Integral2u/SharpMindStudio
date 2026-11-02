namespace SharpMind.Studio.Core.Training;

/// <summary>
/// The shared starting point for a new pretraining job: a small GPT-2-style
/// model, BPE tokenizer, random-window batching, AdamW and validation/exam
/// splits. Playbooks and the New-model editor both start here.
/// </summary>
public static class TrainingJobDefaults
{
    public static TrainingJobSettings Create()
    {
        var job = new TrainingJobSettings
        {
            Name = "",
            TokenizerKind = "Bpe",
            TokenizerVocabSize = 2048,
            TotalSteps = 200,
            LogInterval = 10,
            BatchSize = 4,
            SeqLen = 64,
            BatchingKind = "RandomWindow",
            Optimizer = "AdamW",
            LearningRate = 3e-4f,
            MinLr = 3e-5f,
            WarmupSteps = 10,
            WeightDecay = 0.01f,
            GradClipNorm = 1f,
            LabelSmoothing = 0f,
            CheckpointInterval = 50,
            KeepRecent = 2,
            ValidationPercent = 10,
            TestPercent = 10,
            StartFresh = true,
            NormEps = 1e-5f,
        };

        TrainingModelOptions.Apply(job, TrainingModelOptions.Presets[0]);
        return job;
    }
}
