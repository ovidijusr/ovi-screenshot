using System.Runtime.InteropServices;
using System.Text;

namespace Ovi.Screenshot.Native;

internal sealed record WindowCaptureTarget(nint Handle, Win32.Rect Bounds, string Title)
{
    // Optional identity snapshot guards against recycled HWNDs. Explicit own-window
    // synthetic fixtures may keep zero and acquire a fresh identity at capture start.
    internal uint ProcessId { get; init; }
}

internal static class WindowTargets
{
    internal static IReadOnlyList<WindowCaptureTarget> Enumerate()
    {
        var targets = new List<WindowCaptureTarget>();
        var visited = new HashSet<nint>();
        nint shell = GetShellWindow(), desktop = GetDesktopWindow();
        nint window = GetTopWindow(0);
        // A changing desktop is not an atomic snapshot. Bound traversal and avoid
        // cycles if a window is destroyed/reordered while metadata is inspected.
        while (window != 0 && visited.Count < 8192 && visited.Add(window))
        {
            nint current = window;
            window = GetWindow(current, 2); // GW_HWNDNEXT: next lower window in z-order.
            uint process = OwnerProcessId(current);
            if (process == 0 || process == (uint)Environment.ProcessId || current == shell || current == desktop) continue;
            var className = new StringBuilder(256);
            GetClassName(current, className, className.Capacity);
            if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") continue;
            if (!TryGetBounds(current, out var bounds)) continue;
            int length = Math.Clamp(GetWindowTextLength(current), 0, 4095);
            var title = new StringBuilder(length + 1);
            GetWindowText(current, title, title.Capacity);
            targets.Add(new WindowCaptureTarget(current, bounds, string.IsNullOrWhiteSpace(title.ToString()) ? "Window" : title.ToString())
            {
                ProcessId = process
            });
        }
        return targets;
    }

    // Also accepts our own HWND for the explicitly synthetic capture fixture.
    internal static bool TryGetBounds(nint window, out Win32.Rect bounds)
    {
        bounds = default;
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)) return false;
        if (DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) < 0 || cloaked != 0) return false;
        return DwmGetWindowAttribute(window, 9, out bounds, (uint)Marshal.SizeOf<Win32.Rect>()) >= 0 && bounds.Width > 0 && bounds.Height > 0;
    }

    internal static uint OwnerProcessId(nint window)
    {
        GetWindowThreadProcessId(window, out uint process);
        return process;
    }

    internal static bool ExcludesCapture(nint window) => GetWindowDisplayAffinity(window, out uint affinity) && affinity != 0;

    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder value, int capacity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Win32.Rect value, uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
}
