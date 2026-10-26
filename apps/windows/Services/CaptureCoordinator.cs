using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Services;

internal sealed class CaptureCoordinator(SettingsWindow settings, PreviewStack previews, RecordingController recordings, Action<Exception> report)
{
    private SelectionWindow? _selection;
    private RecordingCountdownWindow? _countdown;
    private CancellationTokenSource? _cancellation;
    private Task _captureTask = Task.CompletedTask;
    private CaptureResult? _lastCapture;
    private bool _stopping, _lastWasRecording;

    internal void Screenshot() => BeginCapture(recordVideo: false);
    internal void RecordArea() => BeginCapture(recordVideo: true);

    private void BeginCapture(bool recordVideo)
    {
        if (_stopping || !_captureTask.IsCompleted || previews.IsBusy || settings.IsLearningShortcut) return;
        if (recordings.IsActive || recordings.IsBusy || (recordVideo && recordings.HasPendingRecording))
        {
            recordings.ShowControl();
            return;
        }
        _captureTask = CaptureAsync(recordVideo);
    }

    private async Task CaptureAsync(bool recordVideo)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        bool settingsVisible = settings.AppWindow.IsVisible;
        bool previewsVisible = previews.Windows.Count != 0;
        nint previousFocus = Win32.GetForegroundWindow();
        settings.AppWindow.Hide();
        bool recordingControlVisible = recordings.HideControlForCapture();
        previews.Suspend();
        try
        {
            if (settingsVisible || previewsVisible || recordingControlVisible)
                await Task.Run(Win32.FlushDesktopComposition, cancellation.Token);
            var monitor = Win32.PointerMonitor();
            var display = await MonitorCapture.AcquireAsync(monitor.Monitor, cancellation.Token);
            var pixels = display.PremultipliedBgra ?? await Task.Run(() => BitmapSources.ToPremultipliedBgra(display.Rgba), cancellation.Token);
            IReadOnlyList<WindowCaptureTarget>? windows = recordVideo ? null : await Task.Run(WindowTargets.Enumerate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            _selection = new SelectionWindow(display, monitor.Info.Monitor, pixels, windows);
            PixelSelection? region = await _selection.SelectAsync();
            WindowCaptureTarget? window = _selection.SelectedWindow;
            _selection = null;
            cancellation.Token.ThrowIfCancellationRequested();
            if (region is null)
            {
                RestoreFocus(previousFocus, settingsVisible);
                return;
            }

            if (recordVideo)
            {
                if (!await CountDownAsync(region.Value, monitor.Info.Monitor, monitor.Info.Work, cancellation.Token))
                {
                    RestoreFocus(previousFocus, settingsVisible);
                    return;
                }
                await Task.Run(Win32.FlushDesktopComposition, cancellation.Token);
                await recordings.StartAsync(monitor.Monitor, region.Value, monitor.Info.Monitor, monitor.Info.Work, cancellation.Token);
                _lastWasRecording = true;
            }
            else
            {
                _lastCapture = await CreateScreenshotAsync(display, region.Value, window, monitor.Info.Work, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _lastWasRecording = false;
                if (!_stopping)
                {
                    previews.Show(_lastCapture);
                    CaptureSound.Play();
                }
            }
            if (!settingsVisible) RestoreFocus(previousFocus, false);
        }
        catch (OperationCanceledException) { RestoreFocus(previousFocus, settingsVisible); }
        catch (Exception error) { if (!_stopping) report(error); }
        finally
        {
            _selection?.Cancel(); _selection = null;
            _countdown?.Cancel(); _countdown = null;
            _cancellation = null;
            if (!_stopping)
            {
                previews.Restore();
                if (recordingControlVisible) recordings.ShowControl();
            }
        }
    }

    private static async Task<CaptureResult> CreateScreenshotAsync(CapturedDisplay display, PixelSelection region,
        WindowCaptureTarget? target, Win32.Rect workArea, CancellationToken cancellation)
    {
        if (target is null) return await Task.Run(() => CaptureImageFactory.Create(display, region, workArea), cancellation);
        await Task.Run(Win32.FlushDesktopComposition, cancellation);
        var window = await WindowCapture.AcquireAsync(target, cancellation);
        return await Task.Run(() => CaptureImageFactory.Create(window,
            new PixelSelection(0, 0, window.Width, window.Height), workArea, windowShadow: true), cancellation);
    }

    private async Task<bool> CountDownAsync(PixelSelection region, Win32.Rect monitor, Win32.Rect workArea, CancellationToken cancellation)
    {
        _countdown = new RecordingCountdownWindow(new Win32.Rect
        {
            Left = monitor.Left + region.X, Top = monitor.Top + region.Y,
            Right = monitor.Left + region.X + region.Width, Bottom = monitor.Top + region.Y + region.Height
        }, workArea);
        try { return await _countdown.RunAsync(cancellation); }
        finally { _countdown.Cancel(); _countdown = null; }
    }

    private void RestoreFocus(nint previousFocus, bool settingsVisible)
    {
        if (_stopping) return;
        if (settingsVisible) settings.Activate();
        else if (previousFocus != 0) Win32.SetForegroundWindow(previousFocus);
    }

    internal void ReopenLast()
    {
        if (_stopping || !_captureTask.IsCompleted) return;
        if (_lastWasRecording || (_lastCapture is null && recordings.HasPendingRecording)) recordings.ShowControl();
        else if (_lastCapture is { IsDeleted: false }) previews.Show(_lastCapture);
    }

    internal void Forget(CaptureResult capture)
    {
        if (_lastCapture is not null && ReferenceEquals(_lastCapture.PngStorageIdentity, capture.PngStorageIdentity))
            _lastCapture = null;
    }

    internal async Task StopAsync()
    {
        _stopping = true;
        _cancellation?.Cancel();
        _selection?.Cancel(); _countdown?.Cancel();
        await _captureTask;
    }
}
