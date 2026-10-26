using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Interop;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Views;

internal readonly record struct PixelSelection(int X, int Y, int Width, int Height);

// The virtual-desktop selector is a thin Win32 surface. Ordinary product windows stay
// WinUI. Its frozen bitmap is ready before showing; no XAML upload/cloak warm-up.
internal sealed class SelectionWindow
{
    private const string ClassName = "OviScreenshotSelectionV2";
    private const int CancelId = 1001;
    private const uint OwnerDrawPressed = 1, OwnerDrawFocused = 16, OwnerDrawHideFocus = 0x0200;
    private static readonly Dictionary<nint, SelectionWindow> Windows = new();
    private static readonly Native.WindowProcedure Procedure = WindowProcedure;
    private static ushort _classAtom;
    private readonly CapturedDisplay _capture;
    private readonly Win32.Rect _bounds;
    private readonly IReadOnlyList<Win32.Rect>? _areaLimits;
    private readonly bool _confirmSelection;
    private Win32.Rect? _activeAreaLimit;
    private readonly IReadOnlyList<WindowCaptureTarget> _windows;
    private readonly NativeCaptureBitmap _source, _buffer;
    private readonly TaskCompletionSource<PixelSelection?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly nint _blackPen, _whitePen;
    private readonly Stopwatch _prepareTimer = Stopwatch.StartNew();
    private nint _window, _cancel;
    private nint _toolbarHandle;
    private nint _cancelFont, _hintFont;
    private Win32.Rect _cancelBounds;
    private Win32.Rect _modeHintBounds;
    private double _scale = 1;
    private bool _cancelHovered;
    private WindowHook? _buttonHook;
    private int _anchorX, _anchorY, _currentX, _currentY;
    private bool _anchored, _dragging, _complete, _shown, _disposed, _selectionReady, _moving;
    private PixelSelection _moveStart;
    private int _moveOriginX, _moveOriginY;
    private bool _windowMode;
    private bool _windowClickPending;
    private bool _closingNotified;
    private bool _hadForeground;
    private WindowCaptureTarget? _pressedWindow;
    private int _windowIndex = -1;
    private int Width => _bounds.Width;
    private int Height => _bounds.Height;
    internal bool IsVisible => _window != 0 && Native.IsWindowVisible(_window);
    internal nint Handle => _window;
    internal bool HasSelection => _selectionReady && !_dragging && !_windowClickPending && !_complete;
    internal event Action<bool>? SelectionChanged;
    internal event Action? Activated;
    internal event Action? ToolbarRequested;
    internal event Action? Closing;
    internal WindowCaptureTarget? SelectedWindow { get; private set; }

    internal SelectionWindow(CapturedDisplay capture, Win32.Rect bounds, byte[]? preparedBgra = null,
        IReadOnlyList<WindowCaptureTarget>? windows = null, Win32.Rect? controlsBounds = null,
        IReadOnlyList<Win32.Rect>? areaLimits = null, bool confirmSelection = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        _capture = capture; _bounds = bounds; _confirmSelection = confirmSelection;
        Win32.Rect controls = ClipWindow(controlsBounds ?? bounds, bounds);
        if (controls.Width <= 0 || controls.Height <= 0) throw new ArgumentException("Selection controls must be inside the captured desktop.", nameof(controlsBounds));
        _areaLimits = areaLimits?.Select(limit => ClipWindow(limit, bounds)).Where(limit => limit.Width > 0 && limit.Height > 0).ToArray();
        if (_areaLimits is { Count: 0 }) throw new ArgumentException("Recording selection requires an available display.", nameof(areaLimits));
        _windows = windows?.Where(target => target.Handle != 0 && ClipWindow(target.Bounds, bounds) is { Width: > 0, Height: > 0 }).ToArray() ?? [];
        byte[] pixels = preparedBgra ?? capture.PremultipliedBgra ?? BitmapSources.ToPremultipliedBgra(capture.Rgba);
        _source = NativeCaptureBitmap.FromBgra(capture.Width, capture.Height, pixels);
        try
        {
            _buffer = NativeCaptureBitmap.Create(Width, Height);
            _blackPen = Native.CreatePen(0, 3, 0);
            _whitePen = Native.CreatePen(0, 1, 0x00FFFFFF);
            if (_blackPen == 0 || _whitePen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            RegisterClass();
            _window = Native.CreateWindowEx(0x00000008 | 0x00000080 | 0x00010000, ClassName,
                confirmSelection
                    ? "Select recording area. Drag or use arrows and Enter. Space selects a window. Tab opens recording controls. Escape cancels."
                    : "Select capture area. Drag or use arrows and Enter. Space selects a window. Escape cancels.", 0x80000000 | 0x02000000,
                controls.Left, controls.Top, controls.Width, controls.Height, 0, 0, Native.GetModuleHandle(null), 0);
            if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint dpi = Win32.GetDpiForWindow(_window);
            _scale = dpi == 0 ? 1 : dpi / 96d;
            // Construction DPI messages must use DefWindowProc, not cancel a
            // partially built selector. Keep the HWND hidden until fully painted.
            if (!Win32.SetWindowPos(_window, 0, bounds.Left, bounds.Top, Width, Height, Win32.SwpNoActivate | 0x0004))
                throw new InvalidOperationException("Could not position the selection canvas."); // SWP_NOZORDER
            Windows.Add(_window, this);
            Win32.DisableWindowTransitions(_window);
            Win32.SetWindowDisplayAffinity(_window, 0x11);
            if (Win32.GetCursorPos(out var pointer)) SetCurrent(pointer.X - bounds.Left, pointer.Y - bounds.Top);
            (_cancelBounds, _modeHintBounds) = PlaceControls(bounds, controls, _scale);
            _cancelFont = CreateUiFont(13, _scale);
            _hintFont = CreateUiFont(11, _scale);
            _cancel = Native.CreateWindowEx(0, "BUTTON", "Cancel (Esc)", 0x40000000 | 0x10000000 | 0x00010000 | 0x0000000B,
                _cancelBounds.Left, _cancelBounds.Top, _cancelBounds.Width, _cancelBounds.Height,
                _window, new nint(CancelId), Native.GetModuleHandle(null), 0);
            if (_cancel == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SendMessage(_cancel, WindowMessage.SetFont, (nuint)_cancelFont, new nint(1));
            _buttonHook = new WindowHook(_cancel, (message, key, _) =>
            {
                if (message == WindowMessage.EraseBackground) return 1;
                if (message == WindowMessage.MouseMove && !_cancelHovered)
                {
                    _cancelHovered = true;
                    var tracking = new Native.MouseTracking { Size = (uint)Marshal.SizeOf<Native.MouseTracking>(), Flags = 2, Window = _cancel };
                    Native.TrackMouseEvent(ref tracking);
                    Native.InvalidateRect(_cancel, 0, false);
                }
                if (message == WindowMessage.MouseLeave)
                {
                    _cancelHovered = false;
                    Native.InvalidateRect(_cancel, 0, false);
                }
                if (message != WindowMessage.KeyDown) return null;
                if ((uint)key is 0x1B or 0x0D) { Finish(null); return 0; }
                if ((uint)key == 0x09 || (_confirmSelection && (uint)key == 0x75))
                {
                    if (_confirmSelection) ToolbarRequested?.Invoke(); else Native.SetFocus(_window);
                    return 0;
                }
                return null;
            }, Fail);
            // Paint the complete backing surface before its HWND becomes visible.
            PaintBuffer(0);
            CaptureTiming.Write("overlay.prepare", _prepareTimer.Elapsed, Width, Height);
        }
        catch { DisposeResources(); if (_window != 0) Native.DestroyWindow(_window); throw; }
    }

    internal Task<PixelSelection?> SelectAsync()
    {
        if (_complete || _shown) return _completion.Task;
        _shown = true;
        var timer = Stopwatch.StartNew();
        Native.ShowWindow(_window, 5);
        if (Native.SetForegroundWindow(_window)) _hadForeground = true;
        Native.SetFocus(_window);
        Native.InvalidateRect(_window, 0, false);
        Native.UpdateWindow(_window);
        Native.UpdateWindow(_cancel);
        CaptureTiming.Write("overlay.show-painted", timer.Elapsed, Width, Height);
        return _completion.Task;
    }

    internal void Cancel() => Finish(null);

    internal void SetToolbarHandle(nint toolbar)
    {
        if (!_confirmSelection || _complete || toolbar == 0) throw new InvalidOperationException("The recording toolbar is unavailable.");
        _toolbarHandle = toolbar;
    }

    internal void FocusSelection()
    {
        if (!_confirmSelection || _complete || !_shown || _window == 0) return;
        if (Native.SetForegroundWindow(_window)) _hadForeground = true;
        Native.SetFocus(_window);
    }

    internal void ConfirmSelection()
    {
        if (!_confirmSelection || !_selectionReady || _dragging || _windowClickPending || _complete) return;
        if (_windowMode)
        {
            if (ResolveWindowConfirmation(SelectedWindow, _bounds, _capture.Width, _capture.Height) is { } confirmation)
                Finish(confirmation.Selection);
        }
        else if (CurrentSelection() is { Width: > 0, Height: > 0 } area) Finish(area);
    }

    private static void RegisterClass()
    {
        if (_classAtom != 0) return;
        var definition = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = Native.GetModuleHandle(null), Cursor = Native.LoadCursor(0, new nint(32515)), ClassName = ClassName
            // Background brush intentionally NULL: never erase to white/black on show.
        };
        _classAtom = Native.RegisterClassEx(in definition);
        if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (_classAtom == 0) _classAtom = 1;
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(window, out var owner)) return Native.DefWindowProc(window, message, wParam, lParam);
        try { return owner.HandleMessage(message, wParam, lParam); }
        catch (Exception error)
        {
            // No managed exception may unwind through a native window callback.
            try { owner.Fail(error); } catch { }
            return 0;
        }
    }

    private nint HandleMessage(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WindowMessage.DrawItem:
                if ((int)wParam == CancelId) { DrawCancel(Marshal.PtrToStructure<Native.DrawItem>(lParam)); return 1; }
                break;
            case WindowMessage.SettingsChanged: case WindowMessage.ThemeChanged:
                if (_cancel != 0) Native.InvalidateRect(_cancel, 0, false);
                InvalidateModeHint();
                break;
            case WindowMessage.EraseBackground: return 1; // WM_ERASEBKGND: backing pixels always cover the client area.
            case WindowMessage.Paint: Paint(); return 0;
            case WindowMessage.LeftButtonDown:
            {
                var point = ReadPointer(lParam);
                if (_windowMode)
                {
                    ChangeVisuals(() => SetCurrent(point.X, point.Y));
                    _pressedWindow = CurrentWindow();
                    _windowClickPending = true;
                    NotifySelectionChanged();
                    Native.SetFocus(_window); Native.SetCapture(_window); return 0;
                }
                var sourcePoint = ToSourcePoint(point.X, point.Y);
                if (_confirmSelection && _selectionReady && Contains(CurrentSelection(), sourcePoint.X, sourcePoint.Y))
                {
                    _moving = _dragging = true;
                    NotifySelectionChanged();
                    _moveStart = CurrentSelection();
                    (_moveOriginX, _moveOriginY) = sourcePoint;
                    Native.SetFocus(_window); Native.SetCapture(_window); return 0;
                }
                ChangeVisuals(() =>
                {
                    SetSelectionReady(false);
                    SelectedWindow = null;
                    ResetAreaAnchor();
                    SetCurrent(point.X, point.Y);
                    _dragging = TryAnchorCurrent();
                });
                Native.SetFocus(_window);
                if (_dragging) Native.SetCapture(_window);
                return 0;
            }
            case WindowMessage.MouseMove:
            {
                var point = ReadPointer(lParam, latest: true);
                MovePointer(point.X, point.Y); return 0;
            }
            case WindowMessage.LeftButtonUp:
            {
                var point = ReadPointer(lParam);
                if (_windowClickPending)
                {
                    MovePointer(point.X, point.Y);
                    bool confirm = ShouldConfirmWindowRelease(_pressedWindow, CurrentWindow());
                    ClearPointerCapture();
                    if (confirm) ConfirmWindow();
                    NotifySelectionChanged();
                    return 0;
                }
                if (!_dragging) return 0;
                bool wasMoving = _moving;
                if (_moving) ChangeVisuals(() => MoveSelectedArea(point.X, point.Y));
                else ChangeVisuals(() => SetCurrent(point.X, point.Y));
                ClearPointerCapture();
                if (CurrentSelection() is { Width: > 0, Height: > 0 } area)
                {
                    if (_confirmSelection) { ChangeVisuals(() => SetSelectionReady(true)); Announce(); }
                    else Finish(area);
                    if (_confirmSelection && wasMoving) NotifySelectionChanged();
                }
                else ChangeVisuals(ResetAreaAnchor);
                return 0;
            }
            case WindowMessage.KeyDown:
                if ((uint)wParam == 0x20 && ((long)lParam & (1L << 30)) != 0) return 0;
                HandleKey((uint)wParam); return 0;
            case WindowMessage.Command: if ((int)(wParam & 0xFFFF) == CancelId) { Finish(null); return 0; } break;
            case WindowMessage.Close: Finish(null); return 0;
            case WindowMessage.CancelMode:
                if (!_confirmSelection || _dragging || _windowClickPending) Finish(null);
                return 0;
            case WindowMessage.DisplayChanged: case WindowMessage.DpiChanged:
                if (_shown) Finish(null);
                return 0;
            case WindowMessage.CaptureChanged: if (_dragging || _windowClickPending) Finish(null); return 0;
            case WindowMessage.Activate:
                if (!_shown) break;
                if (((uint)wParam & 0xFFFF) != 0)
                {
                    if (_confirmSelection) { ObserveForeground(); Activated?.Invoke(); }
                }
                else if (!_confirmSelection || (lParam != 0 && !IsOwnedForeground(lParam)))
                    Finish(null);
                else if (lParam == 0 || !IsOwnedForeground(lParam)) CheckForegroundLater();
                break;
            case WindowMessage.PowerBroadcast: if ((uint)wParam is 4 or 7) Finish(null); break;
            case WindowMessage.NonClientDestroyed:
                nint destroyed = _window;
                _complete = true;
                NotifyClosing();
                ClearPointerCapture();
                Windows.Remove(destroyed); _window = 0;
                DisposeResources(); _completion.TrySetResult(null);
                return Native.DefWindowProc(destroyed, message, wParam, lParam);
        }
        return Native.DefWindowProc(_window, message, wParam, lParam);
    }

    private void HandleKey(uint key)
    {
        if (key == 0x1B) { Finish(null); return; }
        if (key == 0x09 || (_confirmSelection && key == 0x75))
        {
            if (_confirmSelection) ToolbarRequested?.Invoke(); else Native.SetFocus(_cancel);
            return;
        }
        if (key == 0x20 && _windows.Count > 0) { ToggleWindowMode(); return; }
        if (_windowMode)
        {
            if (key == 0x0D) { ConfirmWindow(); return; }
            if (key is >= 0x25 and <= 0x28)
            {
                int direction = key is 0x25 or 0x26 ? -1 : 1;
                ChangeVisuals(() =>
                {
                    SetSelectionReady(false); SelectedWindow = null;
                    _windowIndex = NextWindowIndex(_windowIndex, _windows.Count, direction);
                });
                Announce();
            }
            return;
        }
        if (key == 0x0D)
        {
            if (_confirmSelection && _selectionReady) return;
            if (!_anchored) ChangeVisuals(() => TryAnchorCurrent());
            else if (CurrentSelection() is { Width: > 0, Height: > 0 } area)
            {
                if (_confirmSelection) ChangeVisuals(() => SetSelectionReady(true));
                else Finish(area);
            }
            Announce(); return;
        }
        int step = (Native.GetKeyState(0x10) & 0x8000) != 0 ? 10 : 1;
        if (_confirmSelection && _selectionReady && key is >= 0x25 and <= 0x28)
        {
            int dx = key == 0x25 ? -step : key == 0x27 ? step : 0;
            int dy = key == 0x26 ? -step : key == 0x28 ? step : 0;
            ChangeVisuals(() => ApplyMovedArea(ClampMovedArea(CurrentSelection(), dx, dy,
                _activeAreaLimit ?? new Win32.Rect { Right = _capture.Width, Bottom = _capture.Height })));
            Announce(); return;
        }
        ChangeVisuals(() =>
        {
            switch (key)
            {
                case 0x25: SetSourcePoint(_currentX - step, _currentY); break;
                case 0x27: SetSourcePoint(_currentX + step, _currentY); break;
                case 0x26: SetSourcePoint(_currentX, _currentY - step); break;
                case 0x28: SetSourcePoint(_currentX, _currentY + step); break;
            }
        });
        if (key is >= 0x25 and <= 0x28) Announce();
    }

    private void Announce()
    {
        if (_window == 0) return;
        if (_windowMode)
        {
            string title = (_selectionReady ? SelectedWindow : CurrentWindow())?.Title ?? "Point at a window";
            Native.SetWindowText(_window, _confirmSelection
                ? $"Window recording. {title}. {(_selectionReady ? "Use Record to start." : "Enter selects. Arrows choose a window.")} Space returns to area. Escape cancels."
                : $"Window capture. {title}. Enter captures. Arrows choose a window. Space returns to area. Escape cancels.");
            Native.NotifyWinEvent(0x800C, _window, 0, 0);
            return;
        }
        var selection = CurrentSelection();
        bool inGap = !_anchored && !TryLatchAreaLimit(_areaLimits, _bounds, _capture.Width, _capture.Height, _currentX, _currentY, out _);
        Native.SetWindowText(_window, inGap ? "Move the selection cursor onto a display. Enter sets a corner. Escape cancels."
            : _anchored ? $"Selected area {selection.Width} by {selection.Height} pixels. {(_confirmSelection ? _selectionReady ? "Use Record to start." : "Move and press Enter to select." : "Enter captures.")} Escape cancels."
            : $"Selection cursor {_currentX}, {_currentY}. Enter sets a corner. Escape cancels.");
        Native.NotifyWinEvent(0x800C, _window, 0, 0);
    }

    private void SetCurrent(int x, int y)
    {
        SetSourcePoint((int)Math.Round(x * (double)_capture.Width / Width), (int)Math.Round(y * (double)_capture.Height / Height));
        if (_windowMode)
        {
            int previous = _windowIndex;
            _windowIndex = HitTestWindow(_windows, _bounds.Left + x, _bounds.Top + y);
            if (_windowIndex != previous) Announce();
        }
    }

    private void SetSourcePoint(int x, int y) =>
        (_currentX, _currentY) = ClampAreaPoint(x, y, _capture.Width, _capture.Height, _activeAreaLimit);

    private bool TryAnchorCurrent()
    {
        if (!TryLatchAreaLimit(_areaLimits, _bounds, _capture.Width, _capture.Height, _currentX, _currentY, out var limit)) return false;
        _activeAreaLimit = limit;
        SetSourcePoint(_currentX, _currentY);
        _anchorX = _currentX; _anchorY = _currentY; _anchored = true;
        return true;
    }

    private void ResetAreaAnchor()
    {
        _anchored = false;
        _activeAreaLimit = null;
    }

    private void SetSelectionReady(bool ready)
    {
        if (_selectionReady == ready) return;
        _selectionReady = ready;
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged() => SelectionChanged?.Invoke(HasSelection);

    private (int X, int Y) ToSourcePoint(int x, int y) =>
        (Math.Clamp((int)Math.Round(x * (double)_capture.Width / Width), 0, _capture.Width),
         Math.Clamp((int)Math.Round(y * (double)_capture.Height / Height), 0, _capture.Height));

    private static bool Contains(PixelSelection area, int x, int y) =>
        x >= area.X && x < (long)area.X + area.Width && y >= area.Y && y < (long)area.Y + area.Height;

    private void MoveSelectedArea(int x, int y)
    {
        var point = ToSourcePoint(x, y);
        ApplyMovedArea(ClampMovedArea(_moveStart, point.X - _moveOriginX, point.Y - _moveOriginY,
            _activeAreaLimit ?? new Win32.Rect { Right = _capture.Width, Bottom = _capture.Height }));
    }

    private void ApplyMovedArea(PixelSelection area)
    {
        _anchorX = area.X; _anchorY = area.Y;
        _currentX = area.X + area.Width; _currentY = area.Y + area.Height;
    }

    private static PixelSelection ClampMovedArea(PixelSelection area, int dx, int dy, Win32.Rect limit)
    {
        int x = (int)Math.Clamp((long)area.X + dx, limit.Left, (long)limit.Right - area.Width);
        int y = (int)Math.Clamp((long)area.Y + dy, limit.Top, (long)limit.Bottom - area.Height);
        return area with { X = x, Y = y };
    }

    private bool IsOwnedForeground(nint candidate)
    {
        if (candidate == _window || candidate == _toolbarHandle) return true;
        nint root = Native.GetAncestor(candidate, 3);
        return root == _window || (_toolbarHandle != 0 && root == _toolbarHandle);
    }

    private void CheckForegroundLater()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dispatcher?.TryEnqueue(() =>
        {
            if (_complete || _window == 0) return;
            nint foreground = Win32.GetForegroundWindow();
            if (foreground != 0 && IsOwnedForeground(foreground)) _hadForeground = true;
            else if (_hadForeground && foreground != 0)
                Finish(null);
        });
    }

    private void ObserveForeground()
    {
        nint foreground = Win32.GetForegroundWindow();
        if (foreground != 0 && IsOwnedForeground(foreground)) _hadForeground = true;
    }

    private (int X, int Y) ReadPointer(nint messagePoint, bool latest = false)
    {
        // A button's queued position defines the exact corner, even if the cursor
        // moved before dispatch. Supported canvases fit the signed client range.
        if (latest && Win32.GetCursorPos(out var point)) return ScreenToViewport(_bounds, point);
        return (SignedLow(messagePoint), SignedHigh(messagePoint));
    }

    private static (int X, int Y) ScreenToViewport(Win32.Rect bounds, Win32.Point point) =>
        (checked(point.X - bounds.Left), checked(point.Y - bounds.Top));

    private void MovePointer(int x, int y)
    {
        if (_moving) { ChangeVisuals(() => MoveSelectedArea(x, y)); return; }
        if (_confirmSelection && _selectionReady && !_windowMode && !_dragging) return;
        if (_windowMode && HitTestWindow(_windows, _bounds.Left + x, _bounds.Top + y) == _windowIndex)
        {
            SetCurrent(x, y); // No changed mark and no full-window repaint while moving within one target.
            return;
        }
        ChangeVisuals(() => SetCurrent(x, y));
    }

    private void ToggleWindowMode()
    {
        ClearPointerCapture();
        bool hadSelection = _selectionReady;
        ChangeVisuals(() =>
        {
            SetSelectionReady(false); SelectedWindow = null;
            _windowMode = !_windowMode;
            ResetAreaAnchor();
            if (_windowMode && hadSelection && Win32.GetCursorPos(out var pointer))
                SetCurrent(pointer.X - _bounds.Left, pointer.Y - _bounds.Top);
            else _windowIndex = _windowMode ? HitTestWindow(_windows, _bounds.Left + Math.Min(X(_currentX), Width - 1), _bounds.Top + Math.Min(Y(_currentY), Height - 1)) : -1;
        });
        InvalidateModeHint();
        Announce();
    }

    private WindowCaptureTarget? CurrentWindow() => _windowIndex >= 0 && _windowIndex < _windows.Count ? _windows[_windowIndex] : null;

    private void ClearPointerCapture()
    {
        _dragging = _windowClickPending = _moving = false;
        _pressedWindow = null;
        if (_window != 0 && Native.GetCapture() == _window) Native.ReleaseCapture();
    }

    private static bool ShouldConfirmWindowRelease(WindowCaptureTarget? pressed, WindowCaptureTarget? released) =>
        pressed is not null && ReferenceEquals(pressed, released);

    private void ConfirmWindow()
    {
        if (ResolveWindowConfirmation(CurrentWindow(), _bounds, _capture.Width, _capture.Height) is not { } confirmation) return;
        if (_confirmSelection)
        {
            ChangeVisuals(() => { SelectedWindow = confirmation.Target; SetSelectionReady(true); });
            Announce();
        }
        else { SelectedWindow = confirmation.Target; Finish(confirmation.Selection); }
    }

    private Win32.Rect? MarkBounds()
    {
        if (_windowMode)
        {
            if ((_selectionReady ? SelectedWindow : CurrentWindow()) is not { } target) return null;
            Win32.Rect clipped = ClipWindow(target.Bounds, _bounds);
            return new Win32.Rect { Left = clipped.Left - _bounds.Left, Top = clipped.Top - _bounds.Top, Right = clipped.Right - _bounds.Left, Bottom = clipped.Bottom - _bounds.Top };
        }
        if (!_anchored) return null;
        var area = CurrentSelection();
        return new Win32.Rect { Left = X(area.X), Top = Y(area.Y), Right = X(area.X + area.Width), Bottom = Y(area.Y + area.Height) };
    }

    private static int HitTestWindow(IReadOnlyList<WindowCaptureTarget> windows, int screenX, int screenY)
    {
        for (int index = 0; index < windows.Count; index++)
        {
            Win32.Rect bounds = windows[index].Bounds;
            if (screenX >= bounds.Left && screenX < bounds.Right && screenY >= bounds.Top && screenY < bounds.Bottom) return index;
        }
        return -1;
    }

    private static int NextWindowIndex(int current, int count, int direction) => count == 0 ? -1
        : current < 0 ? direction > 0 ? 0 : count - 1 : (current + direction + count) % count;

    private static Win32.Rect ClipWindow(Win32.Rect window, Win32.Rect monitor) => new()
    {
        Left = Math.Max(window.Left, monitor.Left), Top = Math.Max(window.Top, monitor.Top),
        Right = Math.Min(window.Right, monitor.Right), Bottom = Math.Min(window.Bottom, monitor.Bottom)
    };

    private static (Win32.Rect Cancel, Win32.Rect Hint) PlaceControls(Win32.Rect desktop, Win32.Rect requested, double scale)
    {
        Win32.Rect work = ClipWindow(requested, desktop);
        if (work.Width <= 0 || work.Height <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("The selection controls have no usable display area.");
        int Dip(double value) => Math.Max(1, (int)Math.Round(value * scale));
        int horizontalInset = Math.Min(Dip(12), work.Width / 4);
        int margin = Math.Min(Dip(20), work.Height / 4);
        int width = Math.Min(Dip(112), Math.Max(1, work.Width - horizontalInset * 2));
        int height = Math.Min(Dip(40), Math.Max(1, work.Height - margin * 2));
        int left = work.Left - desktop.Left + (work.Width - width) / 2;
        int top = work.Bottom - desktop.Top - margin - height;
        var cancel = new Win32.Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        int hintWidth = Math.Min(Dip(300), Math.Max(1, work.Width - horizontalInset * 2));
        int workTop = work.Top - desktop.Top;
        int hintBottom = Math.Max(workTop, cancel.Top - Dip(4));
        int hintHeight = Math.Min(Dip(26), hintBottom - workTop);
        int hintLeft = work.Left - desktop.Left + (work.Width - hintWidth) / 2;
        return (cancel, new Win32.Rect { Left = hintLeft, Top = hintBottom - hintHeight, Right = hintLeft + hintWidth, Bottom = hintBottom });
    }

    private static (int X, int Y) ClampAreaPoint(int x, int y, int width, int height, Win32.Rect? activeLimit)
    {
        var limit = activeLimit ?? new Win32.Rect { Right = width, Bottom = height };
        return (Math.Clamp(x, limit.Left, limit.Right), Math.Clamp(y, limit.Top, limit.Bottom));
    }

    private static bool TryLatchAreaLimit(IReadOnlyList<Win32.Rect>? limits, Win32.Rect desktop,
        int width, int height, int x, int y, out Win32.Rect? activeLimit)
    {
        activeLimit = null;
        if (limits is null) return true;
        int screenX = desktop.Left + Math.Clamp((int)Math.Round(x * (double)desktop.Width / width), 0, desktop.Width - 1);
        int screenY = desktop.Top + Math.Clamp((int)Math.Round(y * (double)desktop.Height / height), 0, desktop.Height - 1);
        foreach (var monitor in limits)
        {
            var clipped = ClipWindow(monitor, desktop);
            if (screenX < clipped.Left || screenX >= clipped.Right || screenY < clipped.Top || screenY >= clipped.Bottom) continue;
            activeLimit = new Win32.Rect
            {
                Left = Math.Clamp((int)Math.Floor((clipped.Left - desktop.Left) * (double)width / desktop.Width), 0, width),
                Top = Math.Clamp((int)Math.Floor((clipped.Top - desktop.Top) * (double)height / desktop.Height), 0, height),
                Right = Math.Clamp((int)Math.Ceiling((clipped.Right - desktop.Left) * (double)width / desktop.Width), 0, width),
                Bottom = Math.Clamp((int)Math.Ceiling((clipped.Bottom - desktop.Top) * (double)height / desktop.Height), 0, height)
            };
            return true;
        }
        return false;
    }

    private static bool TryWindowSelection(WindowCaptureTarget target, Win32.Rect monitor, int sourceWidth, int sourceHeight, out PixelSelection selection)
    {
        selection = default;
        Win32.Rect clipped = ClipWindow(target.Bounds, monitor);
        if (clipped.Width <= 0 || clipped.Height <= 0 || monitor.Width <= 0 || monitor.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return false;
        int left = Math.Clamp((int)Math.Floor((clipped.Left - monitor.Left) * (double)sourceWidth / monitor.Width), 0, sourceWidth);
        int top = Math.Clamp((int)Math.Floor((clipped.Top - monitor.Top) * (double)sourceHeight / monitor.Height), 0, sourceHeight);
        int right = Math.Clamp((int)Math.Ceiling((clipped.Right - monitor.Left) * (double)sourceWidth / monitor.Width), 0, sourceWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((clipped.Bottom - monitor.Top) * (double)sourceHeight / monitor.Height), 0, sourceHeight);
        selection = new PixelSelection(left, top, right - left, bottom - top);
        return selection.Width > 0 && selection.Height > 0;
    }

    private static (WindowCaptureTarget Target, PixelSelection Selection)? ResolveWindowConfirmation(WindowCaptureTarget? target, Win32.Rect monitor, int width, int height) =>
        target is not null && TryWindowSelection(target, monitor, width, height, out var selection) ? (target, selection) : null;

    // Pure geometry and z-order checks. No HWND is created and no desktop input/pixels are used.
    internal static void VerifySyntheticWindowSelection()
    {
        var monitor = new Win32.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        var front = new WindowCaptureTarget(new nint(1), new Win32.Rect { Left = -1800, Top = 100, Right = -1000, Bottom = 700 }, "Front");
        var back = new WindowCaptureTarget(new nint(2), new Win32.Rect { Left = -1850, Top = 50, Right = -900, Bottom = 800 }, "Back");
        WindowCaptureTarget[] targets = [front, back];
        Require(ShouldConfirmWindowRelease(front, front) && !ShouldConfirmWindowRelease(front, back) &&
            !ShouldConfirmWindowRelease(null, front) && !ShouldConfirmWindowRelease(front, null),
            "window click confirms only a paired down/up on the same target");
        Require(HitTestWindow(targets, -1700, 200) == 0, "topmost target wins an overlap");
        Require(HitTestWindow(targets, -1840, 60) == 1, "uncovered lower target remains selectable");
        Require(HitTestWindow(targets, -1000, 200) == 1, "right boundary is exclusive");
        Require(HitTestWindow(targets, -50, 900) == -1, "empty desktop has no target");
        var edgeTarget = new WindowCaptureTarget(new nint(6), new Win32.Rect { Left = -100, Top = 0, Right = 0, Bottom = 500 }, "Display edge");
        Require(HitTestWindow([edgeTarget], 25, 200) == -1 && HitTestWindow([edgeTarget], -1, 200) == 0,
            "captured mouse release beyond the monitor is not clamped back onto its edge target");
        Require(TryWindowSelection(front, monitor, 1920, 1080, out var selection) && selection == new PixelSelection(120, 100, 800, 600), "window confirmation has a compatible source rectangle");
        var confirmation = ResolveWindowConfirmation(front, monitor, 1920, 1080);
        Require(confirmation is { } confirmed && ReferenceEquals(confirmed.Target, front) && confirmed.Target.Handle == new nint(1) && confirmed.Selection == selection,
            "confirmation preserves the selected native target separately from the compatibility crop");
        Require(ResolveWindowConfirmation(null, monitor, 1920, 1080) is null, "no hovered target cannot confirm");
        var spanning = new WindowCaptureTarget(new nint(3), new Win32.Rect { Left = -2200, Top = -200, Right = -1600, Bottom = 300 }, "Spanning");
        Require(TryWindowSelection(spanning, monitor, 1920, 1080, out selection) && selection == new PixelSelection(0, 0, 320, 300), "off-monitor bounds clip only the highlight/compatibility rectangle");
        Require(spanning.Bounds.Left == -2200 && spanning.Bounds.Top == -200, "whole-window capture target is preserved");
        var scaledMonitor = new Win32.Rect { Left = -200, Top = 0, Right = 200, Bottom = 200 };
        var scaledTarget = new WindowCaptureTarget(new nint(4), new Win32.Rect { Left = -100, Top = 50, Right = 100, Bottom = 150 }, "Scaled");
        Require(TryWindowSelection(scaledTarget, scaledMonitor, 800, 400, out selection) && selection == new PixelSelection(200, 100, 400, 200), "physical desktop coordinates map to source pixels");
        var outside = new WindowCaptureTarget(new nint(5), new Win32.Rect { Left = 100, Top = 100, Right = 400, Bottom = 400 }, "Other display");
        Require(!TryWindowSelection(outside, monitor, 1920, 1080, out _), "nonintersecting target cannot confirm");
        Require(NextWindowIndex(-1, 2, 1) == 0 && NextWindowIndex(1, 2, 1) == 0 && NextWindowIndex(0, 2, -1) == 1 && NextWindowIndex(0, 0, 1) == -1, "keyboard target cycling wraps without taking Tab from Cancel");
        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException($"Window selection regression: {scenario}.");
        }
    }

    internal static void VerifySyntheticMultiDisplaySelection()
    {
        var desktop = new Win32.Rect { Left = -1920, Top = -600, Right = 2560, Bottom = 1440 };
        var left = new Win32.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        var right = new Win32.Rect { Left = 0, Top = -600, Right = 2560, Bottom = 1440 };
        Win32.Rect[] displays = [left, right];
        int width = desktop.Width, height = desktop.Height;
        Require(ClampAreaPoint(4300, 200, width, height, null) == (4300, 200),
            "unanchored movement remains free across unequal physical displays");
        Require(TryLatchAreaLimit(displays, desktop, width, height, 100, 800, out var limit) &&
            limit is { Left: 0, Top: 600, Right: 1920, Bottom: 1680 }, "video anchor latches the containing negative-origin display");
        Require(ClampAreaPoint(4300, 0, width, height, limit) == (1920, 600) &&
            ClampAreaPoint(-100, height, width, height, limit) == (0, 1680), "drag and keyboard endpoints cannot leave the latched video display");
        Require(TryLatchAreaLimit(displays, desktop, width, height, 3000, 200, out limit) &&
            limit is { Left: 1920, Top: 0, Right: 4480, Bottom: 2040 } &&
            ClampAreaPoint(0, 1800, width, height, limit) == (1920, 1800), "a new anchor can latch the other display");
        Require(TryLatchAreaLimit(displays, desktop, width, height, 1920, 800, out limit) && limit?.Left == 1920,
            "shared display edges use the containing monitor's exclusive right boundary");
        Require(!TryLatchAreaLimit(displays, desktop, width, height, 100, 100, out _), "a keyboard anchor in a monitor gap cannot start video selection");
        Require(TryLatchAreaLimit(null, desktop, width, height, 100, 100, out limit) && limit is null &&
            ClampAreaPoint(width, height, width, height, limit) == (width, height), "screenshots may span the full frozen virtual canvas, including gaps");
        var target = new WindowCaptureTarget(new nint(10), new Win32.Rect { Left = -1500, Top = 200, Right = 500, Bottom = 900 }, "Across displays");
        Require(TryWindowSelection(target, desktop, width, height, out var selection) && selection == new PixelSelection(420, 800, 2000, 700),
            "a spanning window highlight maps once into the unscaled virtual bitmap");
        var primaryWork = new Win32.Rect { Left = 0, Top = -600, Right = 2560, Bottom = 1400 };
        var (cancel, hint) = PlaceControls(desktop, primaryWork, 1.5);
        Require(InsideWork(cancel, primaryWork) && InsideWork(hint, primaryWork) && cancel.Width == 168 && cancel.Height == 60,
            "controls stay on the initiating display and use its own DPI");
        var (negativeCancel, negativeHint) = PlaceControls(desktop, left, 2);
        Require(InsideWork(negativeCancel, left) && InsideWork(negativeHint, left) && negativeCancel.Width == 224,
            "controls stay inside a negative-origin initiating display");
        var tinyWork = new Win32.Rect { Left = 100, Top = 100, Right = 110, Bottom = 108 };
        var (tinyCancel, tinyHint) = PlaceControls(desktop, tinyWork, 2);
        Require(InsideWork(tinyCancel, tinyWork) && InsideWork(tinyHint, tinyWork), "controls clamp to a small available work area");
        var large = new Win32.Rect { Left = -25000, Top = -5000, Right = 25000, Bottom = 5000 };
        Require(ScreenToViewport(large, new Win32.Point { X = 15000, Y = -1234 }) == (40000, 3766),
            "screen pointer coordinates do not wrap through a signed 16-bit client position");
        var movable = new PixelSelection(100, 700, 400, 300);
        var moveLimit = new Win32.Rect { Left = 0, Top = 600, Right = 1920, Bottom = 1680 };
        Require(ClampMovedArea(movable, 500, -50, moveLimit) == new PixelSelection(600, 650, 400, 300),
            "moving preserves area dimensions and pointer offset");
        Require(ClampMovedArea(movable, -2000, 3000, moveLimit) == new PixelSelection(0, 1380, 400, 300),
            "moving clamps the entire selection to its originating display");
        Require(ClampMovedArea(movable, 3000, -3000, moveLimit) == new PixelSelection(1520, 600, 400, 300),
            "moving clamps against the opposite monitor edges");
        Require(Contains(movable, 100, 700) && Contains(movable, 499, 999) && !Contains(movable, 500, 999),
            "only points inside the selected area start a move");
        VerifySyntheticMoveLifecycle();

        bool InsideWork(Win32.Rect viewport, Win32.Rect work) => viewport.Left + desktop.Left >= work.Left &&
            viewport.Right + desktop.Left <= work.Right && viewport.Top + desktop.Top >= work.Top && viewport.Bottom + desktop.Top <= work.Bottom;
        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException("Multi-display selection regression: " + scenario + ".");
        }
    }

    private static void VerifySyntheticMoveLifecycle()
    {
        const int width = 320, height = 200;
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        var display = CapturedDisplay.FromOpaqueBgra(width, height, pixels);
        var bounds = new Win32.Rect { Left = -320, Top = -200, Right = 0, Bottom = 0 };
        var window = new SelectionWindow(display, bounds, pixels, areaLimits: [bounds], confirmSelection: true);
        var changes = new List<bool>();
        int focusRequests = 0, closes = 0;
        bool aliveWhenClosing = false;
        window.SelectionChanged += changes.Add;
        window.ToolbarRequested += () => focusRequests++;
        window.Closing += () => { closes++; aliveWhenClosing = window.Handle != 0; };
        try
        {
            window.HandleKey(0x09); window.HandleKey(0x75);
            if (focusRequests != 2) throw new InvalidOperationException("Keyboard navigation could not reach the recording toolbar.");
            window.SetCurrent(20, 30);
            if (!window.TryAnchorCurrent()) throw new InvalidOperationException("Could not anchor generated recording selection.");
            window.SetCurrent(120, 90);
            window.SetSelectionReady(true);
            if (!window.HasSelection || changes.Count != 1 || !changes[0])
                throw new InvalidOperationException("Recording selection did not become ready.");
            window._moveStart = window.CurrentSelection();
            window._moveOriginX = 30; window._moveOriginY = 40;
            window._moving = window._dragging = true;
            window.NotifySelectionChanged();
            window.MoveSelectedArea(310, 190);
            window.ConfirmSelection();
            if (window.HasSelection || window._complete || changes[^1])
                throw new InvalidOperationException("A moving selection could be confirmed or left Record enabled.");
            window.ClearPointerCapture();
            window.NotifySelectionChanged();
            if (!window.HasSelection || !changes[^1] || window.CurrentSelection() != new PixelSelection(220, 140, 100, 60))
                throw new InvalidOperationException("Moving selection did not retain its dimensions and clamp to the display.");
            window.ConfirmSelection();
            if (!window._completion.Task.IsCompletedSuccessfully || window.CurrentSelection() != new PixelSelection(220, 140, 100, 60)
                || closes != 1 || !aliveWhenClosing)
                throw new InvalidOperationException("Record did not confirm the moved selection.");
        }
        finally { window.Cancel(); }

        var target = new WindowCaptureTarget(new nint(1), new Win32.Rect { Left = -300, Top = -160, Right = -120, Bottom = -60 }, "Generated window");
        var picker = new SelectionWindow(display, bounds, pixels, windows: [target], areaLimits: [bounds], confirmSelection: true);
        try
        {
            picker._windowMode = true; picker._windowIndex = 0;
            picker.ConfirmWindow();
            picker._windowIndex = -1; // Moving the pointer to the toolbar must not lose the chosen HWND.
            if (!picker.HasSelection || picker._complete || !ReferenceEquals(picker.SelectedWindow, target))
                throw new InvalidOperationException("Window selection was not retained for Record.");
            picker.ConfirmSelection();
            if (!picker._completion.Task.IsCompletedSuccessfully || !ReferenceEquals(picker.SelectedWindow, target))
                throw new InvalidOperationException("Record did not confirm the selected window.");
        }
        finally { picker.Cancel(); }
    }
    private PixelSelection CurrentSelection() => new(Math.Min(_anchorX, _currentX), Math.Min(_anchorY, _currentY), Math.Abs(_anchorX - _currentX), Math.Abs(_anchorY - _currentY));
    private int X(int source) => (int)Math.Round(source * (double)Width / _capture.Width);
    private int Y(int source) => (int)Math.Round(source * (double)Height / _capture.Height);
    private static int SignedLow(nint value) => unchecked((short)((long)value & 0xFFFF));
    private static int SignedHigh(nint value) => unchecked((short)(((long)value >> 16) & 0xFFFF));

    private void ChangeVisuals(Action change)
    {
        InvalidateMarks(); change(); InvalidateMarks();
    }

    private void InvalidateMarks()
    {
        if (_window == 0) return;
        if (!_windowMode && !_selectionReady)
        {
            int x = X(_currentX), y = Y(_currentY);
            Invalidate(x - 10, y - 10, x + 11, y + 11);
        }
        if (MarkBounds() is not { } mark) return;
        int left = mark.Left, top = mark.Top, right = mark.Right, bottom = mark.Bottom;
        // Separate border strips preserve a complex dirty region; never repaint its whole interior.
        Invalidate(left - 3, top - 3, right + 4, top + 4);
        Invalidate(left - 3, bottom - 3, right + 4, bottom + 4);
        Invalidate(left - 3, top, left + 4, bottom);
        Invalidate(right - 3, top, right + 4, bottom);
    }

    private void Invalidate(int left, int top, int right, int bottom)
    {
        var rect = new Win32.Rect { Left = Math.Clamp(left, 0, Width), Top = Math.Clamp(top, 0, Height), Right = Math.Clamp(right, 0, Width), Bottom = Math.Clamp(bottom, 0, Height) };
        if (rect.Width > 0 && rect.Height > 0) Native.InvalidateRect(_window, in rect, false);
    }

    private void Paint()
    {
        nint dirty = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Native.GetUpdateRgn(_window, dirty, false);
            nint target = Native.BeginPaint(_window, out var paint);
            try
            {
                PaintBuffer(dirty);
                GdiCaptureNative.BitBlt(target, 0, 0, Width, Height, _buffer.Dc, 0, 0, GdiCaptureNative.SrcCopy);
            }
            finally { Native.EndPaint(_window, in paint); }
        }
        finally { GdiCaptureNative.DeleteObject(dirty); }
    }

    private void PaintBuffer(nint dirty)
    {
        int saved = Native.SaveDC(_buffer.Dc);
        try
        {
            if (dirty != 0) Native.SelectClipRgn(_buffer.Dc, dirty);
            if (_source.Width == Width && _source.Height == Height)
                GdiCaptureNative.BitBlt(_buffer.Dc, 0, 0, Width, Height, _source.Dc, 0, 0, GdiCaptureNative.SrcCopy);
            else GdiCaptureNative.StretchBlt(_buffer.Dc, 0, 0, Width, Height, _source.Dc, 0, 0, _source.Width, _source.Height, GdiCaptureNative.SrcCopy);
            Native.SelectObject(_buffer.Dc, Native.GetStockObject(5)); // NULL_BRUSH: selection has no fill or dimming.
            for (int pass = 0; pass < 2; pass++)
            {
                Native.SelectObject(_buffer.Dc, pass == 0 ? _blackPen : _whitePen);
                if (MarkBounds() is { } mark)
                {
                    Native.Rectangle(_buffer.Dc, mark.Left, mark.Top, mark.Right, mark.Bottom);
                }
                if (!_windowMode && !_selectionReady)
                {
                    int x = Math.Clamp(X(_currentX), 0, Width - 1), y = Math.Clamp(Y(_currentY), 0, Height - 1);
                    Native.MoveToEx(_buffer.Dc, x - 7, y, 0); Native.LineTo(_buffer.Dc, x + 8, y);
                    Native.MoveToEx(_buffer.Dc, x, y - 7, 0); Native.LineTo(_buffer.Dc, x, y + 8);
                }
            }
            if (_windows.Count > 0 && (dirty == 0 || Native.RectInRegion(dirty, in _modeHintBounds))) DrawModeHint();
        }
        finally { Native.RestoreDC(_buffer.Dc, saved); }
    }

    private int Dip(double value) => (int)Math.Round(value * _scale);

    private void InvalidateModeHint()
    {
        if (_windows.Count > 0) Invalidate(_modeHintBounds.Left, _modeHintBounds.Top, _modeHintBounds.Right, _modeHintBounds.Bottom);
    }

    private void DrawModeHint()
    {
        if (_modeHintBounds.Width <= 0 || _modeHintBounds.Height <= 0) return;
        ButtonPalette palette = ReadButtonPalette();
        int saved = Native.SaveDC(_buffer.Dc);
        nint brush = Native.CreateSolidBrush(palette.Fill), pen = Native.CreatePen(0, 1, palette.Stroke);
        try
        {
            if (brush == 0 || pen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SelectObject(_buffer.Dc, brush); Native.SelectObject(_buffer.Dc, pen);
            Native.RoundRect(_buffer.Dc, _modeHintBounds.Left, _modeHintBounds.Top, _modeHintBounds.Right, _modeHintBounds.Bottom, Dip(10), Dip(10));
            Native.SetBkMode(_buffer.Dc, 1); Native.SetTextColor(_buffer.Dc, palette.Text); Native.SelectObject(_buffer.Dc, _hintFont);
            var text = _modeHintBounds;
            Native.DrawText(_buffer.Dc, _windowMode ? "Click a window · Space for area" : "Space for window", -1, ref text, 0x00000001 | 0x00000004 | 0x00000020 | 0x00000800);
        }
        finally
        {
            Native.RestoreDC(_buffer.Dc, saved);
            GdiCaptureNative.DeleteObject(brush); GdiCaptureNative.DeleteObject(pen);
        }
    }

    private void DrawCancel(Native.DrawItem item)
    {
        // Keep the rounded corners identical to the captured pixels underneath.
        // The standard BUTTON still supplies focus, Invoke/UIA and click behavior.
        GdiCaptureNative.BitBlt(item.Dc, item.Bounds.Left, item.Bounds.Top, item.Bounds.Width, item.Bounds.Height,
            _buffer.Dc, _cancelBounds.Left, _cancelBounds.Top, GdiCaptureNative.SrcCopy);
        DrawCancelSurface(item.Dc, item.Bounds, item.State, _cancelHovered, _scale, _cancelFont, _hintFont, ReadButtonPalette());
    }

    private readonly record struct ButtonPalette(uint Fill, uint Hover, uint Pressed, uint Text, uint Hint, uint Stroke, uint Focus,
        bool HighContrast = false, uint HighlightText = 0);

    private static ButtonPalette ReadButtonPalette()
    {
        if (Win32.IsHighContrast)
        {
            uint face = Native.GetSysColor(15), text = Native.GetSysColor(18);
            uint highlight = Native.GetSysColor(13), highlightText = Native.GetSysColor(14);
            return new ButtonPalette(face, highlight, highlight, text, text, text, highlight, true, highlightText);
        }
        uint fill = ThemeColor("ControlSolidFillColorDefaultBrush", Native.GetSysColor(15));
        return new ButtonPalette(fill,
            ThemeColor("ControlFillColorSecondaryBrush", fill), ThemeColor("ControlFillColorTertiaryBrush", fill),
            ThemeColor("TextFillColorPrimaryBrush", Native.GetSysColor(18), fill),
            ThemeColor("TextFillColorSecondaryBrush", Native.GetSysColor(18), fill),
            ThemeColor("ControlStrokeColorDefaultBrush", Native.GetSysColor(6), fill),
            ThemeColor("FocusStrokeColorOuterBrush", Native.GetSysColor(13), fill));
    }

    private static uint ThemeColor(string key, uint fallback, uint? backdrop = null)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out object value) && value is SolidColorBrush brush)
            {
                var color = brush.Color;
                uint basis = backdrop ?? fallback;
                uint red = (uint)((color.R * color.A + (basis & 255) * (255 - color.A) + 127) / 255);
                uint green = (uint)((color.G * color.A + ((basis >> 8) & 255) * (255 - color.A) + 127) / 255);
                uint blue = (uint)((color.B * color.A + ((basis >> 16) & 255) * (255 - color.A) + 127) / 255);
                return red | (green << 8) | (blue << 16);
            }
        }
        catch (Exception error) { Trace.WriteLine(error); }
        return fallback;
    }

    private static nint CreateUiFont(int size, double scale)
    {
        nint font = Native.CreateFont(-(int)Math.Round(size * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI Variable Text");
        if (font == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return font;
    }

    private static void DrawCancelSurface(nint dc, Win32.Rect bounds, uint state, bool hovered, double scale,
        nint font, nint hintFont, ButtonPalette palette)
    {
        int D(double value) => (int)Math.Round(value * scale);
        bool pressed = (state & OwnerDrawPressed) != 0;
        bool focused = (state & OwnerDrawFocused) != 0 && (state & OwnerDrawHideFocus) == 0;
        uint fill = pressed ? palette.Pressed : hovered ? palette.Hover : palette.Fill;
        uint textColor = palette.HighContrast && (hovered || pressed) ? palette.HighlightText : palette.Text;
        uint hintColor = palette.HighContrast ? textColor : palette.Hint;
        int saved = Native.SaveDC(dc);
        nint brush = Native.CreateSolidBrush(fill), pen = Native.CreatePen(0, Math.Max(1, D(1)), palette.Stroke);
        nint focusPen = 0;
        try
        {
            if (brush == 0 || pen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var body = new Win32.Rect { Left = bounds.Left + D(3), Top = bounds.Top + D(3), Right = bounds.Right - D(3), Bottom = bounds.Bottom - D(3) };
            Native.SelectObject(dc, brush); Native.SelectObject(dc, pen);
            Native.RoundRect(dc, body.Left, body.Top, body.Right, body.Bottom, D(14), D(14));
            if (focused)
            {
                focusPen = Native.CreatePen(0, Math.Max(1, D(1)), palette.Focus);
                Native.SelectObject(dc, Native.GetStockObject(5)); Native.SelectObject(dc, focusPen);
                Native.RoundRect(dc, bounds.Left + D(1), bounds.Top + D(1), bounds.Right - D(1), bounds.Bottom - D(1), D(18), D(18));
            }
            Native.SetBkMode(dc, 1);
            Native.SelectObject(dc, font);
            Native.SetTextColor(dc, textColor);
            var label = new Win32.Rect { Left = body.Left + D(12), Top = body.Top, Right = body.Right - D(35), Bottom = body.Bottom };
            Native.DrawText(dc, "Cancel", -1, ref label, 0x00000004 | 0x00000020 | 0x00000800);
            Native.SelectObject(dc, hintFont);
            Native.SetTextColor(dc, hintColor);
            var hint = new Win32.Rect { Left = body.Right - D(34), Top = body.Top, Right = body.Right - D(11), Bottom = body.Bottom };
            Native.DrawText(dc, "Esc", -1, ref hint, 0x00000002 | 0x00000004 | 0x00000020 | 0x00000800);
        }
        finally
        {
            Native.RestoreDC(dc, saved);
            GdiCaptureNative.DeleteObject(brush); GdiCaptureNative.DeleteObject(pen);
            if (focusPen != 0) GdiCaptureNative.DeleteObject(focusPen);
        }
    }

    // Pure generated bitmap: normal, hover and keyboard-focus states. No HWND is
    // shown and no desktop pixels are acquired to produce this visual fixture.
    internal static async Task RenderCancelAppearanceAsync(string outputPath)
    {
        const int width = 360, height = 52;
        ButtonPalette palette = ReadButtonPalette();
        uint background = ThemeColor("SolidBackgroundFillColorBaseBrush", Native.GetSysColor(15));
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)(background >> 16); pixels[offset + 1] = (byte)(background >> 8);
            pixels[offset + 2] = (byte)background; pixels[offset + 3] = 255;
        }
        using var bitmap = NativeCaptureBitmap.FromBgra(width, height, pixels);
        nint font = 0, hint = 0;
        try
        {
            font = CreateUiFont(13, 1); hint = CreateUiFont(11, 1);
            for (int index = 0; index < 3; index++)
            {
                var bounds = new Win32.Rect { Left = index * 120 + 4, Top = 6, Right = index * 120 + 116, Bottom = 46 };
                DrawCancelSurface(bitmap.Dc, bounds, index == 2 ? 16u : 0u, index == 1, 1, font, hint, palette);
            }
            GdiCaptureNative.GdiFlush();
            byte[] actual = bitmap.CopyOpaqueBgra();
            if (!actual.AsSpan(0, 4).SequenceEqual(pixels.AsSpan(0, 4)))
                throw new InvalidOperationException("Cancel drawing changed its background corners.");
            using var image = ImageDocument.Import(ImageDescriptor.Rgba(width, height, width * 4),
                CapturedDisplay.FromOpaqueBgra(width, height, actual).Rgba);
            await File.WriteAllBytesAsync(outputPath, image.ExportPng());
        }
        finally
        {
            if (font != 0) GdiCaptureNative.DeleteObject(font);
            if (hint != 0) GdiCaptureNative.DeleteObject(hint);
        }
    }

    private void Finish(PixelSelection? selection)
    {
        if (_complete) return;
        _complete = true;
        NotifyClosing();
        ClearPointerCapture();
        _completion.TrySetResult(selection);
        _buttonHook?.Dispose(); _buttonHook = null;
        if (_window != 0) Native.DestroyWindow(_window);
        DisposeResources();
    }

    private void NotifyClosing()
    {
        if (_closingNotified) return;
        _closingNotified = true;
        try { Closing?.Invoke(); }
        catch (Exception error)
        {
            try { Ovi.Screenshot.Services.AppLog.Shared.Write(error); }
            catch { Trace.WriteLine(error); }
        }
    }
    private void Fail(Exception error)
    {
        _completion.TrySetException(error); Finish(null);
    }
    private void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _buttonHook?.Dispose(); _buttonHook = null;
        _source?.Dispose(); _buffer?.Dispose();
        if (_blackPen != 0) GdiCaptureNative.DeleteObject(_blackPen);
        if (_whitePen != 0) GdiCaptureNative.DeleteObject(_whitePen);
        if (_cancelFont != 0) GdiCaptureNative.DeleteObject(_cancelFont);
        if (_hintFont != 0) GdiCaptureNative.DeleteObject(_hintFont);
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
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
        [StructLayout(LayoutKind.Sequential)] internal struct DrawItem
        {
            public uint Type, ControlId, ItemId, Action, State;
            public nint Window, Dc;
            public Win32.Rect Bounds;
            public nuint Data;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseTracking { public uint Size, Flags; public nint Window; public uint HoverTime; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(in WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint SetFocus(nint window);
        [DllImport("user32.dll")] internal static extern nint SetCapture(nint window);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetCapture();
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadCursor(nint instance, nint name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(nint window, string text);
        [DllImport("user32.dll")] internal static extern void NotifyWinEvent(uint eventId, nint window, int objectId, int childId);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, in Win32.Rect rectangle, bool erase);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, nint rectangle, bool erase);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint window);
        [DllImport("user32.dll")] internal static extern int GetUpdateRgn(nint window, nint region, bool erase);
        [DllImport("user32.dll")] internal static extern nint BeginPaint(nint window, out PaintStruct paint);
        [DllImport("user32.dll")] internal static extern bool EndPaint(nint window, in PaintStruct paint);
        [DllImport("user32.dll")] internal static extern bool TrackMouseEvent(ref MouseTracking tracking);
        [DllImport("user32.dll")] internal static extern uint GetSysColor(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(nint dc, string text, int count, ref Win32.Rect rectangle, uint format);
        [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern int SelectClipRgn(nint dc, nint region);
        [DllImport("gdi32.dll")] internal static extern bool RectInRegion(nint region, in Win32.Rect rectangle);
        [DllImport("gdi32.dll")] internal static extern int SaveDC(nint dc);
        [DllImport("gdi32.dll")] internal static extern bool RestoreDC(nint dc, int state);
        [DllImport("gdi32.dll")] internal static extern nint GetStockObject(int index);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreatePen(int style, int width, uint color);
        [DllImport("gdi32.dll")] internal static extern bool Rectangle(nint dc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern bool MoveToEx(nint dc, int x, int y, nint old);
        [DllImport("gdi32.dll")] internal static extern bool LineTo(nint dc, int x, int y);
        [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] internal static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc, int mode);
        [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc, uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string faceName);
    }
}
