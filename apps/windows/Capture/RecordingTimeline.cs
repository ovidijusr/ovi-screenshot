using System.Diagnostics;

namespace Ovi.Screenshot.Capture;

// One serialized consumer (MediaStreamSource), immutable latest pixels, and a
// media clock shared by the UI and encoder. Pauses add neither samples nor time.
internal sealed class RecordingTimeline : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly TimeSpan _frameInterval;
    private TaskCompletionSource _changed = NewSignal();
    private VideoFrameLease? _pixels;
    private bool _started, _paused, _stopped;
    private long _nextSampleTicks, _lastTimestamp = -1;
    private long _pauseGeneration;
    private TimeSpan _lastPauseElapsed;

    internal RecordingTimeline(TimeSpan? frameInterval = null)
    {
        _frameInterval = frameInterval ?? Mp4FrameEncoder.FrameInterval;
        if (_frameInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frameInterval));
    }

    internal bool IsPaused { get { lock (_gate) return _paused; } }
    internal bool NeedsFrame { get { lock (_gate) return !_stopped && !_paused && _pixels is null; } }
    internal TimeSpan Elapsed { get { lock (_gate) return _clock.Elapsed; } }
    internal RecordingClockState Clock
    {
        get { lock (_gate) return new(_clock.Elapsed, _clock.IsRunning, _paused, _stopped, _pauseGeneration, _lastPauseElapsed); }
    }

    internal void PublishPixels(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        PublishFrame(VideoFrameBufferPool.WrapImmutable(pixels));
    }

    // Takes ownership, including when a paused or stopped timeline rejects it.
    internal void PublishFrame(VideoFrameLease pixels)
    {
        lock (_gate)
        {
            if (_stopped || _paused) { pixels.Dispose(); return; }
            _pixels?.Dispose();
            _pixels = pixels;
            // Resume counts time only after a fresh source has been supplied.
            if (_started && !_clock.IsRunning) _clock.Start();
            SignalChanged();
        }
    }

    internal void Pause()
    {
        lock (_gate)
        {
            if (_stopped || _paused) return;
            _paused = true;
            _clock.Stop();
            _lastPauseElapsed = _clock.Elapsed;
            _pauseGeneration++;
            ReleaseLatestFrame();
            SignalChanged();
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_stopped || !_paused) return;
            _paused = false;
            ReleaseLatestFrame();
            // Do not expose the pre-pause managed frame or run the clock while
            // the platform adapter prepares the first current frame.
            SignalChanged();
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _paused = false;
            _clock.Stop();
            ReleaseLatestFrame();
            SignalChanged();
        }
    }

    public void Dispose() => Stop();

    internal async ValueTask<BgraVideoFrame?> GetNextSampleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            long remaining = 0;
            lock (_gate)
            {
                if (_stopped) return null;
                changed = _changed.Task;
                if (!_paused && _pixels is not null)
                {
                    if (!_started) { _started = true; _clock.Start(); }
                    long elapsed = _clock.Elapsed.Ticks;
                    remaining = _nextSampleTicks - elapsed;
                    if (remaining <= 0)
                    {
                        long timestamp = _lastTimestamp < 0 ? 0 : Math.Max(elapsed, _lastTimestamp + 1);
                        _lastTimestamp = timestamp;
                        _nextSampleTicks = checked(timestamp + _frameInterval.Ticks);
                        return new BgraVideoFrame(_pixels.Retain(), TimeSpan.FromTicks(timestamp), _frameInterval);
                    }
                }
            }
            if (remaining > 0)
            {
                var delay = Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining / (double)TimeSpan.TicksPerMillisecond)), cancellationToken);
                await Task.WhenAny(changed, delay).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReleaseLatestFrame()
    {
        _pixels?.Dispose();
        _pixels = null;
    }

    private void SignalChanged()
    {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
