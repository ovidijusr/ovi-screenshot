using System.Runtime.InteropServices;

namespace Ovi.Screenshot.Native;

internal static class Win32
{
    internal const int GwlWndProc = -4, GwlExStyle = -20;
    internal const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    internal const int SwHide = 0, SwShowNoActivate = 4;
    internal const uint WmHotkey = 0x0312, WmMouseActivate = 0x0021, WmApp = 0x8000;
    internal const long WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
    internal static readonly nint HwndTopmost = new(-1);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CallWindowProc(nint procedure, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);

    internal static bool TryRoundWindow(nint window)
    {
        int preference = 2; // DWMWCP_ROUND; the native frame owns clipping and shadow.
        return DwmSetWindowAttribute(window, 33, ref preference, sizeof(int)) >= 0;
    }

    internal static void DisableWindowTransitions(nint window)
    {
        int disabled = 1;
        _ = DwmSetWindowAttribute(window, 3, ref disabled, sizeof(int));
    }

    internal static bool TrySetCloaked(nint window, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(window, 13, ref value, sizeof(int)) >= 0;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    internal static void FlushDesktopComposition() => _ = DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo { public uint Size, Flags; public nint DefaultScheme; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrastInfo value, uint flags);

    internal static bool IsHighContrast
    {
        get
        {
            var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
            return SystemParametersInfo(0x0042, info.Size, ref info, 0) && (info.Flags & 1) != 0;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadIcon(nint instance, nint iconName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    internal static (nint Monitor, MonitorInfo Info) PointerMonitor()
    {
        if (!GetCursorPos(out Point pointer)) throw new InvalidOperationException("The pointer position is unavailable.");
        nint monitor = MonitorFromPoint(pointer, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("The display is unavailable.");
        return (monitor, info);
    }
}

internal sealed class WindowHook : IDisposable
{
    private readonly nint _window;
    private readonly Win32.WindowProcedure _callback;
    private readonly nint _previous;
    private bool _disposed;

    internal WindowHook(nint window, Func<uint, nuint, nint, nint?> handler, Action<Exception>? onError = null)
    {
        _window = window;
        _callback = (hwnd, message, wParam, lParam) =>
        {
            try
            {
                nint? result = handler(message, wParam, lParam);
                if (result.HasValue) return result.Value;
            }
            catch (Exception error)
            {
                // Managed exceptions must not escape through the native window procedure.
                try { onError?.Invoke(error); } catch { System.Diagnostics.Trace.WriteLine(error); }
            }
            return Win32.CallWindowProc(_previous, hwnd, message, wParam, lParam);
        };
        _previous = Win32.SetWindowLongPtr(window, Win32.GwlWndProc, Marshal.GetFunctionPointerForDelegate(_callback));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Win32.SetWindowLongPtr(_window, Win32.GwlWndProc, _previous);
        GC.KeepAlive(_callback);
    }
}
