using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Capture;

internal static class SelectionCoordinates
{
    internal static Win32.Rect ToDesktop(PixelSelection selection, Win32.Rect snapshotBounds)
    {
        if (selection.X < 0 || selection.Y < 0 || selection.Width <= 0 || selection.Height <= 0
            || (long)selection.X + selection.Width > snapshotBounds.Width
            || (long)selection.Y + selection.Height > snapshotBounds.Height)
            throw new ArgumentOutOfRangeException(nameof(selection), "The selected area is outside the captured desktop.");
        return new Win32.Rect
        {
            Left = checked(snapshotBounds.Left + selection.X), Top = checked(snapshotBounds.Top + selection.Y),
            Right = checked(snapshotBounds.Left + selection.X + selection.Width),
            Bottom = checked(snapshotBounds.Top + selection.Y + selection.Height)
        };
    }

    internal static DisplayMonitor RecordingMonitor(DisplayTopology topology, Win32.Rect selection) =>
        topology.Monitors.FirstOrDefault(monitor => Contains(monitor.Bounds, selection))
        ?? throw new InvalidOperationException("Select a recording area within one display.");

    internal static PixelSelection ToMonitor(Win32.Rect selection, DisplayMonitor monitor)
    {
        if (!Contains(monitor.Bounds, selection))
            throw new ArgumentOutOfRangeException(nameof(selection), "The recording area crosses display boundaries.");
        return new PixelSelection(selection.Left - monitor.Bounds.Left, selection.Top - monitor.Bounds.Top,
            selection.Width, selection.Height);
    }

    internal static bool IncludesDisplayPixels(DisplayTopology topology, Win32.Rect selection) =>
        topology.Monitors.Any(monitor => Math.Max(selection.Left, monitor.Bounds.Left) < Math.Min(selection.Right, monitor.Bounds.Right)
            && Math.Max(selection.Top, monitor.Bounds.Top) < Math.Min(selection.Bottom, monitor.Bounds.Bottom));

    private static bool Contains(Win32.Rect outer, Win32.Rect inner) => inner.Width > 0 && inner.Height > 0
        && inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
