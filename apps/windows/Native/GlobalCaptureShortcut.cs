using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Ovi.Screenshot.Native;

// Used only for the explicitly selected conflicting chord; normal bindings use RegisterHotKey.
internal sealed class GlobalCaptureShortcut : IDisposable
{
    private const uint ResetMessage = 0x8000 + 41, CaptureMessage = 0x8000 + 42;
    private static readonly uint[] ModifierKeys = [0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0x5b, 0x5c];
    private readonly CaptureShortcut _shortcut;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _captured;
    private readonly GlobalShortcutState _state;
    private readonly HookProcedure _procedure;
    private readonly Thread _thread;
    private readonly object _configuration = new();
    private readonly ConcurrentQueue<ResetRequest> _resets = new();
    private readonly TaskCompletionSource<Exception?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hook;
    private uint _threadId;
    private int _enabled, _generation, _activeGeneration, _disposed;
    private nuint _releaseTimer;
    private int _deliveryGeneration;
    private long _deliveryDeadline;

    internal event Action<Exception>? Failed;

    internal GlobalCaptureShortcut(CaptureShortcut shortcut, DispatcherQueue dispatcher, Action captured)
    {
        _shortcut = shortcut; _dispatcher = dispatcher; _captured = captured;
        _state = new GlobalShortcutState(shortcut);
        _procedure = HandleKey;
        _thread = new Thread(Run) { IsBackground = true, Name = "Ovi capture shortcut" };
        _thread.Start();
        if (!_started.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            Dispose();
            throw new TimeoutException("Could not start the screenshot key. Try again.");
        }
        if (_started.Task.Result is { } error) { Dispose(); throw error; }
        // Installation is prepared but inert until the coordinator atomically swaps bindings.
    }

    internal bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0 && Volatile.Read(ref _disposed) == 0;
        set
        {
            lock (_configuration)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                Volatile.Write(ref _enabled, 0);
                int generation = Interlocked.Increment(ref _generation);
                var request = new ResetRequest(value, generation);
                _resets.Enqueue(request);
                if (!PostThreadMessage(Volatile.Read(ref _threadId), ResetMessage, 0, 0))
                {
                    Interlocked.Increment(ref _generation);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update the screenshot key. Try again.");
                }
                if (!request.Completed.Task.Wait(TimeSpan.FromSeconds(2)))
                {
                    Interlocked.Increment(ref _generation);
                    throw new TimeoutException("The screenshot key listener did not respond. Try again.");
                }
                if (request.Completed.Task.Result is { } error) throw error;
            }
        }
    }

    private void Run()
    {
        try
        {
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            PeekMessage(out _, 0, 0, 0, 0); // Establish the thread queue before startup is acknowledged.
            if (Volatile.Read(ref _disposed) != 0) return;
            _hook = SetWindowsHookEx(13, _procedure, GetModuleHandle(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the screenshot key.");
            _state.Reset(false);
            _started.TrySetResult(null);
            while (Volatile.Read(ref _disposed) == 0)
            {
                int result = GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The screenshot key listener stopped.");
                if (message.Id == ResetMessage) ApplyResets();
                else if (message.Id == CaptureMessage)
                {
                    _deliveryGeneration = unchecked((int)message.WParam);
                    _deliveryDeadline = Environment.TickCount64 + 300;
                    TryDeliverCapture();
                }
                else if (message.Id == 0x0113 && message.WParam == _releaseTimer) TryDeliverCapture();
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _enabled, 0);
            if (!_started.TrySetResult(error))
                _dispatcher.TryEnqueue(() => { if (Volatile.Read(ref _disposed) == 0) Failed?.Invoke(error); });
        }
        finally
        {
            StopReleaseTimer();
            Unhook();
            _started.TrySetResult(new InvalidOperationException("The screenshot key listener did not start."));
            while (_resets.TryDequeue(out var request)) request.Completed.TrySetResult(new InvalidOperationException("The screenshot key listener stopped."));
        }
    }

    private void ApplyResets()
    {
        while (_resets.TryDequeue(out var request))
        {
            try
            {
                StopReleaseTimer();
                if (Volatile.Read(ref _disposed) != 0 || request.Generation != Volatile.Read(ref _generation))
                    throw new InvalidOperationException("The screenshot key update was cancelled.");
                uint[] held = request.Enabled ? ModifierKeys.Where(key => GetAsyncKeyState((int)key) < 0).ToArray() : [];
                bool mainHeld = request.Enabled && GetAsyncKeyState((int)_shortcut.Key) < 0;
                _state.Reset(request.Enabled, held, mainHeld);
                _activeGeneration = request.Generation;
                // A timed-out request must never arm itself later.
                if (request.Generation != Volatile.Read(ref _generation) || Volatile.Read(ref _disposed) != 0)
                    throw new InvalidOperationException("The screenshot key update was cancelled.");
                Volatile.Write(ref _enabled, request.Enabled ? 1 : 0);
                request.Completed.TrySetResult(null);
            }
            catch (Exception error)
            {
                Volatile.Write(ref _enabled, 0);
                _state.Reset(false);
                request.Completed.TrySetResult(error);
            }
        }
    }

    private nint HandleKey(int code, nuint message, nint data)
    {
        if (code != 0 || !Enabled || _activeGeneration != Volatile.Read(ref _generation)) return CallNextHookEx(0, code, message, data);
        try
        {
            bool down = message is 0x0100 or 0x0104, up = message is 0x0101 or 0x0105;
            if (!down && !up) return CallNextHookEx(0, code, message, data);
            KeyData key = Marshal.PtrToStructure<KeyData>(data);
            GlobalShortcutStep step = _state.Process(key.Key, key.ScanCode, key.Flags, down, up);
            if (step.Capture) PostThreadMessage(Volatile.Read(ref _threadId), CaptureMessage, unchecked((nuint)_activeGeneration), 0);
            return step.Consume ? 1 : CallNextHookEx(0, code, message, data);
        }
        catch { return CallNextHookEx(0, code, message, data); }
    }

    private void TryDeliverCapture()
    {
        if (!Enabled || _deliveryGeneration != Volatile.Read(ref _generation)) { StopReleaseTimer(); return; }
        // Outside the hook: wait for Windows to apply the forwarded modifier releases as well.
        bool held = _state.IsPending || GetAsyncKeyState((int)_shortcut.Key) < 0 ||
            ModifierKeys.Any(key => (GlobalShortcutState.Modifier(key) & _shortcut.Modifiers) != 0 && GetAsyncKeyState((int)key) < 0);
        if (held)
        {
            if (Environment.TickCount64 >= _deliveryDeadline) { StopReleaseTimer(); return; }
            if (_releaseTimer == 0)
            {
                _releaseTimer = SetTimer(0, 0, 10, 0);
                if (_releaseTimer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not finish the screenshot key.");
            }
            return;
        }
        StopReleaseTimer();
        int generation = _deliveryGeneration;
        _dispatcher.TryEnqueue(() => { if (Enabled && generation == Volatile.Read(ref _generation)) _captured(); });
    }

    private void StopReleaseTimer()
    {
        if (_releaseTimer == 0) return;
        KillTimer(0, _releaseTimer); _releaseTimer = 0;
    }

    private void Unhook()
    {
        nint hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0) UnhookWindowsHookEx(hook);
        GC.KeepAlive(_procedure);
    }

    internal static void VerifySyntheticBinding()
    {
        var state = new GlobalShortcutState(CaptureShortcut.Parse("Shift+Win+3"));
        state.Reset(true);
        Require(!Key(0xa0, true).Consume && !Key(0x5b, true).Consume, "modifier downs pass through");
        Require(Key(0x33, true).Consume, "exact chord consumes main down");
        for (int repeat = 0; repeat < 4; repeat++)
        {
            GlobalShortcutStep repeated = Key(0x33, true);
            Require(repeated.Consume && !repeated.Capture, "auto-repeat does not capture");
        }
        Require(Key(0x33, false) is { Consume: true, Capture: false }, "wait for modifiers after main up");
        Require(Key(0x5b, false) is { Consume: false, Capture: false }, "Win release stays balanced");
        Require(Key(0xa0, false) is { Consume: false, Capture: true }, "one capture after full release");
        Require(!Key(0x33, false).Capture, "no duplicate capture on extra keyup");

        state.Reset(true);
        Key(0x5b, true); Key(0xa0, true); Key(0x33, true);
        Require(!Key(0xa0, false).Capture && !Key(0x5b, false).Capture, "macro modifier-first release waits for main");
        Require(Key(0x33, false) is { Consume: true, Capture: true }, "macro reverse release order");

        state.Reset(true);
        Require(!Key(0x33, true).Consume && !Key(0x33, false).Consume, "bare main key passes through");
        Key(0xa0, true);
        Require(!Key(0x33, true).Consume, "missing Win does not match");
        Key(0x5b, true);
        Require(!Key(0x33, true).Consume, "unmatched main auto-repeat stays passed through after modifiers change");
        Require(!Key(0x33, false).Consume, "do not latch a stroke midway");
        Require(!Key(0x34, true).Consume && !Key(0x34, false).Consume, "wrong main key passes through");
        Key(0xa2, true);
        Require(!Key(0x33, true).Consume && !Key(0x33, false).Consume, "extra modifier does not match");

        state.Reset(true); Key(0xa0, true); Key(0x5b, true); Key(0x33, true);
        Key(0x33, false); Key(0x33, true);
        Require(!Key(0xa0, false).Capture && !Key(0x5b, false).Capture, "re-pressed main must release after modifiers");
        Require(Key(0x33, false).Capture, "capture after latest main release");

        state.Reset(true); Key(0xa0, true); Key(0x5b, true); Key(0x33, true);
        state.Reset(false);
        Require(!Key(0x33, false).Consume && !Key(0xa0, false).Capture && !Key(0x5b, false).Capture, "pause clears pending stroke and passes input");
        state.Reset(true, [0xa0, 0x5b], mainHeld: true);
        Require(!Key(0x33, true).Consume && !Key(0x33, false).Capture, "resume ignores a main key already held");
        Key(0xa0, false); Key(0x5b, false);
        Key(0x10, true, 0x36); Key(0x5c, true); Key(0x33, true);
        Key(0x10, false, 0x36); Key(0x5c, false);
        Require(Key(0x33, false).Capture, "right modifiers and generic Shift normalize");

        var snapshot = new GlobalShortcutState(CaptureShortcut.Parse("PrintScreen"));
        snapshot.Reset(true);
        Require(snapshot.Process(0x2c, 0x37, 0x80, false, true) is { Consume: true, Capture: true }, "release-only PrintScreen");

        GlobalShortcutStep Key(uint key, bool down, uint scan = 0) => state.Process(key, scan, down ? 0u : 0x80u, down, !down);
        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException($"Global shortcut regression: {scenario}.");
        }
    }

    public void Dispose()
    {
        lock (_configuration)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _enabled, 0);
            Interlocked.Increment(ref _generation);
            uint thread = Volatile.Read(ref _threadId);
            if (thread != 0) PostThreadMessage(thread, 0x0012, 0, 0);
        }
        if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(2))) Unhook();
    }

    private sealed record ResetRequest(bool Enabled, int Generation)
    {
        internal TaskCompletionSource<Exception?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Key, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Win32.Point Point; public uint Private; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint HookProcedure(int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
