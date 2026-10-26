using System.Runtime.InteropServices.WindowsRuntime;
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
    internal const int AudioSampleCapacity = 8;
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / FrameRate);
    private readonly MediaStreamSource _source;
    private readonly Func<CancellationToken, ValueTask<BgraVideoFrame?>> _provider;
    private readonly Func<CancellationToken, ValueTask<PcmAudioFrame?>>? _audioProvider;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly SemaphoreSlim _audioSlots = new(AudioSampleCapacity, AudioSampleCapacity);
    private readonly CancellationTokenSource _end = new();
    private readonly CancellationTokenSource _audioEnd = new();
    private readonly int _frameBytes;
    private readonly object _samplesGate = new(), _disposeGate = new();
    private readonly Dictionary<MediaStreamSample, BgraVideoFrame> _samples = new();
    private readonly Dictionary<MediaStreamSample, PcmAudioFrame> _audioSamples = new();
    private IRandomAccessStream? _stream;
    private IAsyncActionWithProgress<double>? _transcodeOperation;
    private Task? _transcode;
    private Task? _disposeTask;
    private Exception? _sampleFailure;
    private int _processedSamples;
    private int _processedAudioSamples;
    private int _peakAudioSamples;

    private Mp4FrameEncoder(int width, int height, Func<CancellationToken, ValueTask<BgraVideoFrame?>> provider,
        RecordingOptions options, Func<CancellationToken, ValueTask<PcmAudioFrame?>>? audioProvider)
    {
        _provider = provider; _audioProvider = audioProvider;
        _frameBytes = checked(width * height * 4);
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        // WGC/Win2D buffers are top-down. Without an explicit stride Media Foundation
        // can assume bottom-up RGB storage and vertically invert the encoded video.
        properties.Properties[new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = checked((uint)width * 4); // MF_MT_DEFAULT_STRIDE
        properties.FrameRate.Numerator = (uint)options.FramesPerSecond; properties.FrameRate.Denominator = 1;
        properties.PixelAspectRatio.Numerator = properties.PixelAspectRatio.Denominator = 1;
        var video = new VideoStreamDescriptor(properties);
        _source = audioProvider is null ? new MediaStreamSource(video) : new MediaStreamSource(video,
            new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(PcmAudioFrame.SampleRate, PcmAudioFrame.Channels, PcmAudioFrame.BitsPerSample)));
        _source.BufferTime = TimeSpan.Zero; _source.CanSeek = false;
        _source.Starting += OnStarting;
        _source.SampleRequested += OnSampleRequested;
    }

    internal Task Completion => _transcode ?? Task.CompletedTask;
    internal int ProcessedSampleCount => Volatile.Read(ref _processedSamples);
    internal int ProcessedAudioSampleCount => Volatile.Read(ref _processedAudioSamples);
    internal int PeakAudioSampleCount { get { lock (_samplesGate) return _peakAudioSamples; } }

    internal static Task<Mp4FrameEncoder> StartAsync(int width, int height, string outputPath,
        Func<CancellationToken, ValueTask<BgraVideoFrame?>> provider, CancellationToken cancellationToken) =>
        StartAsync(width, height, outputPath, provider, new RecordingOptions(), null, cancellationToken);

    internal static async Task<Mp4FrameEncoder> StartAsync(int width, int height, string outputPath,
        Func<CancellationToken, ValueTask<BgraVideoFrame?>> provider, RecordingOptions options,
        Func<CancellationToken, ValueTask<PcmAudioFrame?>>? audioProvider, CancellationToken cancellationToken)
    {
        options.Validate();
        if (options.HasAudio != (audioProvider is not null))
            throw new ArgumentException("The audio setting and PCM source must match.", nameof(audioProvider));
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("The MP4 frame dimensions must be positive even numbers.");
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!Path.IsPathFullyQualified(outputPath) || directory is null || !string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an absolute path ending in .mp4.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        var encoder = new Mp4FrameEncoder(width, height, provider, options, audioProvider);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(Path.GetFileName(fullPath), CreationCollisionOption.FailIfExists);
            encoder._stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = options.HasAudio ? AudioEncodingProperties.CreateAac(PcmAudioFrame.SampleRate, PcmAudioFrame.Channels, 192_000) : null;
            profile.Video.Width = (uint)width; profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)options.FramesPerSecond; profile.Video.FrameRate.Denominator = 1;
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
        SemaphoreSlim? gate = null;
        CancellationToken cancellation = default;
        bool acquired = false;
        try
        {
            request = args.Request;
            deferral = request.GetDeferral();
            bool audio = request.StreamDescriptor is AudioStreamDescriptor;
            gate = audio ? _audioGate : _requestGate;
            cancellation = audio ? _audioEnd.Token : _end.Token;
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            acquired = true;
            if (cancellation.IsCancellationRequested) { EndRequest(request); return; }
            if (audio) await SupplyAudioAsync(request, cancellation).ConfigureAwait(false);
            else await SupplyVideoAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { EndRequest(request); }
        catch (Exception error)
        {
            ReportCallbackFailure(error);
            EndRequest(request);
        }
        finally
        {
            FinishRequest(() => deferral?.Complete(), () => { if (acquired) gate!.Release(); }, ReportCallbackFailure);
        }
    }

    private async Task SupplyVideoAsync(MediaStreamSourceSampleRequest request, CancellationToken cancellation)
    {
        BgraVideoFrame? frame = await _provider(cancellation).ConfigureAwait(false);
        if (frame is null || cancellation.IsCancellationRequested)
        {
            frame?.Dispose(); EndRequest(request); return;
        }
        SubmitFrame(request, frame);
    }

    private async Task SupplyAudioAsync(MediaStreamSourceSampleRequest request, CancellationToken cancellation)
    {
        await _audioSlots.WaitAsync(cancellation).ConfigureAwait(false);
        bool registered = false;
        try
        {
            var frame = await _audioProvider!(cancellation).ConfigureAwait(false);
            if (frame is null || cancellation.IsCancellationRequested) { EndRequest(request); return; }
            long expectedTicks = (long)(frame.Pcm.Length / PcmAudioFrame.BytesPerFrame) * TimeSpan.TicksPerSecond / PcmAudioFrame.SampleRate;
            if (frame.Pcm.Length == 0 || frame.Pcm.Length > PcmAudioFrame.BytesPerBlock || frame.Pcm.Length % PcmAudioFrame.BytesPerFrame != 0 ||
                frame.Timestamp < TimeSpan.Zero || frame.Duration <= TimeSpan.Zero || Math.Abs(frame.Duration.Ticks - expectedTicks) > 1)
                throw new InvalidOperationException("An audio frame has an invalid PCM layout or duration.");
            var sample = MediaStreamSample.CreateFromBuffer(frame.Pcm.AsBuffer(), frame.Timestamp);
            sample.Duration = frame.Duration;
            lock (_samplesGate)
            {
                _audioSamples.Add(sample, frame); registered = true;
                _peakAudioSamples = Math.Max(_peakAudioSamples, _audioSamples.Count);
            }
            sample.Processed += OnSampleProcessed;
            request.Sample = sample;
        }
        finally { if (!registered) _audioSlots.Release(); }
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
            switch (ReleaseSample(sample))
            {
                case SampleKind.Video: Interlocked.Increment(ref _processedSamples); break;
                case SampleKind.Audio: Interlocked.Increment(ref _processedAudioSamples); break;
            }
        }
        catch (Exception error) { ReportCallbackFailure(error); }
    }

    private SampleKind ReleaseSample(MediaStreamSample sample)
    {
        BgraVideoFrame? frame;
        bool audio;
        lock (_samplesGate)
        {
            audio = _audioSamples.Remove(sample);
            if (!_samples.Remove(sample, out frame) && !audio) return SampleKind.None;
        }
        try { sample.Processed -= OnSampleProcessed; }
        finally { frame?.Dispose(); if (audio) _audioSlots.Release(); }
        return audio ? SampleKind.Audio : SampleKind.Video;
    }

    private enum SampleKind { None, Video, Audio }

    private void ReleaseRemainingSamples()
    {
        MediaStreamSample[] samples;
        lock (_samplesGate) samples = _samples.Keys.Concat(_audioSamples.Keys).ToArray();
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

    internal async Task CompleteAsync(bool drainAudio = false)
    {
        _end.Cancel();
        if (!drainAudio) _audioEnd.Cancel();
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
        _audioEnd.Cancel();
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
        _end.Cancel(); _audioEnd.Cancel(); RequestAbort();
        if (_transcode is not null)
        {
            try { await _transcode.ConfigureAwait(false); } catch { /* Reported by the recording completion path. */ }
        }
        await _requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _audioGate.WaitAsync().ConfigureAwait(false);
            try
            {
                DetachSourceCallbacks();
                ReleaseRemainingSamples();
                try { _stream?.Dispose(); }
                finally { _stream = null; }
            }
            finally { _audioGate.Release(); }
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
