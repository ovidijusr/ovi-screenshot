using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Services;

namespace Ovi.Screenshot.Capture;

internal sealed class CapturedDisplay
{
    private byte[]? _rgba;
    private readonly object _pixelsGate = new();
    internal int Width { get; }
    internal int Height { get; }
    internal byte[]? PremultipliedBgra { get; }
    internal byte[] Rgba
    {
        get
        {
            byte[]? existing = Volatile.Read(ref _rgba);
            if (existing is not null) return existing;
            lock (_pixelsGate)
            {
                if (_rgba is not null) return _rgba;
                byte[] bgra = PremultipliedBgra!;
                byte[] rgba = new byte[bgra.Length];
                for (int offset = 0; offset < bgra.Length; offset += 4)
                {
                    rgba[offset] = bgra[offset + 2]; rgba[offset + 1] = bgra[offset + 1];
                    rgba[offset + 2] = bgra[offset]; rgba[offset + 3] = 255;
                }
                Volatile.Write(ref _rgba, rgba);
                return rgba;
            }
        }
    }

    internal CapturedDisplay(int width, int height, byte[] rgba, byte[]? premultipliedBgra = null)
    {
        Width = width; Height = height; _rgba = rgba; PremultipliedBgra = premultipliedBgra;
    }
    private CapturedDisplay(int width, int height, byte[] opaqueBgra, bool fromBgra)
    {
        Width = width; Height = height; PremultipliedBgra = opaqueBgra;
    }
    internal static CapturedDisplay FromOpaqueBgra(int width, int height, byte[] bgra) => new(width, height, bgra, true);
}

internal static class MonitorCapture
{
    // One-shot SDR stills use GDI; ScreenRecorder retains WGC. No capture session/indicator.
    // BitBlt performs no color management; HDR/wide-gamut fidelity is outside this prototype.
    internal static Task<CapturedDisplay> AcquireAsync(nint monitor, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var info = new Win32.MonitorInfo { Size = (uint)Marshal.SizeOf<Win32.MonitorInfo>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The display is unavailable.");
        using var bitmap = NativeCaptureBitmap.Create(info.Monitor.Width, info.Monitor.Height);
        nint desktop = GdiCaptureNative.GetDC(0);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The desktop is unavailable in this Windows session.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GdiCaptureNative.BitBlt(bitmap.Dc, 0, 0, bitmap.Width, bitmap.Height, desktop, info.Monitor.Left, info.Monitor.Top,
                GdiCaptureNative.SrcCopy | GdiCaptureNative.CaptureBlt | GdiCaptureNative.NoMirrorBitmap))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture the selected display.");
            GdiCaptureNative.GdiFlush();
        }
        finally { GdiCaptureNative.ReleaseDC(0, desktop); }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bgra = bitmap.CopyOpaqueBgra();
        CaptureTiming.Write("still.gdi", timer.Elapsed, bitmap.Width, bitmap.Height);
        // The overlay consumes BGRA directly. Convert for Rust only after selection.
        return CapturedDisplay.FromOpaqueBgra(bitmap.Width, bitmap.Height, bgra);
    }, cancellationToken);

    // Memory DIB fixture only: never acquires a desktop DC or reads screen pixels.
    internal static void VerifySyntheticBitmap()
    {
        byte[] source = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
                         10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255];
        using var input = NativeCaptureBitmap.FromBgra(3, 2, source);
        using var output = NativeCaptureBitmap.Create(2, 2);
        if (!GdiCaptureNative.BitBlt(output.Dc, 0, 0, 2, 2, input.Dc, 1, 0, GdiCaptureNative.SrcCopy))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        GdiCaptureNative.GdiFlush();
        byte[] expected = [0, 255, 0, 255, 255, 0, 0, 255, 40, 50, 60, 255, 70, 80, 90, 255];
        byte[] actual = output.CopyOpaqueBgra();
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Native bitmap crop, channels, or top-down row order changed.");
        var captured = CapturedDisplay.FromOpaqueBgra(2, 2, actual);
        byte[] expectedRgba = [0, 255, 0, 255, 0, 0, 255, 255, 60, 50, 40, 255, 90, 80, 70, 255];
        if (!captured.Rgba.AsSpan().SequenceEqual(expectedRgba))
            throw new InvalidOperationException("Deferred RGBA conversion changed source pixels.");
    }
}

internal sealed class NativeCaptureBitmap : IDisposable
{
    private nint _bitmap, _previous;
    internal nint Dc { get; private set; }
    internal nint Pixels { get; private set; }
    internal int Width { get; }
    internal int Height { get; }

    private NativeCaptureBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _ = checked(width * height * 4);
        Width = width; Height = height;
        Dc = GdiCaptureNative.CreateCompatibleDC(0);
        if (Dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var info = new GdiCaptureNative.BitmapInfo { Header = new GdiCaptureNative.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<GdiCaptureNative.BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32
            } };
            _bitmap = GdiCaptureNative.CreateDIBSection(Dc, in info, 0, out nint bits, 0, 0);
            if (_bitmap == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Pixels = bits;
            _previous = GdiCaptureNative.SelectObject(Dc, _bitmap);
            if (_previous == 0 || _previous == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    internal static NativeCaptureBitmap Create(int width, int height) => new(width, height);
    internal static NativeCaptureBitmap FromBgra(int width, int height, byte[] pixels)
    {
        if (pixels.Length != checked(width * height * 4)) throw new ArgumentException("Image buffer dimensions do not match.");
        var bitmap = new NativeCaptureBitmap(width, height);
        try { Marshal.Copy(pixels, 0, bitmap.Pixels, pixels.Length); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }

    internal byte[] CopyOpaqueBgra()
    {
        byte[] pixels = new byte[checked(Width * Height * 4)];
        Marshal.Copy(Pixels, pixels, 0, pixels.Length);
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        return pixels;
    }

    public void Dispose()
    {
        if (Dc == 0) return;
        if (_previous != 0 && _previous != new nint(-1)) GdiCaptureNative.SelectObject(Dc, _previous);
        if (_bitmap != 0) GdiCaptureNative.DeleteObject(_bitmap);
        GdiCaptureNative.DeleteDC(Dc);
        Dc = Pixels = _bitmap = _previous = 0;
    }
}

internal static class GdiCaptureNative
{
    internal const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000, NoMirrorBitmap = 0x80000000;
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] internal static extern bool StretchBlt(nint dc, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
}

internal static class CaptureTiming
{
    internal static void Write(string phase, TimeSpan elapsed, int width, int height) =>
        _ = WriteAsync(AppLog.Shared, phase, elapsed, width, height);

    internal static Task WriteAsync(AppLog log, string phase, TimeSpan elapsed, int width, int height) => Task.Run(() =>
        log.Write($"capture-timing {phase} {elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)}ms {width}x{height}"));
}
