namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Metrics that stay comparable when the tokenizer changes. Raw loss is nats
/// per token, so growing the vocabulary changes its scale; bits per byte
/// normalises by the UTF-8 bytes the model actually predicts.
/// </summary>
public static class ExamMetrics
{
    public static double BitsPerByte(double lossNatsPerToken, long tokens, long bytes)
    {
        if (!double.IsFinite(lossNatsPerToken) || lossNatsPerToken < 0 || tokens <= 0 || bytes <= 0)
            return double.NaN;

        return lossNatsPerToken * tokens / (Math.Log(2) * bytes);
    }
}
