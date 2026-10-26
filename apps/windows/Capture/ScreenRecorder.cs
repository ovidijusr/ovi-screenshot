using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Ovi.Screenshot.Capture;

internal sealed class ScreenRecorder : IAsyncDisposable
{
    private readonly string _outputPath;
    private readonly PixelSelection _region;
    private readonly object _captureGate = new(), _stopGate = new();
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _copyClock = Stopwatch.StartNew();
    private readonly RecordingTimeline _timeline;
    private readonly RecordingOptions _options;
    private readonly WindowCaptureTarget? _windowTarget;
    private readonly uint _windowProcess;
    private readonly Win32.Rect _windowBounds;
    private readonly VideoFrameBufferPool _buffers;
    private readonly CancellationTokenSource _sourceEnded = new();
    private CanvasDevice? _canvasDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Mp4FrameEncoder? _encoder;
    private RecordingAudioCapture? _audio;
    private Direct3D11CaptureFrame? _latestFrame;
    private Task<string>? _stopTask;
    private Exception? _captureFailure;
    private bool _stopping;
    private long _lastCopyTicks = -1;
    private Windows.Graphics.SizeInt32 _captureSize;

    private ScreenRecorder(PixelSelection region, string path, RecordingOptions options,
        WindowCaptureTarget? windowTarget = null, uint windowProcess = 0, Win32.Rect windowBounds = default)
    {
        _region = region; _outputPath = Path.GetFullPath(path);
        _options = options; _timeline = new RecordingTimeline(options.FrameInterval);
        _windowTarget = windowTarget; _windowProcess = windowProcess; _windowBounds = windowBounds;
        Width = checked(region.Width + (region.Width & 1));
        Height = checked(region.Height + (region.Height & 1));
        _buffers = new VideoFrameBufferPool(checked(Width * Height * 4));
    }

    internal int Width { get; }
    internal int Height { get; }
    internal Task Completion => _completion.Task;
    internal bool IsPaused => _timeline.IsPaused;
    internal TimeSpan Elapsed => _timeline.Elapsed;

    internal void Pause()
    {
        lock (_captureGate)
        {
            if (_stopping) return;
            _timeline.Pause();
        }
    }

    internal void Resume()
    {
        lock (_captureGate)
        {
            if (_stopping || !_timeline.IsPaused) return;
            _timeline.Resume();
            _lastCopyTicks = -1;
        }
        // A static desktop may not emit another WGC event. The retained latest
        // native frame remains valid until replaced and is copied anew here.
        _ = Task.Run(RefreshAfterResumeAsync);
    }

    private async Task RefreshAfterResumeAsync()
    {
        try
        {
            while (true)
            {
                lock (_captureGate)
                {
                    if (_stopping || !_timeline.NeedsFrame) return;
                    if (CopyLatestFrame()) return;
                }
                await _buffers.WaitForAvailableAsync(_sourceEnded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_sourceEnded.IsCancellationRequested) { }
        catch (Exception error) { FailCapture(error); }
    }

    internal static Task<ScreenRecorder> StartAsync(nint monitor, PixelSelection region, string outputPath, CancellationToken startupCancellation) =>
        StartAsync(monitor, region, outputPath, new RecordingOptions(), startupCancellation);

    internal static Task<ScreenRecorder> StartAsync(nint monitor, PixelSelection region, string outputPath,
        RecordingOptions options, CancellationToken startupCancellation)
    {
        ValidateCaptureSupport(options, startupCancellation);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException("Select a nonempty recording area.", nameof(region));
        return StartItemAsync(CreateForMonitor(monitor), region, outputPath, options, startupCancellation);
    }

    internal static Task<ScreenRecorder> StartWindowAsync(WindowCaptureTarget target, string outputPath,
        RecordingOptions options, CancellationToken startupCancellation)
    {
        ValidateCaptureSupport(options, startupCancellation);
        ArgumentNullException.ThrowIfNull(target);
        if (!WindowTargets.TryGetBounds(target.Handle, out var bounds))
            throw new InvalidOperationException("That window is no longer available. Select it again.");
        uint process = WindowTargets.OwnerProcessId(target.Handle);
        if (process == 0 || (target.ProcessId != 0 && target.ProcessId != process) ||
            bounds.Width != target.Bounds.Width || bounds.Height != target.Bounds.Height)
            throw new InvalidOperationException("That window changed before recording started. Select it again.");
        if (WindowTargets.ExcludesCapture(target.Handle))
            throw new NotSupportedException("That window does not allow capture.");
        var item = CreateForWindow(target.Handle);
        return StartItemAsync(item, new PixelSelection(0, 0, item.Size.Width, item.Size.Height), outputPath, options,
            startupCancellation, target, process, bounds);
    }

    private static void ValidateCaptureSupport(RecordingOptions options, CancellationToken cancellation)
    {
        options.Validate(); cancellation.ThrowIfCancellationRequested();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Screen recording is unavailable in this Windows session.");
    }

    private static async Task<ScreenRecorder> StartItemAsync(GraphicsCaptureItem item, PixelSelection region, string outputPath,
        RecordingOptions options, CancellationToken startupCancellation, WindowCaptureTarget? windowTarget = null,
        uint windowProcess = 0, Win32.Rect windowBounds = default)
    {
        if (region.Width <= 0 || region.Height <= 0) throw new InvalidOperationException("The capture source has no visible pixels.");
        var recorder = new ScreenRecorder(region, outputPath, options, windowTarget, windowProcess, windowBounds);
        try
        {
            recorder._canvasDevice = new CanvasDevice();
            recorder._item = item;
            recorder._captureSize = recorder._item.Size;
            if ((long)region.X + region.Width > recorder._item.Size.Width || (long)region.Y + region.Height > recorder._item.Size.Height)
                throw new ArgumentException("The selected recording area is outside the display.", nameof(region));
            recorder._pool = Direct3D11CaptureFramePool.CreateFreeThreaded(recorder._canvasDevice.As<IDirect3DDevice>(),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, recorder._item.Size);
            recorder._session = recorder._pool.CreateCaptureSession(recorder._item);
            recorder._session.IsCursorCaptureEnabled = true;
            recorder._pool.FrameArrived += recorder.OnFrameArrived;
            recorder._item.Closed += recorder.OnSourceClosed;
            recorder._session.StartCapture();
            if (windowTarget is not null) _ = Task.Run(recorder.WatchWindowAsync);
            await recorder._firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5), startupCancellation);
            recorder.ThrowIfCaptureFailed();
            if (options.HasAudio)
            {
                recorder._audio = await RecordingAudioCapture.StartAsync(options, () => recorder._timeline.Clock, startupCancellation);
                _ = recorder.ObserveAudioFailureAsync(recorder._audio);
            }
            recorder.ThrowIfCaptureFailed();
            recorder._encoder = await Mp4FrameEncoder.StartAsync(recorder.Width, recorder.Height, recorder._outputPath,
                recorder.GetSampleAsync, options, options.HasAudio ? recorder.GetAudioSampleAsync : null, startupCancellation);
            _ = recorder.ObserveEncoderAsync();
            recorder.ThrowIfCaptureFailed();
            return recorder;
        }
        catch
        {
            await recorder.DisposeAsync();
            throw;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_captureGate)
        {
            if (_stopping) return;
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                if (frame.ContentSize.Width != _captureSize.Width || frame.ContentSize.Height != _captureSize.Height)
                {
                    frame.Dispose();
                    throw new InvalidOperationException("The recording source changed size.");
                }
                try { ValidateWindowSource(); }
                catch { frame.Dispose(); throw; }
                Direct3D11CaptureFrame? previous = _latestFrame;
                _latestFrame = frame;
                previous?.Dispose();
                // Drain/retain the newest native frame while paused, without
                // GPU readback or managed frame allocation.
                if (_timeline.IsPaused) return;
                long now = _copyClock.Elapsed.Ticks;
                if (!_timeline.NeedsFrame && _lastCopyTicks >= 0 && now - _lastCopyTicks < _options.FrameInterval.Ticks) return;
                if (CopyLatestFrame()) _firstFrame.TrySetResult();
            }
            catch (Exception error) { FailCapture(error); }
        }
    }

    // Caller owns _captureGate, including the retained frame's lifetime.
    private bool CopyLatestFrame()
    {
        if (_latestFrame is null) throw new InvalidOperationException("The current recording frame is unavailable.");
        VideoFrameLease? pixels = _buffers.TryRent();
        if (pixels is null) return false; // The encoder still owns the bounded buffers; drop this update.
        try
        {
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_canvasDevice!, _latestFrame.Surface);
            bitmap.GetPixelBytes(pixels.Buffer, _region.X, _region.Y, _region.Width, _region.Height);
            pixels.PadPackedCrop(_region.Width, _region.Height, Width, Height);
            _lastCopyTicks = _copyClock.Elapsed.Ticks;
            _timeline.PublishFrame(pixels);
            pixels = null;
            return true;
        }
        finally { pixels?.Dispose(); }
    }

    private ValueTask<BgraVideoFrame?> GetSampleAsync(CancellationToken cancellationToken) =>
        _timeline.GetNextSampleAsync(cancellationToken);

    private async ValueTask<PcmAudioFrame?> GetAudioSampleAsync(CancellationToken cancellationToken)
    {
        try { return await _audio!.GetNextSampleAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { FailCapture(error); return null; }
    }

    private void ThrowIfCaptureFailed()
    {
        if (Volatile.Read(ref _captureFailure) is { } error) throw new InvalidOperationException("The recording source is unavailable.", error);
    }

    private void ValidateWindowSource()
    {
        if (_windowTarget is null) return;
        if (!WindowTargets.TryGetBounds(_windowTarget.Handle, out var bounds) ||
            WindowTargets.OwnerProcessId(_windowTarget.Handle) != _windowProcess)
            throw new InvalidOperationException("The recorded window closed, minimized or became unavailable.");
        if (bounds.Width != _windowBounds.Width || bounds.Height != _windowBounds.Height)
            throw new InvalidOperationException("The recorded window changed size. The clip has been stopped.");
    }

    private async Task WatchWindowAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(_sourceEnded.Token).ConfigureAwait(false))
            {
                lock (_captureGate)
                {
                    if (_stopping) return;
                    ValidateWindowSource();
                }
            }
        }
        catch (OperationCanceledException) when (_sourceEnded.IsCancellationRequested) { }
        catch (Exception error) { FailCapture(error); }
    }

    private async Task ObserveAudioFailureAsync(RecordingAudioCapture audio)
    {
        try { FailCapture(await audio.Failure.WaitAsync(_sourceEnded.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (_sourceEnded.IsCancellationRequested) { }
        catch (Exception error) { FailCapture(error); }
    }

    private void OnSourceClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_captureGate)
        {
            if (!_stopping) FailCapture(new InvalidOperationException("The recording source is no longer available."));
        }
    }

    private void FailCapture(Exception error)
    {
        Interlocked.CompareExchange(ref _captureFailure, error, null);
        _firstFrame.TrySetException(error);
        _timeline.Stop();
        CancelSource();
    }

    private void CancelSource()
    {
        try { _sourceEnded.Cancel(); }
        catch (Exception error) { Interlocked.CompareExchange(ref _captureFailure, error, null); }
    }

    private async Task ObserveEncoderAsync()
    {
        try { await _encoder!.Completion.ConfigureAwait(false); }
        catch (Exception error) { Interlocked.CompareExchange(ref _captureFailure, error, null); }
        try { await StopAsync().ConfigureAwait(false); } catch { /* Exposed through Completion/StopAsync. */ }
    }

    internal Task<string> StopAsync()
    {
        lock (_stopGate) return _stopTask ??= StopCoreAsync();
    }

    private async Task<string> StopCoreAsync()
    {
        try
        {
            _timeline.Stop(); CancelSource();
            CloseCaptureResources();
            try { if (_audio is not null) await _audio.StopInputsAsync().ConfigureAwait(false); }
            catch (Exception error) { Interlocked.CompareExchange(ref _captureFailure, error, null); }
            try
            {
                if (_encoder is not null) await _encoder.CompleteAsync(drainAudio: true).ConfigureAwait(false);
            }
            finally
            {
                try { if (_encoder is not null) await _encoder.DisposeAsync().ConfigureAwait(false); }
                finally { if (_audio is not null) await _audio.DisposeAsync().ConfigureAwait(false); }
            }
            if (_captureFailure is not null) throw new InvalidOperationException("Recording could not finish.", _captureFailure);
            _completion.TrySetResult();
            return _outputPath;
        }
        catch (Exception error) { _completion.TrySetException(error); throw; }
    }

    private void CloseCaptureResources()
    {
        Direct3D11CaptureFrame? frame;
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        GraphicsCaptureItem? item;
        CanvasDevice? device;
        lock (_captureGate)
        {
            _stopping = true;
            frame = _latestFrame; _latestFrame = null;
            pool = _pool; _pool = null;
            session = _session; _session = null;
            item = _item; _item = null;
            device = _canvasDevice; _canvasDevice = null;
        }
        CleanupCapture(() => { if (pool is not null) pool.FrameArrived -= OnFrameArrived; });
        CleanupCapture(() => { if (item is not null) item.Closed -= OnSourceClosed; });
        CleanupCapture(() => frame?.Dispose());
        CleanupCapture(() => session?.Dispose());
        CleanupCapture(() => pool?.Dispose());
        CleanupCapture(() => device?.Dispose());
    }

    private void CleanupCapture(Action release)
    {
        try { release(); }
        catch (Exception error) { Interlocked.CompareExchange(ref _captureFailure, error, null); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { /* The explicit completion path reports failures. */ }
        if (_encoder is not null) await _encoder.DisposeAsync().ConfigureAwait(false);
    }

    private static GraphicsCaptureItem CreateForMonitor(nint monitor) => CreateItem(monitor, window: false);
    private static GraphicsCaptureItem CreateForWindow(nint window) => CreateItem(window, window: true);

    private static GraphicsCaptureItem CreateItem(nint handle, bool window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out nint text));
        nint factory = 0, item = 0;
        try
        {
            Guid factoryId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in factoryId, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            Guid itemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(window ? interop.CreateForWindow(handle, in itemId, out item)
                : interop.CreateForMonitor(handle, in itemId, out item));
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(text);
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, in Guid iid, out nint result);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
}
