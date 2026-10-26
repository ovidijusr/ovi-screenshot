using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Ovi.Screenshot.Views;

internal static class BitmapSources
{
    internal static WriteableBitmap FromRgba(int width, int height, byte[] rgba)
    {
        return FromPremultipliedBgra(width, height, ToPremultipliedBgra(rgba));
    }

    internal static byte[] ToPremultipliedBgra(byte[] rgba)
    {
        byte[] bgra = new byte[rgba.Length];
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            int alpha = rgba[offset + 3];
            bgra[offset] = (byte)((rgba[offset + 2] * alpha + 127) / 255);
            bgra[offset + 1] = (byte)((rgba[offset + 1] * alpha + 127) / 255);
            bgra[offset + 2] = (byte)((rgba[offset] * alpha + 127) / 255);
            bgra[offset + 3] = (byte)alpha;
        }
        return bgra;
    }

    internal static WriteableBitmap FromPremultipliedBgra(int width, int height, byte[] bgra)
    {
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("Image buffer dimensions do not match.", nameof(bgra));
        var bitmap = new WriteableBitmap(width, height);
        using Stream pixels = bitmap.PixelBuffer.AsStream();
        pixels.Write(bgra);
        bitmap.Invalidate();
        return bitmap;
    }
}
