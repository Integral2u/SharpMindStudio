using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
        using Image<Rgb24> image = Image.Load<Rgb24>(content);
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(image.Width, image.Height));
        int resizedWidth = Math.Max(32, (int)Math.Round(image.Width * scale / 32.0) * 32);
        int resizedHeight = Math.Max(32, (int)Math.Round(image.Height * scale / 32.0) * 32);
        image.Mutate(context => context.Resize(resizedWidth, resizedHeight, KnownResamplers.Bicubic));

        var chw = new float[3 * resizedHeight * resizedWidth];
        int plane = resizedHeight * resizedWidth;
        for (int y = 0; y < resizedHeight; y++)
        {
            for (int x = 0; x < resizedWidth; x++)
            {
                Rgb24 pixel = image[x, y];
                chw[y * resizedWidth + x] = (pixel.R / 255f - 0.5f) / 0.5f;
                chw[plane + y * resizedWidth + x] = (pixel.G / 255f - 0.5f) / 0.5f;
                chw[2 * plane + y * resizedWidth + x] = (pixel.B / 255f - 0.5f) / 0.5f;
            }
        }

        height = resizedHeight;
        width = resizedWidth;
        return chw;
    }
}
