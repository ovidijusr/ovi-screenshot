using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Native;

internal sealed record CaptureShortcut(uint Modifiers, uint Key, string Label)
{
    internal static CaptureShortcut Parse(string text)
    {
        uint modifiers = 0;
        uint key = 0;
        string? keyLabel = null;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= 2; break;
                case "SHIFT": modifiers |= 4; break;
                case "ALT": modifiers |= 1; break;
                case "WIN": case "WINDOWS": case "CMD": case "COMMAND": modifiers |= 8; break;
                default:
                    if (key != 0) throw new ArgumentException("Choose one key, such as F17 or Ctrl+Shift+4.");
                    if (part.Length > 1 && char.ToUpperInvariant(part[0]) == 'F' && uint.TryParse(part.AsSpan(1), out uint function) && function is >= 1 and <= 24)
                    {
                        if (function == 12) throw new ArgumentException("F12 is reserved by Windows. Choose another key.");
                        key = 0x70 + function - 1;
                        keyLabel = $"F{function}";
                    }
                    else if (part.Replace(" ", "").ToUpperInvariant() is "PRINTSCREEN" or "PRTSC" or "PRTSCN" or "SNAPSHOT")
                    {
                        key = 0x2c;
                        keyLabel = "PrintScreen";
                    }
                    else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                    {
                        key = char.ToUpperInvariant(part[0]);
                        keyLabel = ((char)key).ToString();
                    }
                    else if (part.StartsWith("Key0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(part.AsSpan(5), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out uint virtualKey) && virtualKey is > 6 and < 255)
                    {
                        key = virtualKey;
                        keyLabel = $"Key0x{key:X2}";
                    }
                    else throw new ArgumentException("Choose a function key such as F17, PrintScreen, or Ctrl+Shift+4.");
                    break;
            }
        }
        bool standalone = key is >= 0x70 and <= 0x87 || key == 0x2c || key is >= 0xa6 and <= 0xb7;
        if (key == 0 || key == 0x7b || (modifiers == 0 && !standalone))
            throw new ArgumentException("Use a function key such as F17, or add Ctrl, Shift, or Alt to a letter or digit.");
        string label = string.Join("+", new[] { (modifiers & 2) != 0 ? "Ctrl" : null, (modifiers & 4) != 0 ? "Shift" : null,
            (modifiers & 1) != 0 ? "Alt" : null, (modifiers & 8) != 0 ? "Win" : null, keyLabel }.Where(part => part != null));
        return new CaptureShortcut(modifiers, key, label);
    }

    internal static CaptureShortcut FromKeyCode(uint modifiers, uint key)
    {
        string keyName = key switch
        {
            >= 0x70 and <= 0x87 => $"F{key - 0x70 + 1}",
            0x2c => "PrintScreen",
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a => ((char)key).ToString(),
            _ => $"Key0x{key:X2}"
        };
        string prefix = string.Concat((modifiers & 2) != 0 ? "Ctrl+" : "", (modifiers & 4) != 0 ? "Shift+" : "",
            (modifiers & 1) != 0 ? "Alt+" : "", (modifiers & 8) != 0 ? "Win+" : "");
        return Parse(prefix + keyName);
    }
}

internal sealed class DesktopIntegration : IDisposable
{
    private const uint TrayMessage = Win32.WmApp + 5;
    private readonly nint _window;
    private readonly WindowHook _hook;
    private readonly Action _capture, _settings, _quit, _reopen;
    private readonly Action? _record;
    private readonly Action<Exception> _onError;
    private readonly TrayIcon _trayIcon;
    private TrayMenuWindow? _trayMenu;
    private readonly uint _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
    private Win32.NotifyIconData _icon;
    private bool _hotkey;
    private bool _version4, _disposed, _recording;
    private CaptureShortcut? _registeredShortcut;
    private bool _shortcutPaused;
    private GlobalCaptureShortcut? _directShortcut;
    private int _hotkeyId = 1;

    internal DesktopIntegration(nint window, Action capture, Action settings, Action quit, Action reopen, Action<Exception> onError, Action? record = null)
    {
        _window = window; _capture = capture; _settings = settings; _quit = quit; _reopen = reopen; _record = record; _onError = onError;
        _trayIcon = new TrayIcon();
        _icon = new Win32.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<Win32.NotifyIconData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, CallbackMessage = TrayMessage,
            Icon = _trayIcon.Handle, Tooltip = "Ovi Screenshot", Info = "", InfoTitle = ""
        };
        _hook = new WindowHook(window, HandleMessage, onError);
        try { AddTrayIcon(); }
        catch { _hook.Dispose(); _trayIcon.Dispose(); throw; }
    }

    internal void Register(CaptureShortcut shortcut)
    {
        if (_shortcutPaused) throw new InvalidOperationException("Finish setting the key before applying a shortcut.");
        InstallShortcut(shortcut);
    }

    private void InstallShortcut(CaptureShortcut shortcut)
    {
        if ((_hotkey || _directShortcut?.Enabled == true) && _registeredShortcut == shortcut) return;
        int newId = _hotkeyId == 1 ? 2 : 1;
        bool registered = Win32.RegisterHotKey(_window, newId, shortcut.Modifiers | 0x4000, shortcut.Key);
        GlobalCaptureShortcut? direct = null;
        if (!registered)
        {
            int error = Marshal.GetLastWin32Error();
            // The user confirmed this exact Mac screenshot macro. Windows owns its taskbar chord.
            // Scope direct handling to that binding; all other conflicts keep their existing error.
            if (error == 1409 && shortcut.Modifiers == 12 && shortcut.Key == '3')
                direct = new GlobalCaptureShortcut(shortcut, DispatcherQueue.GetForCurrentThread(), _capture);
            else
                throw new InvalidOperationException($"{shortcut.Label} is already in use or unavailable. Choose another shortcut. ({new Win32Exception(error).Message})");
        }
        // Prepare and enable the replacement first so a failure preserves the previous binding.
        if (direct is not null)
        {
            direct.Failed += error =>
            {
                if (_disposed || !ReferenceEquals(_directShortcut, direct)) return;
                _onError(error); _settings();
            };
            try { direct.Enabled = true; }
            catch { direct.Dispose(); throw; }
        }
        if (_hotkey) Win32.UnregisterHotKey(_window, _hotkeyId);
        _directShortcut?.Dispose();
        _directShortcut = direct;
        _hotkeyId = newId; _hotkey = registered; _registeredShortcut = shortcut;
    }

    internal void PauseShortcut(bool paused)
    {
        if (_disposed) return;
        if (paused)
        {
            if (_shortcutPaused && !HasActiveShortcut) return;
            try
            {
                if (_directShortcut is not null) _directShortcut.Enabled = false;
                if (_hotkey)
                {
                    if (!Win32.UnregisterHotKey(_window, _hotkeyId))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not pause the capture shortcut.");
                    _hotkey = false;
                }
            }
            finally { _shortcutPaused = !HasActiveShortcut; }
            return;
        }

        if (!_shortcutPaused && HasActiveShortcut) return;
        try
        {
            if (_directShortcut is not null) _directShortcut.Enabled = true;
            else if (_registeredShortcut is { } shortcut) InstallShortcut(shortcut);
            _shortcutPaused = false;
        }
        catch { _shortcutPaused = true; throw; }
    }

    private bool HasActiveShortcut => _hotkey || _directShortcut?.Enabled == true;

    internal void SetRecording(bool recording)
    {
        if (_disposed) return;
        _recording = recording;
        _icon.Tooltip = recording ? "Ovi Screenshot · Recording" : "Ovi Screenshot";
        if (!Win32.Shell_NotifyIcon(1, ref _icon))
            throw new InvalidOperationException("Could not update the capture icon.");
    }

    private void AddTrayIcon()
    {
        if (!Win32.Shell_NotifyIcon(0, ref _icon))
            throw new InvalidOperationException("Could not add the screenshot icon to the notification area.");
        _icon.TimeoutOrVersion = 4;
        _version4 = Win32.Shell_NotifyIcon(4, ref _icon);
    }

    private nint? HandleMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated) { AddTrayIcon(); return 0; }
        if (message == Win32.WmHotkey && _hotkey && !_shortcutPaused && (int)wParam == _hotkeyId) { _capture(); return 0; }
        if (message != TrayMessage) return null;
        uint notification = (uint)((long)lParam & 0xffff);
        // Version 4 sends one NIN_SELECT for a single activation, and NIN_KEYSELECT for keyboard use.
        if ((_version4 && notification is 0x0400 or 0x0401) || (!_version4 && notification == 0x0202)) _capture();
        if ((_version4 && notification == 0x007B) || (!_version4 && notification == 0x0205)) OpenMenu();
        return 0;
    }

    private void OpenMenu()
    {
        if (_disposed) return;
        if (!Win32.GetCursorPos(out var point)) return;
        if (_trayMenu is null)
        {
            _trayMenu = new TrayMenuWindow(_capture, _record, _reopen, _settings, _quit);
            _trayMenu.Failed += error => { _onError(error); _settings(); };
        }
        _trayMenu.ShowAt(point, _recording);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _directShortcut?.Dispose(); _directShortcut = null;
        if (_hotkey) Win32.UnregisterHotKey(_window, _hotkeyId);
        Win32.Shell_NotifyIcon(2, ref _icon);
        _trayMenu?.Dispose();
        _hook.Dispose();
        _trayIcon.Dispose();
    }
}
