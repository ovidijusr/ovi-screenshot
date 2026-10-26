using Microsoft.UI.Dispatching;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Ovi.Screenshot.Services;

internal sealed class RecordingController
{
    private readonly RecordingLibrary _library;
    private readonly CaptureStorage? _storage;
    private readonly AppLog _log;
    private readonly Action<Exception> _report;
    private readonly Func<CancellationToken, Task<bool>> _confirmDiscard;
    private readonly Func<CancellationToken, Task<IReadOnlyList<StoredRecording>>> _recoverRecordings;
    private readonly CancellationTokenSource _interactive = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly List<StoredRecording> _pending = [];
    private ActiveRecording? _active;
    private RecordingControlWindow? _control;
    private Task _stopTask = Task.CompletedTask;
    private Task _stopAndSaveTask = Task.CompletedTask;
    private Task _fileAction = Task.CompletedTask;
    private Task? _recoveryTask;
    private Task? _shutdownTask;
    private bool _fileActionRunning, _stopping;
    private Win32.Rect? _previewWorkArea;

    internal bool IsActive => _active is not null;
    internal bool IsBusy => _recoveryTask is not { IsCompleted: true } || !_stopTask.IsCompleted || !_stopAndSaveTask.IsCompleted || _fileActionRunning;
    internal bool HasPendingRecording => _pending.Count != 0;
    internal event Action? CaptureRequested;
    internal event Action<bool>? StateChanged;
    internal Func<StoredCaptureFile, Win32.Rect, Task>? RecordingSaved { get; set; }

    internal RecordingController(DispatcherQueue dispatcher, RecordingLibrary library, AppLog log, Action<Exception> report,
        Func<CancellationToken, Task<bool>> confirmDiscard,
        Func<CancellationToken, Task<IReadOnlyList<StoredRecording>>>? recoverRecordings = null,
        CaptureStorage? storage = null)
    {
        _library = library; _log = log; _report = report;
        _storage = storage;
        _confirmDiscard = confirmDiscard;
        _recoverRecordings = recoverRecordings ?? library.RecoverAsync;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => RefreshElapsedTime();
    }

    internal Task RecoverAsync() => _stopping ? Task.CompletedTask : _recoveryTask ??= RecoverCoreAsync();

    private async Task RecoverCoreAsync()
    {
        try
        {
            var recovered = await _recoverRecordings(_interactive.Token);
            if (!_stopping)
            {
                _pending.AddRange(recovered);
                if (_storage is not null)
                    while (_pending.Count > 0 && !_stopping)
                    {
                        try { await AutoSaveAsync(); }
                        catch (Exception error) { _log.Write(error); ShowControl(); _control?.ShowFileActionFailure("Auto-save failed"); break; }
                    }
            }
        }
        catch (OperationCanceledException) when (_stopping) { }
    }

    internal void Toggle()
    {
        if (_stopping) return;
        if (IsActive) StopAndSave();
        else if (HasPendingRecording || IsBusy) ShowControl();
        else CaptureRequested?.Invoke();
    }

    internal Task StartAsync(nint monitor, PixelSelection region, Win32.Rect monitorBounds, Win32.Rect workArea, CancellationToken cancellation)
        => StartAsync(monitor, region, monitorBounds, workArea, new RecordingOptions(), cancellation);

    internal Task StartAsync(nint monitor, PixelSelection region, Win32.Rect monitorBounds, Win32.Rect workArea,
        RecordingOptions options, CancellationToken cancellation)
    {
        var display = new DisplayMonitor(monitor, monitorBounds, workArea);
        var bounds = new Win32.Rect
        {
            Left = monitorBounds.Left + region.X, Top = monitorBounds.Top + region.Y,
            Right = monitorBounds.Left + region.X + region.Width, Bottom = monitorBounds.Top + region.Y + region.Height
        };
        return StartCoreAsync(path => ScreenRecorder.StartAsync(monitor, region, path, options, cancellation),
            display, bounds, null, cancellation);
    }

    internal Task StartWindowAsync(WindowCaptureTarget target, DisplayMonitor display, RecordingOptions options, CancellationToken cancellation)
        => StartCoreAsync(path => ScreenRecorder.StartWindowAsync(target, path, options, cancellation),
            display, target.Bounds, target, cancellation);

    private async Task StartCoreAsync(Func<string, Task<ScreenRecorder>> start, DisplayMonitor display,
        Win32.Rect bounds, WindowCaptureTarget? target, CancellationToken cancellation)
    {
        if (_stopping || IsActive || IsBusy || HasPendingRecording)
            throw new InvalidOperationException("Finish the current recording first.");
        string path = _library.NewPath();
        ScreenRecorder? recorder = null;
        RecordingBoundaryWindow? boundary = null;
        try
        {
            EnsureDisplayCurrent(display);
            recorder = await start(path);
            cancellation.ThrowIfCancellationRequested();
            EnsureDisplayCurrent(display);
            var topology = target is null ? null : DisplayTopology.Enumerate();
            if (target is not null)
            {
                if (!WindowTargets.TryGetBounds(target.Handle, out bounds))
                    throw new InvalidOperationException("The recording window is unavailable.");
            }
            boundary = new RecordingBoundaryWindow(bounds, topology?.Bounds ?? display.Bounds);
            boundary.Show();
            EnsureDisplayCurrent(display);
            _active = new ActiveRecording(recorder, boundary, path, display, target, topology);
            _previewWorkArea = display.WorkArea;
            _stopTask = Task.CompletedTask;
            CloseControl();
            ShowControl();
            _timer.Start();
            AnnounceState(true);
            _ = ObserveCompletionAsync(recorder);
        }
        catch
        {
            boundary?.Dispose();
            if (recorder is not null) await recorder.DisposeAsync();
            _active = null;
            try { await _library.DeleteAsync(path); } catch (Exception error) { _log.Write(error); }
            throw;
        }
    }

    internal void ShowControl()
    {
        if (_stopping || (!IsActive && !HasPendingRecording)) return;
        if (_control is null)
        {
            var control = new RecordingControlWindow(_active?.Display.WorkArea ?? Win32.PointerMonitor().Info.Work);
            _control = control;
            control.StopRequested += StopAndSave;
            control.PauseRequested += TogglePause;
            control.SaveRequested += () => QueueFileAction("Save failed", SaveAsync);
            control.DiscardRequested += () => QueueFileAction("Discard failed", DiscardAsync);
            control.Closed += (_, _) => { if (ReferenceEquals(_control, control)) _control = null; };
        }
        if (_active is { } session)
        {
            _control.SetElapsed(session.Recorder.Elapsed);
            if (!_stopTask.IsCompleted) _control.MarkFinalizing();
            else _control.SetPaused(session.Recorder.IsPaused);
        }
        else
        {
            _control.SetElapsed(_pending[0].Duration);
            _control.MarkReady(_pending[0].Recovered);
        }
        _control.ShowWithoutActivation();
    }

    internal bool HideControlForCapture()
    {
        if (_control?.AppWindow.IsVisible != true) return false;
        _control.AppWindow.Hide();
        return true;
    }

    private void TogglePause()
    {
        if (_stopping || IsBusy || _active is not { } session) return;
        try
        {
            if (session.Recorder.IsPaused) session.Recorder.Resume(); else session.Recorder.Pause();
            _control?.SetPaused(session.Recorder.IsPaused);
            session.Boundary.SetPaused(session.Recorder.IsPaused);
            RefreshElapsedTime();
        }
        catch (Exception error) { _report(error); }
    }

    private void RefreshElapsedTime()
    {
        if (_active is not { } session) return;
        try
        {
            if (session.Target is { } target)
            {
                if (session.Topology?.IsCurrent() != true || !WindowTargets.TryGetBounds(target.Handle, out var bounds)
                    || (target.ProcessId != 0 && WindowTargets.OwnerProcessId(target.Handle) != target.ProcessId))
                { StopAndSave(); return; }
                session.Boundary.UpdateBounds(bounds, session.Topology.Bounds);
            }
            else if (!session.Display.IsCurrent()) { StopAndSave(); return; }
            _control?.SetElapsed(session.Recorder.Elapsed);
        }
        catch (Exception error) { _log.Write(error); StopAndSave(); }
    }

    private static void EnsureDisplayCurrent(DisplayMonitor display)
    {
        if (!display.IsCurrent()) throw new InvalidOperationException("The recording display changed. Select its area again.");
    }

    private async Task ObserveCompletionAsync(ScreenRecorder recorder)
    {
        try { await recorder.Completion; }
        catch (Exception error) { _log.Write(error); }
        if (!_stopping && ReferenceEquals(_active?.Recorder, recorder) && _stopTask.IsCompleted) StopAndSave();
    }

    private void StopAndSave()
    {
        if (!_stopping && _stopAndSaveTask.IsCompleted) _stopAndSaveTask = StopAndSaveAsync();
    }

    private async Task StopAndSaveAsync()
    {
        try
        {
            await StopAsync();
            if (!_stopping && HasPendingRecording)
                await QueueFileAction("Auto-save failed", _storage is null ? SaveAsync : AutoSaveAsync);
        }
        catch (Exception error) { _report(error); }
    }

    private Task StopAsync()
    {
        if (!_stopTask.IsCompleted || _active is null) return _stopTask;
        return _stopTask = StopSessionAsync(_active);
    }

    private async Task StopSessionAsync(ActiveRecording session)
    {
        _timer.Stop();
        session.Boundary.Dispose();
        _control?.MarkFinalizing();
        try
        {
            string path = await session.Recorder.StopAsync();
            _pending.Add(await _library.RememberAsync(path, session.Recorder.Elapsed));
        }
        catch (Exception error)
        {
            _log.Write(error);
            var recovered = await _library.FindRecoverableAsync(session.Path);
            if (recovered is not null) _pending.Add(recovered);
            else
            {
                try { await _library.DeleteAsync(session.Path); } catch (Exception cleanup) { _log.Write(cleanup); }
                _control?.ShowFailure("Recording failed");
            }
        }
        finally
        {
            try { await session.Recorder.DisposeAsync(); } catch (Exception error) { _log.Write(error); }
            if (ReferenceEquals(_active, session)) _active = null;
            AnnounceState(false);
        }
        if (!_stopping && HasPendingRecording && _storage is null) ShowControl();
    }

    private Task QueueFileAction(string failureMessage, Func<Task> action)
    {
        if (_stopping || _fileActionRunning || IsActive || !HasPendingRecording) return _fileAction;
        _fileActionRunning = true;
        return _fileAction = RunFileActionAsync(failureMessage, action);
    }

    private async Task RunFileActionAsync(string failureMessage, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception error)
        {
            _log.Write(error);
            if (!_stopping && HasPendingRecording) ShowControl();
            _control?.ShowFileActionFailure(failureMessage);
        }
        finally { _fileActionRunning = false; }
    }

    private async Task SaveAsync()
    {
        var recording = _pending[0];
        var control = _control!;
        var picker = new FileSavePicker { SuggestedFileName = $"Recording {DateTime.Now:yyyy-MM-dd HHmmss}" };
        picker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, control.Handle);
        var destination = await picker.PickSaveFileAsync().AsTask(_interactive.Token);
        if (destination is null) return;
        if (!StringComparer.OrdinalIgnoreCase.Equals(destination.Path, recording.Path))
        {
            var source = await StorageFile.GetFileFromPathAsync(recording.Path);
            await source.CopyAndReplaceAsync(destination);
            try { await _library.DeleteAsync(recording.Path); } catch (Exception error) { _log.Write(error); }
        }
        CompletePending(recording);
    }

    private async Task AutoSaveAsync()
    {
        var recording = _pending[0];
        var saved = await _storage!.SaveRecordingAsync(recording.Path);
        try { await _library.DeleteAsync(recording.Path); }
        catch (Exception error) { _log.Write(error); }
        CompletePending(recording, showNext: false);
        if (_stopping || RecordingSaved is null) return;
        try { await RecordingSaved(saved, _previewWorkArea ?? Win32.PointerMonitor().Info.Work); }
        catch (Exception error) { _report(new InvalidOperationException("Recording saved, but its preview could not be opened.", error)); }
    }

    private async Task DiscardAsync()
    {
        var recording = _pending[0];
        if (!await _confirmDiscard(_interactive.Token)) return;
        await _library.DeleteAsync(recording.Path);
        CompletePending(recording);
    }

    private void CompletePending(StoredRecording recording, bool showNext = true)
    {
        _pending.Remove(recording);
        CloseControl();
        if (showNext && !_stopping && HasPendingRecording) ShowControl();
    }

    private void CloseControl()
    {
        _control?.CloseSafely();
        _control = null;
    }

    private void AnnounceState(bool recording)
    {
        try { StateChanged?.Invoke(recording); }
        catch (Exception error) { _log.Write(error); }
    }

    internal Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _stopping = true;
        _interactive.Cancel();
        _timer.Stop();
        try { await (_recoveryTask ?? Task.CompletedTask); }
        catch (Exception error) { _log.Write(error); }
        await StopAsync();
        await _stopAndSaveTask;
        await _fileAction;
        if (_storage is not null)
            while (_pending.Count > 0)
            {
                try { await AutoSaveAsync(); }
                catch (Exception error) { _log.Write(error); break; }
            }
        CloseControl();
        _interactive.Dispose();
    }

    private sealed record ActiveRecording(ScreenRecorder Recorder, RecordingBoundaryWindow Boundary, string Path,
        DisplayMonitor Display, WindowCaptureTarget? Target, DisplayTopology? Topology);
}
