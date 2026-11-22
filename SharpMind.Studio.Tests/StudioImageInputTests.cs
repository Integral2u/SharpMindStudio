using SkiaSharp;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class StudioImageInputTests
{
    [Fact]
    public void Decode_NormalizesAnImageIntoChw()
    {
        byte[] png = Encode(SKColors.Red, 64, 64, SKEncodedImageFormat.Png);

        float[] chw = StudioImageInput.Decode(png, 512, out int height, out int width);

        Assert.Equal(64, width);
        Assert.Equal(64, height);
        Assert.Equal(3 * 64 * 64, chw.Length);
        Assert.Equal(1.0f, chw[0], 3);       // red -> +1
        Assert.Equal(-1.0f, chw[64 * 64], 3); // green -> -1
        Assert.Equal(-1.0f, chw[2 * 64 * 64], 3); // blue -> -1
    }

    [Fact]
    public void Decode_RoundsTheResizeToThePatchGrid()
    {
        byte[] png = Encode(SKColors.Black, 100, 50, SKEncodedImageFormat.Png);

        StudioImageInput.Decode(png, 512, out int height, out int width);

        Assert.Equal(96, width);
        Assert.Equal(64, height);
    }

    [Fact]
    public void Decode_ReadsWebP()
    {
        byte[] webp = Encode(SKColors.Red, 32, 32, SKEncodedImageFormat.Webp);

        float[] chw = StudioImageInput.Decode(webp, 512, out int height, out int width);

        Assert.Equal(32, width);
        Assert.Equal(32, height);
        Assert.Equal(1.0f, chw[0], 3);
    }

    private static byte[] Encode(
        SKColor color,
        int width,
        int height,
        SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, 100)
            ?? throw new InvalidOperationException($"SkiaSharp could not encode {format}.");
        return data.ToArray();
    }
}
