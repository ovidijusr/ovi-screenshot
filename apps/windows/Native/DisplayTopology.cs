using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ovi.Screenshot.Native;

internal sealed record DisplayMonitor(nint Handle, Win32.Rect Bounds, Win32.Rect WorkArea)
{
    internal bool IsCurrent()
    {
        var info = new Win32.MonitorInfo { Size = (uint)Marshal.SizeOf<Win32.MonitorInfo>() };
        return Win32.GetMonitorInfo(Handle, ref info) && info.Monitor.Equals(Bounds);
    }
}

// All coordinates are physical virtual-screen pixels under the app's PMv2 manifest.
internal sealed class DisplayTopology
{
    internal IReadOnlyList<DisplayMonitor> Monitors { get; }
    internal Win32.Rect Bounds { get; }

    internal DisplayTopology(IReadOnlyList<DisplayMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) throw new ArgumentException("No displays are available.", nameof(monitors));
        var snapshot = monitors.ToArray();
        var handles = new HashSet<nint>();
        foreach (var monitor in snapshot)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            ValidateRectangle(monitor.Bounds);
            if (!handles.Add(monitor.Handle)) throw new ArgumentException("Display handles must be unique.", nameof(monitors));
        }
        var bounds = new Win32.Rect
        {
            Left = snapshot.Min(monitor => monitor.Bounds.Left), Top = snapshot.Min(monitor => monitor.Bounds.Top),
            Right = snapshot.Max(monitor => monitor.Bounds.Right), Bottom = snapshot.Max(monitor => monitor.Bounds.Bottom)
        };
        ValidateRectangle(bounds);
        Monitors = Array.AsReadOnly(snapshot);
        Bounds = bounds;
    }

    internal static DisplayTopology Enumerate()
    {
        var monitors = new List<DisplayMonitor>();
        Exception? failure = null;
        Native.MonitorEnum callback = AddMonitor;
        bool success = Native.EnumDisplayMonitors(0, 0, callback, 0);
        int error = Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);
        if (failure is not null) throw new InvalidOperationException("The display layout changed. Try capturing again.", failure);
        if (!success) throw new Win32Exception(error, "Could not enumerate the displays.");
        return new DisplayTopology(monitors);

        bool AddMonitor(nint handle, nint dc, ref Win32.Rect rectangle, nint state)
        {
            try
            {
                var info = new Win32.MonitorInfo { Size = (uint)Marshal.SizeOf<Win32.MonitorInfo>() };
                if (!Native.GetMonitorInfo(handle, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                monitors.Add(new DisplayMonitor(handle, info.Monitor, info.Work));
                return true;
            }
            catch (Exception exception)
            {
                // Managed exceptions must never unwind through the native callback.
                failure = exception;
                return false;
            }
        }
    }

    internal DisplayMonitor AtPoint(Win32.Point point)
    {
        foreach (var monitor in Monitors)
            if (ContainsPoint(monitor.Bounds, point)) return monitor;

        DisplayMonitor nearest = Monitors[0];
        double nearestDistance = double.PositiveInfinity;
        foreach (var monitor in Monitors)
        {
            var bounds = monitor.Bounds;
            double dx = point.X < bounds.Left ? (long)bounds.Left - point.X
                : point.X >= bounds.Right ? (long)point.X - (bounds.Right - 1) : 0;
            double dy = point.Y < bounds.Top ? (long)bounds.Top - point.Y
                : point.Y >= bounds.Bottom ? (long)point.Y - (bounds.Bottom - 1) : 0;
            double distance = dx * dx + dy * dy;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = monitor; }
        }
        return nearest;
    }

    internal DisplayMonitor ForRectangle(Win32.Rect rectangle)
    {
        ValidateRectangle(rectangle);
        DisplayMonitor? largest = null;
        long largestArea = 0;
        foreach (var monitor in Monitors)
        {
            long width = Math.Max(0L, (long)Math.Min(rectangle.Right, monitor.Bounds.Right) - Math.Max(rectangle.Left, monitor.Bounds.Left));
            long height = Math.Max(0L, (long)Math.Min(rectangle.Bottom, monitor.Bounds.Bottom) - Math.Max(rectangle.Top, monitor.Bounds.Top));
            long area = width * height;
            if (area > largestArea) { largestArea = area; largest = monitor; }
        }
        return largest ?? AtPoint(new Win32.Point
        {
            X = (int)((long)rectangle.Left + ((long)rectangle.Right - rectangle.Left) / 2),
            Y = (int)((long)rectangle.Top + ((long)rectangle.Bottom - rectangle.Top) / 2)
        });
    }

    // Contains the selection's canvas, including deterministic black display gaps.
    internal bool Contains(Win32.Rect rectangle) => rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top
        && rectangle.Left >= Bounds.Left && rectangle.Top >= Bounds.Top && rectangle.Right <= Bounds.Right && rectangle.Bottom <= Bounds.Bottom;

    internal static bool ContainsPoint(Win32.Rect bounds, Win32.Point point) => point.X >= bounds.Left && point.X < bounds.Right
        && point.Y >= bounds.Top && point.Y < bounds.Bottom;

    internal bool IsCurrent()
    {
        try { return Matches(Enumerate()); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    internal bool Matches(DisplayTopology other)
    {
        if (Monitors.Count != other.Monitors.Count) return false;
        return Monitors.All(monitor => other.Monitors.Any(current => current.Handle == monitor.Handle && SameBounds(current.Bounds, monitor.Bounds)));
    }

    private static bool SameBounds(Win32.Rect first, Win32.Rect second) => first.Left == second.Left && first.Top == second.Top
        && first.Right == second.Right && first.Bottom == second.Bottom;

    private static void ValidateRectangle(Win32.Rect rectangle)
    {
        long width = (long)rectangle.Right - rectangle.Left, height = (long)rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "The display rectangle is invalid.");
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate bool MonitorEnum(nint monitor, nint dc, ref Win32.Rect rectangle, nint state);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint state);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)] internal static extern bool GetMonitorInfo(nint monitor, ref Win32.MonitorInfo info);
    }
}
