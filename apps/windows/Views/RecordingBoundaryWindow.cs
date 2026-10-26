using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Views;

// UI-thread-owned, noninteractive HWND strips. There is deliberately no window
// covering the recording's interior: no dimming, input capture, or bitmap read.
internal sealed class RecordingBoundaryWindow : IDisposable
{
    private const uint ExcludeFromCapture = 0x11;
    private const uint ExtendedStyle = 0x00000008 | 0x00000020 | 0x00000080 | 0x00080000 | 0x08000000;
    private static readonly Dictionary<nint, RecordingBoundaryWindow> Owners = new();
    private static readonly Win32.WindowProcedure Procedure = WindowProcedure;
    private readonly string _className = $"OviRecordingBoundary_{Guid.NewGuid():N}";
    private readonly uint _thread = Native.GetCurrentThreadId();
    private readonly nint _instance = Native.GetModuleHandle(null);
    private Win32.Rect _region, _monitor;
    private readonly List<Strip> _strips = new(4);
    private nint _brush;
    private bool _registered, _paused, _disposed;
    private readonly bool _selecting;
    private Exception? _paintFailure;

    internal bool IsVisible => !_disposed && _strips.Any(strip => strip.Window != 0 && Native.IsWindowVisible(strip.Window));
    internal int VisibleStripCount => _strips.Count(strip => strip.Window != 0 && Native.IsWindowVisible(strip.Window));

    internal RecordingBoundaryWindow(Win32.Rect screenRegion, Win32.Rect monitorBounds, bool selecting = false)
    {
        _selecting = selecting;
        // Validate/clamp with the same pure geometry used by the native strips.
        _ = Layout(screenRegion, monitorBounds, 2);
        _region = Intersect(screenRegion, monitorBounds);
        _monitor = monitorBounds;
        try
        {
            RefreshBrush();
            var definition = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = _instance,
                ClassName = _className
            };
            if (Native.RegisterClassEx(in definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
            foreach (var placement in Layout(_region, _monitor, 2))
            {
                var bounds = placement.Bounds;
                nint window = Native.CreateWindowEx(ExtendedStyle, _className, "Recording boundary",
                    0x80000000 | 0x08000000, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    0, 0, _instance, 0);
                if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var strip = new Strip { Window = window };
                _strips.Add(strip);
                Owners.Add(window, this);
                Win32.DisableWindowTransitions(window);
                // WS_EX_LAYERED + WS_EX_TRANSPARENT passes mouse events through
                // to OTHER processes too; HTTRANSPARENT alone cannot ensure that.
                if (!Native.SetLayeredWindowAttributes(window, 0, 255, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                strip.Excluded = Win32.SetWindowDisplayAffinity(window, ExcludeFromCapture)
                    && Native.GetWindowDisplayAffinity(window, out uint affinity)
                    && affinity == ExcludeFromCapture;
            }
            uint dpi = Win32.GetDpiForWindow(_strips[0].Window);
            int thickness = Math.Max(1, (int)Math.Round(2 * (dpi == 0 ? 96 : dpi) / 96d));
            var layout = Layout(_region, _monitor, thickness);
            for (int index = 0; index < _strips.Count; index++)
            {
                var strip = _strips[index];
                strip.Placement = layout[index];
                var bounds = strip.Placement.Bounds;
                if (!Win32.SetWindowPos(strip.Window, Win32.HwndTopmost, bounds.Left, bounds.Top,
                    bounds.Width, bounds.Height, Win32.SwpNoActivate))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (strip.Placement.RequiresCaptureExclusion && !strip.Excluded)
                    Trace.WriteLine("Recording boundary edge omitted because native capture exclusion could not be verified.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Show()
    {
        EnsureUsable();
        try
        {
            foreach (var strip in _strips)
            {
                if (strip.Placement.RequiresCaptureExclusion && !strip.Excluded) continue;
                var bounds = strip.Placement.Bounds;
                if (!Win32.SetWindowPos(strip.Window, Win32.HwndTopmost, bounds.Left, bounds.Top,
                    bounds.Width, bounds.Height, Win32.SwpNoActivate | Win32.SwpShowWindow))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Native.InvalidateRect(strip.Window, 0, false);
                Native.UpdateWindow(strip.Window);
            }
            ThrowPaintFailure();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void SetPaused(bool paused)
    {
        EnsureUsable();
        if (_paused == paused) return;
        _paused = paused;
        try
        {
            RefreshBrush();
            foreach (var strip in _strips)
            {
                Native.InvalidateRect(strip.Window, 0, false);
                Native.UpdateWindow(strip.Window);
            }
            ThrowPaintFailure();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void UpdateBounds(Win32.Rect region, Win32.Rect monitor)
    {
        EnsureUsable();
        var clipped = Intersect(region, monitor);
        if (_region.Equals(clipped) && _monitor.Equals(monitor)) return;
        uint dpi = Win32.GetDpiForWindow(_strips[0].Window);
        int thickness = Math.Max(1, (int)Math.Round(2 * (dpi == 0 ? 96 : dpi) / 96d));
        var layout = Layout(clipped, monitor, thickness);
        _region = clipped; _monitor = monitor;
        for (int index = 0; index < _strips.Count; index++)
        {
            var strip = _strips[index];
            strip.Placement = layout[index];
            var bounds = strip.Placement.Bounds;
            uint visibility = strip.Placement.RequiresCaptureExclusion && !strip.Excluded ? 0x0080u : Win32.SwpShowWindow;
            if (!Win32.SetWindowPos(strip.Window, Win32.HwndTopmost, bounds.Left, bounds.Top,
                bounds.Width, bounds.Height, Win32.SwpNoActivate | visibility))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Native.GetCurrentThreadId() != _thread)
            throw new InvalidOperationException("Recording boundaries must be used on their owning UI thread.");
        ThrowPaintFailure();
    }

    private void ThrowPaintFailure()
    {
        if (_paintFailure is { } error) throw new InvalidOperationException("The recording boundary could not be painted.", error);
    }

    private void RefreshBrush()
    {
        uint color = Native.GetSysColor(Win32.IsHighContrast && _paused ? 8 : 13);
        if (!Win32.IsHighContrast)
        {
            try
            {
                string key = _selecting ? "AccentFillColorDefaultBrush"
                    : _paused ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush";
                if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is SolidColorBrush brush)
                {
                    var value = brush.Color;
                    color = value.R | ((uint)value.G << 8) | ((uint)value.B << 16);
                }
            }
            catch (Exception error) { Trace.WriteLine(error); }
        }
        nint replacement = Native.CreateSolidBrush(color);
        if (replacement == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint previous = _brush;
        _brush = replacement;
        if (previous != 0) Native.DeleteObject(previous);
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (!Owners.TryGetValue(window, out var owner))
                return Native.DefWindowProc(window, message, wParam, lParam);
            switch (message)
            {
                case WindowMessage.NonClientHitTest: return new nint(-1); // WM_NCHITTEST: HTTRANSPARENT.
                case Win32.WmMouseActivate: return 3; // MA_NOACTIVATE.
                case WindowMessage.EraseBackground: return 1; // WM_ERASEBKGND: paint only the thin strip.
                case WindowMessage.Paint:
                    owner.Paint(window);
                    return 0;
                case WindowMessage.SettingsChanged: case WindowMessage.SystemColorsChanged: case WindowMessage.ThemeChanged: // Settings, system colors, theme.
                    if (!owner._disposed)
                    {
                        owner.RefreshBrush();
                        foreach (var strip in owner._strips) Native.InvalidateRect(strip.Window, 0, false);
                    }
                    return 0;
                case WindowMessage.NonClientDestroyed:
                    Owners.Remove(window);
                    foreach (var strip in owner._strips)
                        if (strip.Window == window) strip.Window = 0;
                    break;
            }
            return Native.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // Never unwind a managed exception through user32. A later public
            // call reports the error; immediately hide every remaining edge.
            try
            {
                if (Owners.TryGetValue(window, out var owner))
                {
                    owner._paintFailure ??= error;
                    foreach (var strip in owner._strips)
                        if (strip.Window != 0) Win32.ShowWindow(strip.Window, Win32.SwHide);
                }
                Trace.WriteLine(error);
            }
            catch { } // Error reporting itself must not cross the native ABI.
            return 0;
        }
    }

    private void Paint(nint window)
    {
        nint dc = Native.BeginPaint(window, out var paint);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (_disposed || _brush == 0) return;
            if (!Native.GetClientRect(window, out var bounds) || Native.FillRect(dc, in bounds, _brush) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Native.EndPaint(window, in paint); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Native.GetCurrentThreadId() != _thread)
            throw new InvalidOperationException("Recording boundaries must be disposed on their owning UI thread.");
        _disposed = true;
        foreach (var strip in _strips)
        {
            nint window = strip.Window;
            if (window == 0) continue;
            Win32.ShowWindow(window, Win32.SwHide);
            Native.DestroyWindow(window);
            Owners.Remove(window);
            strip.Window = 0;
        }
        if (_registered)
        {
            Native.UnregisterClass(_className, _instance);
            _registered = false;
        }
        if (_brush != 0) { Native.DeleteObject(_brush); _brush = 0; }
    }

    internal readonly record struct BoundaryStrip(Win32.Rect Bounds, bool RequiresCaptureExclusion);

    // Pure screen-pixel geometry. Prefer a full-width strip OUTSIDE each edge;
    // only fall inward where the monitor prevents that. Never cross a display.
    internal static BoundaryStrip[] Layout(Win32.Rect screenRegion, Win32.Rect monitorBounds, int thickness)
    {
        if (thickness < 1 || thickness > 64) throw new ArgumentOutOfRangeException(nameof(thickness));
        ValidateRect(screenRegion, nameof(screenRegion));
        ValidateRect(monitorBounds, nameof(monitorBounds));
        var region = Intersect(screenRegion, monitorBounds);
        ValidateRect(region, nameof(screenRegion));
        int insideWidth = Math.Min(thickness, region.Width), insideHeight = Math.Min(thickness, region.Height);
        int left = (long)region.Left - monitorBounds.Left >= thickness ? region.Left - thickness : region.Left;
        int right = (long)monitorBounds.Right - region.Right >= thickness ? region.Right + thickness : region.Right;
        var leftStrip = new Win32.Rect
        {
            Left = left, Top = region.Top, Right = left < region.Left ? region.Left : region.Left + insideWidth, Bottom = region.Bottom
        };
        var rightStrip = new Win32.Rect
        {
            Left = right > region.Right ? region.Right : region.Right - insideWidth, Top = region.Top, Right = right, Bottom = region.Bottom
        };
        int top = (long)region.Top - monitorBounds.Top >= thickness ? region.Top - thickness : region.Top;
        int bottom = (long)monitorBounds.Bottom - region.Bottom >= thickness ? region.Bottom + thickness : region.Bottom;
        var rectangles = new[]
        {
            new Win32.Rect { Left = leftStrip.Left, Top = top, Right = rightStrip.Right, Bottom = top < region.Top ? region.Top : region.Top + insideHeight },
            new Win32.Rect { Left = leftStrip.Left, Top = bottom > region.Bottom ? region.Bottom : region.Bottom - insideHeight, Right = rightStrip.Right, Bottom = bottom },
            leftStrip,
            rightStrip
        };
        return rectangles.Select(bounds => new BoundaryStrip(bounds, Overlaps(bounds, region))).ToArray();
    }

    internal static void VerifySyntheticLayout()
    {
        var monitor = new Win32.Rect { Left = -1920, Top = -200, Right = 0, Bottom = 880 };
        var inset = new Win32.Rect { Left = -1600, Top = 0, Right = -200, Bottom = 600 };
        var outside = Layout(inset, monitor, 4);
        Require(outside.Length == 4 && outside.All(strip => !strip.RequiresCaptureExclusion), "inset strips must remain outside the recording");
        var edges = Layout(monitor, monitor, 4);
        Require(edges.All(strip => strip.RequiresCaptureExclusion), "monitor-edge strips must require capture exclusion");
        foreach (var strip in outside.Concat(edges))
        {
            var bounds = strip.Bounds;
            Require(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= monitor.Left && bounds.Top >= monitor.Top
                && bounds.Right <= monitor.Right && bounds.Bottom <= monitor.Bottom, "negative-origin strips must stay within their display");
            Require(bounds.Width <= 4 || bounds.Height <= 4, "the recording interior must never be covered by an overlay window");
        }
        var tiny = Layout(new Win32.Rect { Left = -1920, Top = -200, Right = -1919, Bottom = -199 }, monitor, 8);
        Require(tiny.All(strip => strip.Bounds.Width > 0 && strip.Bounds.Height > 0), "one-pixel selections need valid thin strips");
        static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException($"Recording boundary geometry regression: {detail}.");
        }
    }

    private static void ValidateRect(Win32.Rect rectangle, string argument)
    {
        long width = (long)rectangle.Right - rectangle.Left, height = (long)rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
            throw new ArgumentOutOfRangeException(argument);
    }

    private static Win32.Rect Intersect(Win32.Rect first, Win32.Rect second) => new()
    {
        Left = Math.Max(first.Left, second.Left), Top = Math.Max(first.Top, second.Top),
        Right = Math.Min(first.Right, second.Right), Bottom = Math.Min(first.Bottom, second.Bottom)
    };

    private static bool Overlaps(Win32.Rect first, Win32.Rect second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;

    private sealed class Strip
    {
        internal nint Window;
        internal bool Excluded;
        internal BoundaryStrip Placement;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
        {
            public uint Size, Style;
            public nint Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? MenuName;
            public string ClassName;
            public nint SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PaintStruct
        {
            public nint Dc;
            public int Erase;
            public Win32.Rect Paint;
            public int Restore, IncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(in WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string className, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")] internal static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, nint rectangle, bool erase);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint BeginPaint(nint window, out PaintStruct paint);
        [DllImport("user32.dll")] internal static extern bool EndPaint(nint window, in PaintStruct paint);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Win32.Rect rectangle);
        [DllImport("user32.dll")] internal static extern int FillRect(nint dc, in Win32.Rect rectangle, nint brush);
        [DllImport("user32.dll")] internal static extern uint GetSysColor(int index);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    }
}
