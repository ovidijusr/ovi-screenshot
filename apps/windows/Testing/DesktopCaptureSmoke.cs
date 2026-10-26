using System.Buffers.Binary;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Interop;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Testing;

internal static class DesktopCaptureSmoke
{
    internal static void Verify()
    {
        var left = Monitor(1, -4, -1, 0, 2);
        var above = Monitor(2, 1, -3, 4, -1);
        var main = Monitor(3, 0, 0, 5, 3);
        var topology = new DisplayTopology([left, above, main]);
        var bounds = topology.Bounds;
        Require(bounds.Left == -4 && bounds.Top == -3 && bounds.Width == 9 && bounds.Height == 6,
            "Virtual bounds lost a negative-origin or unequal-resolution monitor.");
        VerifySelection(topology, left, above, main);
        VerifyLimits();
        VerifyLayoutComparison(topology, left, above, main);
        VerifyComposition(topology);
    }

    private static void VerifySelection(DisplayTopology topology, DisplayMonitor left, DisplayMonitor above, DisplayMonitor main)
    {
        Require(topology.AtPoint(new Win32.Point { X = -4, Y = -1 }) == left, "A negative-origin point selected the wrong monitor.");
        Require(topology.AtPoint(new Win32.Point { X = 0, Y = 0 }) == main, "Exclusive right edges selected the neighboring monitor incorrectly.");
        Require(topology.AtPoint(new Win32.Point { X = 1, Y = -3 }) == above, "The upper monitor was not addressable.");
        Require(topology.AtPoint(new Win32.Point { X = -3, Y = -3 }) == left, "A display gap did not use the nearest monitor.");
        Require(topology.Monitors.Contains(topology.AtPoint(new Win32.Point { X = int.MinValue, Y = int.MaxValue })),
            "An extreme point overflowed nearest-monitor distance.");
        Require(topology.ForRectangle(Rect(-1, 0, 3, 2)) == main, "Rectangle ownership ignored largest physical overlap.");
        Require(topology.ForRectangle(Rect(-4, -3, 4, 1)) == left, "Rectangle ownership confused canvas bounds with monitor coverage.");
        Require(topology.ForRectangle(Rect(4, -3, 5, -1)) == above, "A gap rectangle did not use its nearest-center fallback.");
        Require(topology.Contains(Rect(-4, -3, 5, 3)) && !topology.Contains(Rect(-5, -3, 5, 3)),
            "Virtual selection containment used the wrong bounds.");
    }

    private static void VerifyLimits()
    {
        var maximum = DesktopCapture.ValidateLayout(Rect(-16_384, -1024, 16_384, 1024));
        Require(maximum.Width == 32_768 && maximum.Height == 2048 && maximum.ByteLength == 256 * 1024 * 1024,
            "The exact capture layout limit was rejected or overflowed.");
        Reject(() => DesktopCapture.ValidateLayout(Rect(0, 0, 32_769, 1)));
        Reject(() => DesktopCapture.ValidateLayout(Rect(0, 0, 32_768, 2049)));
        Reject(() => DesktopCapture.ValidateLayout(Rect(int.MinValue, 0, int.MaxValue, 2)));
        Reject(() => DesktopCapture.ValidateLayout(Rect(0, 0, 0, 1)));
        Reject(() => new DisplayTopology([]));
        Reject(() => new DisplayTopology([Monitor(1, int.MinValue, 0, -1, 1), Monitor(2, 1, 0, int.MaxValue, 1)]));
        Reject(() => new DisplayTopology([Monitor(1, 0, 0, 2, 2), Monitor(1, 2, 0, 4, 2)]));
    }

    private static void VerifyLayoutComparison(DisplayTopology original, DisplayMonitor left, DisplayMonitor above, DisplayMonitor main)
    {
        Require(original.Matches(new DisplayTopology([main, left, above])), "Display enumeration order invalidated the same layout.");
        Require(!original.Matches(new DisplayTopology([left, above])), "A removed monitor was not detected.");
        Require(!original.Matches(new DisplayTopology([left, above, main with { Handle = new nint(4) }])), "A replaced monitor handle was not detected.");
        Require(!original.Matches(new DisplayTopology([left, above, main with { Bounds = Rect(0, 0, 6, 3) }])), "Changed monitor bounds were not detected.");
        Require(!original.Matches(new DisplayTopology([left, above, main with { Bounds = Rect(1, 0, 6, 3) }])), "A monitor moved without changing resolution was not detected.");
    }

    private static void VerifyComposition(DisplayTopology topology)
    {
        using var canvas = DesktopCapture.CreateCanvas(topology);
        var sources = new List<NativeCaptureBitmap>();
        try
        {
            foreach (var monitor in topology.Monitors)
            {
                byte[] pixels = new byte[monitor.Bounds.Width * monitor.Bounds.Height * 4];
                for (int y = 0; y < monitor.Bounds.Height; y++)
                for (int x = 0; x < monitor.Bounds.Width; x++)
                {
                    byte[] pixel = Pattern(monitor, x, y);
                    pixel.CopyTo(pixels, (y * monitor.Bounds.Width + x) * 4);
                }
                var source = NativeCaptureBitmap.FromBgra(monitor.Bounds.Width, monitor.Bounds.Height, pixels);
                sources.Add(source);
                DesktopCapture.CopyMonitor(canvas, topology.Bounds, monitor.Bounds, source.Dc, default, GdiCaptureNative.SrcCopy);
            }
            if (!GdiCaptureNative.GdiFlush()) throw new InvalidOperationException("Synthetic desktop composition did not flush.");
            byte[] actual = canvas.CopyOpaqueBgra();
            for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                var point = new Win32.Point { X = topology.Bounds.Left + x, Y = topology.Bounds.Top + y };
                var monitor = topology.Monitors.FirstOrDefault(candidate => DisplayTopology.ContainsPoint(candidate.Bounds, point));
                byte[] expected = monitor is null ? [0, 0, 0, 255] : Pattern(monitor, point.X - monitor.Bounds.Left, point.Y - monitor.Bounds.Top);
                expected[3] = 255;
                Require(actual.AsSpan((y * canvas.Width + x) * 4, 4).SequenceEqual(expected),
                    "Virtual desktop pixels changed offsets, gap fill, channels, edge pixels, or top-down row order.");
            }

            // This crop crosses all three generated displays and a black gap.
            using var document = ImageDocument.ImportBgraRegion(canvas.Width, canvas.Height, actual, 2, 1, 6, 4);
            var preview = document.RenderPreview(6, 4);
            Require(preview.Image.Width == 6 && preview.Image.Height == 4 && preview.Rgba.Length == 6 * 4 * 4,
                "A cross-display Rust crop changed its dimensions.");
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 6; x++)
            {
                int source = ((y + 1) * canvas.Width + x + 2) * 4;
                int target = (y * 6 + x) * 4;
                Require(preview.Rgba[target] == actual[source + 2] && preview.Rgba[target + 1] == actual[source + 1]
                    && preview.Rgba[target + 2] == actual[source] && preview.Rgba[target + 3] == 255,
                    "A cross-display Rust crop changed channels, row order, or opaque gap pixels.");
            }
            byte[] png = document.ExportPng();
            Require(png.Length >= 24 && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) == 6
                && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) == 4,
                "A cross-display PNG export changed its selected dimensions.");
        }
        finally { foreach (var source in sources) source.Dispose(); }
    }

    private static byte[] Pattern(DisplayMonitor monitor, int x, int y)
    {
        int basis = (int)monitor.Handle * 50;
        return [(byte)(basis + x), (byte)(basis + y * 3), (byte)(basis + y * monitor.Bounds.Width + x), 0];
    }

    private static DisplayMonitor Monitor(int handle, int left, int top, int right, int bottom)
    {
        var bounds = Rect(left, top, right, bottom);
        return new DisplayMonitor(new nint(handle), bounds, bounds);
    }

    private static Win32.Rect Rect(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("An invalid or oversized desktop layout was accepted.");
    }
}
