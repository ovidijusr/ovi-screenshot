using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Ovi.Screenshot.Native;

// Explicit one-shot learning. After focus loss, only the selected chord's releases may drain briefly.
// No text or key history is stored.
internal sealed class ShortcutKeyCapture : IDisposable
{
    private readonly nint _owner;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<CaptureShortcut> _captured;
    private readonly Action<string?> _cancelled;
    private readonly HookProcedure _procedure;
    private readonly DispatcherQueueTimer _timeout;
    private readonly DispatcherQueueTimer _releaseTimeout;
    private readonly ShortcutCaptureState _state;
    private bool _disposed, _finishing;
    private nint _hook;

    internal bool HasPendingShortcut => _state.HasPendingShortcut;

    internal ShortcutKeyCapture(nint owner, DispatcherQueue dispatcher, Action<CaptureShortcut> captured, Action<string?> cancelled)
    {
        _owner = owner; _dispatcher = dispatcher; _captured = captured; _cancelled = cancelled;
        // Sample only modifier state at the explicit start, not asynchronous key state in the LL hook.
        uint[] modifierKeys = [0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0x5b, 0x5c];
        _state = new ShortcutCaptureState(modifierKeys.Where(key => GetAsyncKeyState((int)key) < 0));
        _procedure = HandleKey;
        _timeout = dispatcher.CreateTimer();
        _timeout.Interval = TimeSpan.FromSeconds(15);
        _timeout.IsRepeating = false;
        _timeout.Tick += Timeout;
        _releaseTimeout = dispatcher.CreateTimer();
        _releaseTimeout.Interval = TimeSpan.FromSeconds(2);
        _releaseTimeout.IsRepeating = false;
        _releaseTimeout.Tick += ReleaseTimeout;
        _hook = SetWindowsHookEx(13, _procedure, GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not listen for a shortcut.");
        _timeout.Start();
    }

    private nint HandleKey(int code, nuint message, nint data)
    {
        if (code != 0 || _disposed || _finishing) return CallNextHookEx(_hook, code, message, data);
        try
        {
            bool down = message is 0x0100 or 0x0104;
            bool up = message is 0x0101 or 0x0105;
            if (!down && !up) return CallNextHookEx(_hook, code, message, data);
            KeyData key = Marshal.PtrToStructure<KeyData>(data);
            bool hadPending = HasPendingShortcut;
            ShortcutLearningStep step = _state.Process(key.Key, key.ScanCode, key.Flags, down, up, IsOwnerForeground());
            if (step.Finished) Finish(step.Shortcut, step.Message);
            else if (!hadPending && HasPendingShortcut) StartReleaseDeadline();
            return step.Consume ? 1 : CallNextHookEx(_hook, code, message, data);
        }
        catch
        {
            Finish(null, "Could not read the shortcut. Try again.");
            return CallNextHookEx(_hook, code, message, data);
        }
    }

    internal void OnOwnerDeactivated()
    {
        if (_disposed || _finishing || IsOwnerForeground()) return;
        if (HasPendingShortcut) StartReleaseDeadline();
        else Finish(null, "Keep Settings open, then try Set key again.");
    }

    private bool IsOwnerForeground()
    {
        nint foreground = Win32.GetForegroundWindow();
        if (foreground == 0) return false;
        if (foreground == _owner) return true;
        nint root = GetAncestor(_owner, 2), rootOwner = GetAncestor(_owner, 3);
        return (root != 0 && GetAncestor(foreground, 2) == root) ||
            (rootOwner != 0 && GetAncestor(foreground, 3) == rootOwner);
    }

    private void StartReleaseDeadline()
    {
        _timeout.Stop();
        if (!_releaseTimeout.IsRunning) _releaseTimeout.Start();
    }

    private void Timeout(DispatcherQueueTimer sender, object args) => Finish(null, "No keyboard key detected. Try Fn with that key, or choose a function key.");
    private void ReleaseTimeout(DispatcherQueueTimer sender, object args) => Finish(null, "Key release wasn’t detected. Release the keys, then try again.");

    private void Finish(CaptureShortcut? shortcut, string? message)
    {
        if (_disposed || _finishing) return;
        _finishing = true;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            Dispose();
            if (shortcut is not null) _captured(shortcut); else _cancelled(message);
        });
    }

    // Runs only synthetic state transitions: no hook, OS input injection, or key logging.
    internal static void VerifySyntheticLearning()
    {
        var snapshot = new ShortcutCaptureState();
        ShortcutLearningStep result = snapshot.Process(0x2c, 0x37, 0x80, false, true, true);
        Require(result.Finished && result.Shortcut?.Label == "PrintScreen", "key-up-only PrintScreen");

        var functionRelease = new ShortcutCaptureState();
        result = functionRelease.Process(0x80, 0, 0x80, false, true, true);
        Require(result.Shortcut?.Label == "F17", "key-up-only function key");

        var heldControl = new ShortcutCaptureState([0xa2]);
        result = heldControl.Process(0x80, 0, 0, true, false, true);
        Require(!result.Finished && heldControl.Pending?.Label == "Ctrl+F17", "already-held modifier");
        result = heldControl.Process(0x80, 0, 0x80, false, true, true);
        Require(!result.Finished, "wait for held modifier release");
        result = heldControl.Process(0x11, 0x1d, 0x80, false, true, true);
        Require(result.Shortcut?.Label == "Ctrl+F17" && !result.Consume, "normalize and pass pre-existing modifier release");

        var chord = new ShortcutCaptureState();
        chord.Process(0xa0, 0x2a, 0, true, false, true);
        chord.Process(0xa2, 0x1d, 0, true, false, true);
        result = chord.Process(0x34, 0x05, 0, true, false, true);
        Require(!result.Finished && chord.Pending?.Label == "Ctrl+Shift+4", "modifier chord");
        chord.Process(0xa0, 0x2a, 0x80, false, true, true);
        chord.Process(0x34, 0x05, 0x80, false, true, true);
        result = chord.Process(0xa2, 0x1d, 0x80, false, true, true);
        Require(result.Shortcut?.Label == "Ctrl+Shift+4", "modifier release order");

        var repeatedMain = new ShortcutCaptureState();
        repeatedMain.Process(0xa0, 0x2a, 0, true, false, true);
        repeatedMain.Process(0xa2, 0x1d, 0, true, false, true);
        repeatedMain.Process(0x34, 0x05, 0, true, false, true);
        repeatedMain.Process(0x34, 0x05, 0x80, false, true, true);
        repeatedMain.Process(0x34, 0x05, 0, true, false, true);
        Require(!repeatedMain.Process(0xa0, 0x2a, 0x80, false, true, true).Finished,
            "re-pressed main key waits after Shift release");
        Require(!repeatedMain.Process(0xa2, 0x1d, 0x80, false, true, true).Finished,
            "re-pressed main key waits after all modifier releases");
        result = repeatedMain.Process(0x34, 0x05, 0x80, false, true, true);
        Require(result is { Consume: true, Finished: true } && result.Shortcut?.Label == "Ctrl+Shift+4",
            "learn only after the latest main-key release");

        var deactivated = new ShortcutCaptureState();
        deactivated.Process(0x80, 0, 0, true, false, true);
        result = deactivated.Process(0x41, 0x1e, 0, true, false, false);
        Require(!result.Consume && !result.Finished, "do not learn unrelated input after focus moves");
        result = deactivated.Process(0x80, 0, 0x80, false, true, false);
        Require(result.Shortcut?.Label == "F17", "pending release after deactivation");

        var inactive = new ShortcutCaptureState();
        result = inactive.Process(0x80, 0, 0, true, false, false);
        Require(result.Finished && result.Shortcut is null && !result.Consume && result.Message is not null, "idle deactivation is visible and foreground-only");

        var escape = new ShortcutCaptureState();
        result = escape.Process(0x1b, 0x01, 0, true, false, true);
        Require(result.Finished && result.Shortcut is null && result.Message is null, "Escape cancellation");

        var reserved = new ShortcutCaptureState();
        result = reserved.Process(0x7b, 0, 0, true, false, true);
        Require(result.Finished && result.Shortcut is null && result.Message is not null, "reserved F12 rejected");

        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException($"Shortcut learning regression: {scenario}.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timeout.Stop();
        _timeout.Tick -= Timeout;
        _releaseTimeout.Stop();
        _releaseTimeout.Tick -= ReleaseTimeout;
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
        GC.KeepAlive(_procedure);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Key, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint HookProcedure(int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
