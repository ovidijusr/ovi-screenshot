using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Ovi.Screenshot.Services;

namespace Ovi.Screenshot.Capture;

internal sealed class RecordingAudioCapture : IAsyncDisposable
{
    private readonly Func<RecordingClockState> _clock;
    private readonly RecordingAudioBuffer _buffer;
    private readonly List<(WasapiRecorder Recorder, MMDevice Device)> _inputs = [];
    private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifetimeGate = new();
    private Task? _stopInputsTask, _disposeTask;
    private int _inputsStopping, _providerDisposed;

    internal RecordingAudioCapture(Func<RecordingClockState> clock, int sources)
        : this(clock, new RecordingAudioBuffer(sources)) { }

    internal RecordingAudioCapture(Func<RecordingClockState> clock, RecordingAudioBuffer buffer)
    {
        _clock = clock;
        _buffer = buffer;
    }

    internal Task<Exception> Failure => _failure.Task;

    internal static async Task<RecordingAudioCapture> StartAsync(RecordingOptions options,
        Func<RecordingClockState> clock, CancellationToken cancellationToken)
    {
        options.Validate();
        ArgumentNullException.ThrowIfNull(clock);
        var capture = new RecordingAudioCapture(clock, (options.SystemAudio ? 1 : 0) + (options.Microphone ? 1 : 0));
        try
        {
            // NAudio captures SynchronizationContext at construction; keep both
            // initialization and its stopped callback independent of the UI.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.SystemAudio) capture.OpenInput(options.OutputDeviceId, loopback: true, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (options.Microphone) capture.OpenInput(options.MicrophoneDeviceId, loopback: false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }, CancellationToken.None).ConfigureAwait(false);
            if (capture.Failure.IsCompleted) throw await capture.Failure.ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<PcmAudioFrame?> GetNextSampleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _providerDisposed) != 0) return null;
            if (_buffer.TryRead(_clock(), out PcmAudioFrame? frame)) return frame;
            await Task.WhenAny(_stopped.Task, Task.Delay(5, cancellationToken))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void OpenInput(string? id, bool loopback, CancellationToken cancellationToken)
    {
        string name = loopback ? "system audio output" : "microphone";
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            DataFlow flow = loopback ? DataFlow.Render : DataFlow.Capture;
            device = string.IsNullOrEmpty(id) ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : enumerator.GetDevice(id);
            if (device.DataFlow != flow || device.State != DeviceState.Active)
                throw new InvalidOperationException($"The selected {name} is unavailable.");
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode()
                .WithBufferLength(50).WithFormat(new WaveFormat((int)PcmAudioFrame.SampleRate,
                    (int)PcmAudioFrame.BitsPerSample, (int)PcmAudioFrame.Channels));
            if (loopback) builder.WithLoopbackCapture();
            recorder = builder.Build();
            int source = _inputs.Count;
            recorder.DataAvailable += (pcm, flags, _, qpcPosition) =>
            {
                if (Volatile.Read(ref _inputsStopping) != 0) return;
                try
                {
                    _buffer.Publish(source, pcm, (flags & AudioClientBufferFlags.Silent) != 0, qpcPosition,
                        (flags & AudioClientBufferFlags.TimestampError) == 0, _clock(), WallTicks());
                }
                catch (Exception error) { ReportFailure(new InvalidOperationException($"Could not process {name}.", error)); }
            };
            recorder.RecordingStopped += (_, args) =>
            {
                if (Volatile.Read(ref _inputsStopping) == 0)
                    ReportFailure(new InvalidOperationException($"The {name} stopped. Check the selected audio device.", args.Exception));
            };
            cancellationToken.ThrowIfCancellationRequested();
            recorder.StartRecording();
            _inputs.Add((recorder, device));
        }
        catch (Exception error)
        {
            try { recorder?.Dispose(); }
            finally { device?.Dispose(); }
            if (error is OperationCanceledException) throw;
            throw new InvalidOperationException($"Could not start the selected {name}. Check that it is connected and allowed in Windows privacy settings.", error);
        }
    }

    private void ReportFailure(Exception error)
    {
        if (_failure.TrySetResult(error)) AppLog.Shared.Write(error);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    // End microphone/loopback access before potentially slow MP4 finalization,
    // retaining already-buffered samples for the encoder's final clock interval.
    internal Task StopInputsAsync()
    {
        lock (_lifetimeGate) return _stopInputsTask ??= StopInputsCoreAsync();
    }

    private async Task StopInputsCoreAsync()
    {
        Volatile.Write(ref _inputsStopping, 1);
        foreach ((WasapiRecorder recorder, _) in _inputs)
        {
            try { recorder.StopRecording(); }
            catch (Exception error) { AppLog.Shared.Write(error); }
        }
        foreach ((WasapiRecorder recorder, MMDevice device) in _inputs)
        {
            try { await recorder.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { AppLog.Shared.Write(error); }
            finally
            {
                try { device.Dispose(); }
                catch (Exception error) { AppLog.Shared.Write(error); }
            }
        }
        _inputs.Clear();
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _providerDisposed, 1);
        _stopped.TrySetResult();
        try { await StopInputsAsync().ConfigureAwait(false); }
        finally
        {
            // Joining capture threads above makes this the last buffer mutation.
            _buffer.Clear();
        }
    }

    private static long WallTicks() => (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
}
