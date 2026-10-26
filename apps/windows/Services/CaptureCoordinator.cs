using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Services;

internal sealed class CaptureCoordinator(SettingsWindow settings, PreviewStack previews, RecordingController recordings,
    Action<Exception> report, Func<CancellationToken, Task<RecordingOptions>>? recordingOptions = null,
    Action<RecordingOptions>? saveRecordingOptions = null, CaptureStorage? storage = null)
{
    private SelectionWindow? _selection;
    private RecordingCountdownWindow? _countdown;
    private CaptureSetupWindow? _captureSetup;
    private CancellationTokenSource? _cancellation;
    private Task _captureTask = Task.CompletedTask;
    private CaptureResult? _lastCapture;
    private StoredCaptureFile? _lastScreenshotFile;
    private VideoPreview? _lastVideo;
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
            Task<RecordingOptions> initialOptions = RecordingOptionsAsync(cancellation.Token);
            Task<AudioDeviceCatalog> audioDevices = AudioDevicesAsync(cancellation.Token);
            var display = await DesktopCapture.AcquireAsync(topology, cancellation.Token);
            var pixels = display.PremultipliedBgra ?? await Task.Run(() => BitmapSources.ToPremultipliedBgra(display.Rgba), cancellation.Token);
            IReadOnlyList<WindowCaptureTarget> windows = await Task.Run(WindowTargets.Enumerate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            EnsureDisplaysUnchanged(topology);
            RecordingOptions initial = await initialOptions;
            cancellation.Token.ThrowIfCancellationRequested();

            var selection = new SelectionWindow(display, topology.Bounds, pixels, windows,
                controlsBounds: initialMonitor.WorkArea,
                areaLimits: topology.Monitors.Select(monitor => monitor.Bounds).ToArray(),
                confirmSelection: true, initialVideo: recordVideo);
            _selection = selection;
            Task<PixelSelection?> selected = selection.SelectAsync();
            RecordingOptions? options = null;
            bool selectedVideo = recordVideo;
            if (!selected.IsCompleted)
            {
                var toolbar = ShowCaptureToolbar(selection, initialMonitor.WorkArea, initial, recordVideo,
                    video => selectedVideo = video, chosen => options = chosen);
                _ = RefreshToolbarDevicesAsync(toolbar, audioDevices, selected);
            }
            else _ = ObservePreparationAsync(audioDevices);
            PixelSelection? region = await selected;
            WindowCaptureTarget? window = selection.SelectedWindow;
            _captureSetup?.CloseSafely(); _captureSetup = null;
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

            if (selectedVideo)
            {
                if (options is null) throw new InvalidOperationException("Choose Record to start a video.");
                if (recordings.HasPendingRecording || recordings.IsBusy) { recordings.ShowControl(); return; }
                var placement = window is null ? SelectionCoordinates.RecordingMonitor(topology, desktopRegion)
                    : topology.ForRectangle(window.Bounds);
                saveRecordingOptions?.Invoke(options);
                EnsureDisplaysUnchanged(topology);
                if (window is not null)
                {
                    if (!WindowTargets.TryGetBounds(window.Handle, out var currentBounds) ||
                        (window.ProcessId != 0 && WindowTargets.OwnerProcessId(window.Handle) != window.ProcessId) ||
                        currentBounds.Width != window.Bounds.Width || currentBounds.Height != window.Bounds.Height)
                        throw new InvalidOperationException("The selected window changed. Select it again.");
                    window = window with { Bounds = currentBounds };
                    placement = topology.ForRectangle(currentBounds);
                    if (!await CountDownAsync(window.Bounds, placement.WorkArea, cancellation.Token))
                    {
                        RestoreFocus(previousFocus, settingsVisible);
                        return;
                    }
                    EnsureDisplaysUnchanged(topology);
                    await Task.Run(Win32.FlushDesktopComposition, cancellation.Token);
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
                    await recordings.StartAsync(monitor.Handle, recordingRegion, monitor.Bounds, monitor.WorkArea,
                        options, cancellation.Token);
                }
                _lastWasRecording = true;
            }
            else
            {
                var placement = topology.ForRectangle(window?.Bounds ?? desktopRegion);
                _lastCapture = await CreateScreenshotAsync(display, region.Value, window, placement.WorkArea, cancellation.Token);
                _lastScreenshotFile = null;
                if (storage is not null)
                {
                    // A completed capture is accepted work: finish its save even if quit begins.
                    try { _lastScreenshotFile = await storage.SaveScreenshotAsync(await _lastCapture.ReadPngAsync()); }
                    catch (Exception error)
                    {
                        var failure = new InvalidOperationException("Screenshot could not be saved automatically. Use Save in the preview.", error);
                        if (_stopping) AppLog.Shared.Write(failure); else report(failure);
                    }
                }
                _lastWasRecording = false;
                if (!_stopping)
                {
                    previews.Show(_lastCapture, _lastScreenshotFile is { } savedFile ? savedFile.DeleteAsync : null, _lastScreenshotFile?.Path);
                    CaptureSound.Play();
                }
            }
            if (!settingsVisible) RestoreFocus(previousFocus, false);
        }
        catch (OperationCanceledException) { RestoreFocus(previousFocus, settingsVisible); }
        catch (Exception error) { if (!_stopping) report(error); }
        finally
        {
            _captureSetup?.CloseSafely(); _captureSetup = null;
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

    private static async Task ObservePreparationAsync(Task preparation)
    {
        try { await preparation.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Shared.Write(error); }
    }

    private CaptureSetupWindow ShowCaptureToolbar(SelectionWindow selection, Win32.Rect workArea,
        RecordingOptions initial, bool initialVideo, Action<bool> modeChanged, Action<RecordingOptions> choose)
    {
        var placement = new Win32.Rect { Left = workArea.Left, Right = workArea.Right, Top = workArea.Top, Bottom = workArea.Top };
        var toolbar = new CaptureSetupWindow(placement, workArea, initial, new AudioDeviceCatalog([], []), initialVideo: initialVideo);
        _captureSetup = toolbar;
        toolbar.AttachOwner(selection.Handle);
        selection.SetToolbarHandle(toolbar.Handle);
        selection.SelectionChanged += toolbar.SetCanCapture;
        selection.Activated += toolbar.RaiseWithoutActivation;
        selection.ToolbarRequested += toolbar.ActivateForKeyboard;
        selection.Closing += toolbar.CloseSafely;
        toolbar.FocusSelectionRequested += selection.FocusSelection;
        toolbar.VideoModeChanged += video => { selection.SetVideoMode(video); modeChanged(video); };
        toolbar.ScreenshotRequested += () =>
        {
            if (!selection.HasSelection) return;
            toolbar.CloseSafely();
            selection.ConfirmSelection();
        };
        toolbar.RecordRequested += chosen =>
        {
            if (!selection.HasSelection) return;
            choose(chosen);
            toolbar.CloseSafely();
            selection.ConfirmSelection();
        };
        toolbar.CancelRequested += () => { toolbar.CloseSafely(); selection.Cancel(); };
        toolbar.SetVideoAvailable(!recordings.HasPendingRecording);
        toolbar.ShowForSelection();
        toolbar.SetCanCapture(selection.HasSelection);
        return toolbar;
    }

    private static async Task RefreshToolbarDevicesAsync(CaptureSetupWindow toolbar, Task<AudioDeviceCatalog> devices,
        Task<PixelSelection?> selected)
    {
        try
        {
            if (await Task.WhenAny(devices, selected) == selected)
            {
                _ = ObservePreparationAsync(devices);
                return;
            }
            AudioDeviceCatalog catalog = await devices;
            if (!selected.IsCompleted) toolbar.SetDevices(catalog);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Shared.Write(error); }
    }

    private static async Task<AudioDeviceCatalog> AudioDevicesAsync(CancellationToken cancellation)
    {
        try { return await Task.Run(RecordingAudioDevices.Enumerate, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            AppLog.Shared.Write(error);
            return new AudioDeviceCatalog([], []);
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
        if (recordings.IsActive || recordings.HasPendingRecording) recordings.ShowControl();
        else if (_lastWasRecording && _lastVideo is { IsDeleted: false }) previews.Show(_lastVideo);
        else if (_lastCapture is { IsDeleted: false })
            previews.Show(_lastCapture, _lastScreenshotFile is { } savedFile ? savedFile.DeleteAsync : null, _lastScreenshotFile?.Path);
    }

    internal void Forget(CaptureResult capture)
    {
        if (_lastCapture is not null && ReferenceEquals(_lastCapture.PngStorageIdentity, capture.PngStorageIdentity))
        { _lastCapture = null; _lastScreenshotFile = null; }
    }

    internal async Task ShowSavedRecordingAsync(StoredCaptureFile saved, Win32.Rect workArea)
    {
        var video = await VideoPreview.CreateAsync(saved.Path, workArea, saved.DeleteAsync);
        if (_stopping) return;
        _lastVideo = video;
        _lastWasRecording = true;
        previews.Show(video);
    }

    internal void ForgetVideo(VideoPreview video)
    {
        if (ReferenceEquals(_lastVideo, video)) _lastVideo = null;
    }

    internal async Task StopAsync()
    {
        _stopping = true;
        _cancellation?.Cancel();
        _captureSetup?.CloseSafely(); _selection?.Cancel(); _countdown?.Cancel();
        await _captureTask;
    }
}
