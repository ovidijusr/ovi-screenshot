using Ovi.Screenshot.Interop;

namespace Ovi.Screenshot.Capture;

// Native capture normalizes pixels; portable presentation is owned by Rust.
internal static class WindowImageStyle
{
    internal const int Padding = ImageDocument.WindowShadowPadding;

    internal static void VerifySyntheticShadow()
    {
        byte[] converted = WindowCapture.StraightRgbaFromPremultipliedBgra([20, 40, 60, 128, 99, 90, 80, 0, 0, 0, 255, 255, 255, 0, 0, 255]);
        byte[] expected = [120, 80, 40, 128, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0, 255, 255];
        Require(converted.AsSpan().SequenceEqual(expected), "Window alpha/channel/top-down conversion changed.");
        const int width = 32, height = 20;
        byte[] rgba = new byte[width * height * 4];
        for (int y = 2; y < height - 2; y++)
        for (int x = 2; x < width - 2; x++)
        {
            int index = (y * width + x) * 4;
            rgba[index] = 45; rgba[index + 1] = 120; rgba[index + 2] = 210; rgba[index + 3] = 255;
        }
        rgba[(3 * width + 3) * 4 + 3] = 128;
        using var source = ImageDocument.Import(ImageDescriptor.Rgba(width, height, width * 4), rgba);
        using var styled = source.AddWindowShadow();
        var image = styled.RenderPreview(uint.MaxValue, uint.MaxValue);
        int styledWidth = checked((int)image.Image.Width), styledHeight = checked((int)image.Image.Height);
        Require(styledWidth == width + Padding * 2 && styledHeight == height + Padding * 2, "Window shadow margin changed.");
        int center = ((Padding + 10) * styledWidth + Padding + 16) * 4;
        Require(image.Rgba.AsSpan(center, 4).SequenceEqual(new byte[] { 45, 120, 210, 255 }), "Window shadow altered opaque source content.");
        int semiTransparent = 0;
        for (int y = 0; y < styledHeight; y++)
        for (int x = 0; x < styledWidth; x++)
        {
            int index = (y * styledWidth + x) * 4;
            if (image.Rgba[index + 3] is > 0 and < 255 && (x < Padding || x >= Padding + width || y < Padding || y >= Padding + height))
            {
                semiTransparent++;
                Require(image.Rgba[index] == 0 && image.Rgba[index + 1] == 0 && image.Rgba[index + 2] == 0,
                    "Window shadow contains an opaque background color.");
            }
        }
        Require(semiTransparent > 20 && image.Rgba[3] == 0 && image.Rgba[^1] == 0, "Window shadow lacks transparent corners or feathered alpha.");
        Require(rgba[(3 * width + 3) * 4 + 3] == 128, "Window styling mutated the captured alpha.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
