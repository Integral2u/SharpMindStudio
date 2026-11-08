using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class StudioImageInputTests
{
    [Fact]
    public void Decode_NormalizesAnImageIntoChw()
    {
        using var image = new Image<Rgb24>(64, 64);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                image[x, y] = new Rgb24(255, 0, 0);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        float[] chw = StudioImageInput.Decode(stream.ToArray(), 512, out int height, out int width);

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
        using var image = new Image<Rgb24>(100, 50);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        StudioImageInput.Decode(stream.ToArray(), 512, out int height, out int width);

        Assert.Equal(96, width);
        Assert.Equal(64, height);
    }
}
