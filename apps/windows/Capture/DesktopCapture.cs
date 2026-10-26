using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Capture;

internal static class DesktopCapture
{
    internal static Task<CapturedDisplay> AcquireAsync(DisplayTopology topology, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(topology);
        cancellationToken.ThrowIfCancellationRequested();
        var layout = ValidateLayout(topology.Bounds);
        EnsureCurrent(topology);
        CapturedDisplay captured;
        if (topology.Monitors.Count == 1)
        {
            captured = await MonitorCapture.AcquireAsync(topology.Monitors[0].Handle, cancellationToken);
        }
        else
        {
            captured = CaptureDesktop(topology, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrent(topology);
        if (captured.Width != layout.Width || captured.Height != layout.Height)
            throw new InvalidOperationException("The display layout changed. Try capturing again.");
        return captured;
    }, cancellationToken);

    private static CapturedDisplay CaptureDesktop(DisplayTopology topology, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        using var bitmap = CreateCanvas(topology);
        nint desktop = GdiCaptureNative.GetDC(0);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The desktop is unavailable in this Windows session.");
        try
        {
            foreach (var monitor in topology.Monitors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyMonitor(bitmap, topology.Bounds, monitor.Bounds, desktop,
                    new Win32.Point { X = monitor.Bounds.Left, Y = monitor.Bounds.Top },
                    GdiCaptureNative.SrcCopy | GdiCaptureNative.CaptureBlt | GdiCaptureNative.NoMirrorBitmap);
            }
            if (!GdiCaptureNative.GdiFlush()) throw new InvalidOperationException("Could not complete the desktop snapshot.");
        }
        finally { GdiCaptureNative.ReleaseDC(0, desktop); }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = bitmap.CopyOpaqueBgra();
        CaptureTiming.Write("still.desktop", timer.Elapsed, bitmap.Width, bitmap.Height);
        return CapturedDisplay.FromOpaqueBgra(bitmap.Width, bitmap.Height, pixels);
    }

    internal static (int Width, int Height, int ByteLength) ValidateLayout(Win32.Rect bounds)
    {
        long width = (long)bounds.Right - bounds.Left, height = (long)bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (width > 32_768 || height > 32_768 || width * height > 64L * 1024 * 1024)
            throw new InvalidOperationException("The combined display area is too large to capture.");
        return ((int)width, (int)height, checked((int)(width * height * 4)));
    }

    internal static NativeCaptureBitmap CreateCanvas(DisplayTopology topology)
    {
        var layout = ValidateLayout(topology.Bounds);
        var bitmap = NativeCaptureBitmap.Create(layout.Width, layout.Height);
        try
        {
            if (!GdiCaptureNative.PatBlt(bitmap.Dc, 0, 0, layout.Width, layout.Height, GdiCaptureNative.Blackness))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize the desktop snapshot.");
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    // Shared by desktop acquisition and generated memory-DIB composition tests.
    internal static void CopyMonitor(NativeCaptureBitmap destination, Win32.Rect canvasBounds, Win32.Rect monitorBounds,
        nint sourceDc, Win32.Point sourceOrigin, uint operation)
    {
        var layout = ValidateLayout(canvasBounds);
        if (destination.Width != layout.Width || destination.Height != layout.Height || sourceDc == 0
            || monitorBounds.Right <= monitorBounds.Left || monitorBounds.Bottom <= monitorBounds.Top
            || monitorBounds.Left < canvasBounds.Left || monitorBounds.Top < canvasBounds.Top
            || monitorBounds.Right > canvasBounds.Right || monitorBounds.Bottom > canvasBounds.Bottom)
            throw new ArgumentException("The monitor pixels do not fit inside the desktop snapshot.", nameof(monitorBounds));
        int x = checked(monitorBounds.Left - canvasBounds.Left), y = checked(monitorBounds.Top - canvasBounds.Top);
        if (!GdiCaptureNative.BitBlt(destination.Dc, x, y, monitorBounds.Width, monitorBounds.Height,
            sourceDc, sourceOrigin.X, sourceOrigin.Y, operation))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture a display.");
    }

    private static void EnsureCurrent(DisplayTopology topology)
    {
        if (!topology.IsCurrent()) throw new InvalidOperationException("The display layout changed. Try capturing again.");
    }
}
