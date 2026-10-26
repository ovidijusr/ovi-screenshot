using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Testing;

internal static class SelectionCoordinatesSmoke
{
    internal static void Verify()
    {
        var left = new DisplayMonitor(1, Rect(-2560, -360, 0, 1080), Rect(-2560, -360, 0, 1040));
        var right = new DisplayMonitor(2, Rect(0, 0, 1920, 1080), Rect(0, 0, 1920, 1040));
        var topology = new DisplayTopology([left, right]);
        var cross = SelectionCoordinates.ToDesktop(new PixelSelection(2400, 400, 400, 300), topology.Bounds);
        Require(cross.Equals(Rect(-160, 40, 240, 340)), "Spanning selection changed pixel coordinates at a negative origin.");
        Require(SelectionCoordinates.IncludesDisplayPixels(topology, cross), "A spanning screenshot lost its actual display pixels.");
        bool rejected = false;
        try { SelectionCoordinates.RecordingMonitor(topology, cross); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Video routing accepted a selection spanning displays.");
        var onRight = SelectionCoordinates.ToDesktop(new PixelSelection(2680, 440, 600, 350), topology.Bounds);
        Require(ReferenceEquals(SelectionCoordinates.RecordingMonitor(topology, onRight), right)
            && SelectionCoordinates.ToMonitor(onRight, right) == new PixelSelection(120, 80, 600, 350),
            "Video on the second display reused virtual-desktop coordinates.");
        var onLeft = Rect(-2500, -300, -1900, 100);
        Require(ReferenceEquals(SelectionCoordinates.RecordingMonitor(topology, onLeft), left)
            && SelectionCoordinates.ToMonitor(onLeft, left) == new PixelSelection(60, 60, 600, 400),
            "Video above/left of the primary display was scaled or translated incorrectly.");
        var gap = Rect(20, -300, 200, -100);
        Require(!SelectionCoordinates.IncludesDisplayPixels(topology, gap), "A gap-only selection claimed to contain display pixels.");
        Require(ReferenceEquals(topology.ForRectangle(cross), right), "Preview placement ignored the display with the largest captured area.");
    }

    private static Win32.Rect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Multi-display routing regression: " + message);
    }
}
