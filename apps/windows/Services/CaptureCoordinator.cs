using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Services;

internal sealed class CaptureCoordinator(SettingsWindow settings, PreviewStack previews, RecordingController recordings,
    Action<Exception> report, Func<CancellationToken, Task<RecordingOptions>>? recordingOptions = null)
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
            var topology = DisplayTopology.Enumerate();
            if (!Win32.GetCursorPos(out var pointer)) throw new InvalidOperationException("The pointer position is unavailable.");
            var initialMonitor = topology.AtPoint(pointer);
            var display = await DesktopCapture.AcquireAsync(topology, cancellation.Token);
            var pixels = display.PremultipliedBgra ?? await Task.Run(() => BitmapSources.ToPremultipliedBgra(display.Rgba), cancellation.Token);
            IReadOnlyList<WindowCaptureTarget> windows = await Task.Run(WindowTargets.Enumerate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            EnsureDisplaysUnchanged(topology);

            _selection = new SelectionWindow(display, topology.Bounds, pixels, windows,
                controlsBounds: initialMonitor.WorkArea,
                areaLimits: recordVideo ? topology.Monitors.Select(monitor => monitor.Bounds).ToArray() : null);
            PixelSelection? region = await _selection.SelectAsync();
            WindowCaptureTarget? window = _selection.SelectedWindow;
            _selection = null;
            cancellation.Token.ThrowIfCancellationRequested();
            if (region is null)
            {
                RestoreFocus(previousFocus, settingsVisible);
                return;
            }

            EnsureDisplaysUnchanged(topology);
            Win32.Rect desktopRegion = SelectionCoordinates.ToDesktop(region.Value, topology.Bounds);
            if (!SelectionCoordinates.IncludesDisplayPixels(topology, desktopRegion))
                throw new InvalidOperationException("Select an area that includes a display.");

            if (recordVideo)
            {
                if (window is not null)
                {
                    var placement = topology.ForRectangle(window.Bounds);
                    if (!await CountDownAsync(window.Bounds, placement.WorkArea, cancellation.Token))
                    {
                        RestoreFocus(previousFocus, settingsVisible);
                        return;
                    }
                    EnsureDisplaysUnchanged(topology);
                    await Task.Run(Win32.FlushDesktopComposition, cancellation.Token);
                    var options = await RecordingOptionsAsync(cancellation.Token);
                    await recordings.StartWindowAsync(window, placement, options, cancellation.Token);
                }
                else
                {
                    var monitor = SelectionCoordinates.RecordingMonitor(topology, desktopRegion);
                    var recordingRegion = SelectionCoordinates.ToMonitor(desktopRegion, monitor);
                    if (!await CountDownAsync(recordingRegion, monitor.Bounds, monitor.WorkArea, cancellation.Token))
                    {
                        RestoreFocus(previousFocus, settingsVisible);
                        return;
                    }
                    EnsureDisplaysUnchanged(topology);
                    await Task.Run(Win32.FlushDesktopComposition, cancellation.Token);
                    var options = await RecordingOptionsAsync(cancellation.Token);
                    await recordings.StartAsync(monitor.Handle, recordingRegion, monitor.Bounds, monitor.WorkArea,
                        options, cancellation.Token);
                }
                _lastWasRecording = true;
            }
            else
            {
                var placement = topology.ForRectangle(window?.Bounds ?? desktopRegion);
                _lastCapture = await CreateScreenshotAsync(display, region.Value, window, placement.WorkArea, cancellation.Token);
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

    private static void EnsureDisplaysUnchanged(DisplayTopology topology)
    {
        if (!topology.IsCurrent()) throw new InvalidOperationException("The display layout changed. Start the capture again.");
    }

    private Task<RecordingOptions> RecordingOptionsAsync(CancellationToken cancellation)
        => recordingOptions?.Invoke(cancellation) ?? Task.FromResult(new RecordingOptions());

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
        => await CountDownAsync(new Win32.Rect
        {
            Left = monitor.Left + region.X, Top = monitor.Top + region.Y,
            Right = monitor.Left + region.X + region.Width, Bottom = monitor.Top + region.Y + region.Height
        }, workArea, cancellation);

    private async Task<bool> CountDownAsync(Win32.Rect bounds, Win32.Rect workArea, CancellationToken cancellation)
    {
        _countdown = new RecordingCountdownWindow(bounds, workArea);
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
