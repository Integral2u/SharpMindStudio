namespace SharpMind.Studio.Core.Training;

/// <summary>
/// One logged training step, kept for the live curve. <see cref="ValLoss"/> is
/// null on steps where the validation set was not evaluated (validation off, or
/// the split produced no held-out documents).
/// </summary>
public readonly record struct TrainingCurvePoint(
    int Step,
    double Loss,
    double LearningRate,
    double? ValLoss);
