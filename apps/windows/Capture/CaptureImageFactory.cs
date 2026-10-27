using Ovi.Screenshot.Interop;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Capture;

internal static class CaptureImageFactory
{
    internal static CaptureResult Create(CapturedDisplay source, PixelSelection region, Win32.Rect workArea, bool windowShadow = false)
    {
        var document = ImportRegion(source, region);
        if (windowShadow)
        {
            try { return Export(document.AddWindowShadow(), workArea); }
            finally { document.Dispose(); }
        }
        return Export(document, workArea);
    }

    private static ImageDocument ImportRegion(CapturedDisplay source, PixelSelection region)
    {
        if (source.PremultipliedBgra is { } bgra)
            return ImageDocument.ImportBgraRegion(source.Width, source.Height, bgra, region.X, region.Y, region.Width, region.Height);

        if (region.X == 0 && region.Y == 0 && region.Width == source.Width && region.Height == source.Height)
            return ImageDocument.Import(ImageDescriptor.Rgba((uint)source.Width, (uint)source.Height, (ulong)source.Width * 4), source.Rgba);

        ValidateRegion(source, region);
        byte[] pixels = new byte[checked(region.Width * region.Height * 4)];
        for (int row = 0; row < region.Height; row++)
            Buffer.BlockCopy(source.Rgba, checked(((region.Y + row) * source.Width + region.X) * 4), pixels,
                checked(row * region.Width * 4), checked(region.Width * 4));
        return ImageDocument.Import(ImageDescriptor.Rgba((uint)region.Width, (uint)region.Height, (ulong)region.Width * 4), pixels);
    }

    private static void ValidateRegion(CapturedDisplay source, PixelSelection region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > source.Width || (long)region.Y + region.Height > source.Height)
            throw new ArgumentOutOfRangeException(nameof(region), "The selected pixels are outside the source image.");
    }

    private static CaptureResult Export(ImageDocument document, Win32.Rect workArea)
    {
        try
        {
            var image = document.Info.Image;
            var preview = document.RenderPreview(600, 360);
            // The immutable document belongs to the encoder until it finishes.
            // Preview pixels are ready now and need not wait for PNG compression.
            Task<byte[]> png = Task.Run(() =>
            {
                using (document) return document.ExportPng();
            });
            return new CaptureResult((int)image.Width, (int)image.Height, png,
                (int)preview.Image.Width, (int)preview.Image.Height, preview.Rgba, workArea);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}
