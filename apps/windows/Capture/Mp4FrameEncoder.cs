using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ovi.Screenshot.Capture;

// One native encoder path serves real screen recording and the synthetic fixture.
// Sample leases remain immutable until Processed or native transcode completion.
internal sealed class Mp4FrameEncoder : IAsyncDisposable
{
    internal const uint FrameRate = 30;
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / FrameRate);
    private readonly MediaStreamSource _source;
    private readonly Func<CancellationToken, ValueTask<BgraVideoFrame?>> _provider;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _end = new();
    private readonly int _frameBytes;
    private readonly object _samplesGate = new(), _disposeGate = new();
    private readonly Dictionary<MediaStreamSample, BgraVideoFrame> _samples = new();
    private IRandomAccessStream? _stream;
    private IAsyncActionWithProgress<double>? _transcodeOperation;
    private Task? _transcode;
    private Task? _disposeTask;
    private Exception? _sampleFailure;
    private int _processedSamples;

    private Mp4FrameEncoder(int width, int height, Func<CancellationToken, ValueTask<BgraVideoFrame?>> provider)
    {
        _provider = provider;
        _frameBytes = checked(width * height * 4);
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        // WGC/Win2D buffers are top-down. Without an explicit stride Media Foundation
        // can assume bottom-up RGB storage and vertically invert the encoded video.
        properties.Properties[new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = checked((uint)width * 4); // MF_MT_DEFAULT_STRIDE
        properties.FrameRate.Numerator = FrameRate; properties.FrameRate.Denominator = 1;
        properties.PixelAspectRatio.Numerator = properties.PixelAspectRatio.Denominator = 1;
        _source = new MediaStreamSource(new VideoStreamDescriptor(properties))
        {
            BufferTime = TimeSpan.Zero, CanSeek = false
        };
        _source.Starting += OnStarting;
        _source.SampleRequested += OnSampleRequested;
    }

    internal Task Completion => _transcode ?? Task.CompletedTask;
    internal int ProcessedSampleCount => Volatile.Read(ref _processedSamples);

    internal static async Task<Mp4FrameEncoder> StartAsync(int width, int height, string outputPath,
        Func<CancellationToken, ValueTask<BgraVideoFrame?>> provider, CancellationToken cancellationToken)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("The MP4 frame dimensions must be positive even numbers.");
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!Path.IsPathFullyQualified(outputPath) || directory is null || !string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an absolute path ending in .mp4.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        var encoder = new Mp4FrameEncoder(width, height, provider);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(Path.GetFileName(fullPath), CreationCollisionOption.FailIfExists);
            encoder._stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = null;
            profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = FrameRate; profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = profile.Video.PixelAspectRatio.Denominator = 1;
            profile.Video.Bitrate = (uint)Math.Clamp((long)width * height * 4, 2_000_000, 32_000_000);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(encoder._source, encoder._stream, profile).AsTask(cancellationToken);
            if (!prepared.CanTranscode)
                throw new NotSupportedException($"Windows could not prepare H.264 recording ({prepared.FailureReason}).");
            cancellationToken.ThrowIfCancellationRequested();
            encoder._transcodeOperation = prepared.TranscodeAsync();
            // Track actual native completion, not a cancellation wrapper: buffers
            // must stay leased while the native encoder can still read them.
            encoder._transcode = encoder._transcodeOperation.AsTask();
            return encoder;
        }
        catch
        {
            await encoder.DisposeAsync();
            throw;
        }
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        try { args.Request.SetActualStartPosition(TimeSpan.Zero); }
        catch (Exception error) { ReportCallbackFailure(error); }
    }

    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        try { await SupplySampleAsync(args).ConfigureAwait(false); }
        catch (Exception error) { ReportCallbackFailure(error); }
    }

    private async Task SupplySampleAsync(MediaStreamSourceSampleRequestedEventArgs args)
    {
        MediaStreamSourceSampleRequest? request = null;
        MediaStreamSourceSampleRequestDeferral? deferral = null;
        bool acquired = false;
        try
        {
            request = args.Request;
            deferral = request.GetDeferral();
            await _requestGate.WaitAsync(_end.Token).ConfigureAwait(false);
            acquired = true;
            if (_end.IsCancellationRequested) { EndRequest(request); return; }
            BgraVideoFrame? frame = await _provider(_end.Token).ConfigureAwait(false);
            if (frame is null || _end.IsCancellationRequested)
            {
                frame?.Dispose();
                EndRequest(request);
                return;
            }
            SubmitFrame(request, frame);
        }
        catch (OperationCanceledException) when (_end.IsCancellationRequested) { EndRequest(request); }
        catch (Exception error)
        {
            ReportCallbackFailure(error);
            EndRequest(request);
        }
        finally
        {
            FinishRequest(() => deferral?.Complete(), () => { if (acquired) _requestGate.Release(); }, ReportCallbackFailure);
        }
    }

    // Takes ownership even if a native assignment fails. Once registered, keep
    // the lease conservatively until Processed or full native completion.
    private void SubmitFrame(MediaStreamSourceSampleRequest request, BgraVideoFrame frame)
    {
        bool registered = false;
        try
        {
            if (frame.Pixels.Length != _frameBytes)
                throw new InvalidOperationException("A video frame has an unexpected byte length.");
            var sample = MediaStreamSample.CreateFromBuffer(frame.Buffer, frame.Timestamp);
            sample.Duration = frame.Duration;
            lock (_samplesGate) { _samples.Add(sample, frame); registered = true; }
            sample.Processed += OnSampleProcessed;
            request.Sample = sample;
        }
        finally { if (!registered) frame.Dispose(); }
    }

    private void OnSampleProcessed(MediaStreamSample sample, object args)
    {
        try
        {
            if (ReleaseSample(sample)) Interlocked.Increment(ref _processedSamples);
        }
        catch (Exception error) { ReportCallbackFailure(error); }
    }

    private bool ReleaseSample(MediaStreamSample sample)
    {
        BgraVideoFrame? frame;
        lock (_samplesGate) { if (!_samples.Remove(sample, out frame)) return false; }
        try { sample.Processed -= OnSampleProcessed; }
        finally { frame.Dispose(); }
        return true;
    }

    private void ReleaseRemainingSamples()
    {
        MediaStreamSample[] samples;
        lock (_samplesGate) samples = _samples.Keys.ToArray();
        foreach (var sample in samples)
        {
            try { ReleaseSample(sample); }
            catch (Exception error) { RecordFailure(error); }
        }
    }

    private void EndRequest(MediaStreamSourceSampleRequest? request)
    {
        try { if (request is not null) request.Sample = null; }
        catch (Exception error) { ReportCallbackFailure(error); }
    }

    private void RecordFailure(Exception error) => Interlocked.CompareExchange(ref _sampleFailure, error, null);

    private void ReportCallbackFailure(Exception error)
    {
        RecordFailure(error);
        try { _source.NotifyError(MediaStreamSourceErrorStatus.Other); }
        catch (Exception notificationError) { RecordFailure(notificationError); }
    }

    // Shared with the synthetic failure fixture to exercise a failed native
    // deferral completion and prove the request gate is still released.
    internal static void FinishRequest(Action completeDeferral, Action releaseGate, Action<Exception> failed)
    {
        try
        {
            try { completeDeferral(); }
            catch (Exception error) { failed(error); }
        }
        finally { releaseGate(); }
    }

    internal async Task CompleteAsync()
    {
        _end.Cancel();
        if (_transcode is not null)
        {
            try { await _transcode.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
            catch { RequestAbort(); throw; }
        }
        if (_sampleFailure is not null) throw new InvalidOperationException("Video encoding failed.", _sampleFailure);
        if (_stream is not null) await _stream.FlushAsync();
    }

    private void RequestAbort()
    {
        if (_transcode?.IsCompleted != false) return;
        try { _transcodeOperation?.Cancel(); }
        catch (Exception error) { RecordFailure(error); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _end.Cancel(); RequestAbort();
        if (_transcode is not null)
        {
            try { await _transcode.ConfigureAwait(false); } catch { /* Reported by the recording completion path. */ }
        }
        await _requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DetachSourceCallbacks();
            ReleaseRemainingSamples();
            try { _stream?.Dispose(); }
            finally { _stream = null; }
        }
        finally { _requestGate.Release(); }
        // Don't dispose synchronization primitives while a late native callback can still unwind.
    }

    private void DetachSourceCallbacks()
    {
        try { _source.Starting -= OnStarting; }
        catch (Exception error) { RecordFailure(error); }
        try { _source.SampleRequested -= OnSampleRequested; }
        catch (Exception error) { RecordFailure(error); }
    }
}
