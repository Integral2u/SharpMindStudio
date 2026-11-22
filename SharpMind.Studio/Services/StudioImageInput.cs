using SkiaSharp;

namespace SharpMind.Studio.Services;

/// <summary>
/// Decodes an attached image into the normalized CHW tensor the native LFM3-VL vision
/// tower expects. The long side is capped and both dimensions are rounded to the
/// patch/merge grid (32 pixels), which keeps the tower's quadratic attention bounded.
/// </summary>
public static class StudioImageInput
{
    public static float[] Decode(byte[] content, int maxSide, out int height, out int width)
    {
        ArgumentNullException.ThrowIfNull(content);

        using SKData data = SKData.CreateCopy(content);
        using SKBitmap? decoded = SKBitmap.Decode(data);
        if (decoded is null)
            throw new InvalidOperationException("Could not decode the image.");

        double scale = Math.Min(1.0, (double)maxSide / Math.Max(decoded.Width, decoded.Height));
        int resizedWidth = Math.Max(32, (int)Math.Round(decoded.Width * scale / 32.0) * 32);
        int resizedHeight = Math.Max(32, (int)Math.Round(decoded.Height * scale / 32.0) * 32);

        using SKBitmap? resized = decoded.Resize(
            new SKImageInfo(
                resizedWidth,
                resizedHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null)
            throw new InvalidOperationException("Could not resize the image.");

        SKColor[] pixels = resized.Pixels;
        var chw = new float[3 * resizedHeight * resizedWidth];
        int plane = resizedHeight * resizedWidth;
        for (int y = 0; y < resizedHeight; y++)
        {
            for (int x = 0; x < resizedWidth; x++)
            {
                SKColor pixel = pixels[y * resizedWidth + x];
                chw[y * resizedWidth + x] = (pixel.Red / 255f - 0.5f) / 0.5f;
                chw[plane + y * resizedWidth + x] = (pixel.Green / 255f - 0.5f) / 0.5f;
                chw[2 * plane + y * resizedWidth + x] = (pixel.Blue / 255f - 0.5f) / 0.5f;
            }
        }

        height = resizedHeight;
        width = resizedWidth;
        return chw;
    }
}
