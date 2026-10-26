using System.Buffers.Binary;

namespace Ovi.Screenshot.Capture;

// Packets retain their position on the video clock; an idle input never pushes
// the other input forward. Each source owns at most one second of PCM.
internal sealed class RecordingAudioBuffer
{
    internal const int CapacityFrames = (int)PcmAudioFrame.SampleRate;
    internal static readonly TimeSpan InputWait = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private readonly Source[] _sources;
    private long _nextFrame, _minimumInputFrame, _pauseGeneration;

    internal RecordingAudioBuffer(int sourceCount)
    {
        if (sourceCount is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(sourceCount));
        _sources = Enumerable.Range(0, sourceCount).Select(_ => new Source()).ToArray();
    }

    internal void Publish(int source, ReadOnlySpan<byte> pcm, bool silent, long qpcPosition,
        bool timestampReliable, RecordingClockState clock, long wallTicks)
    {
        if ((uint)source >= _sources.Length) throw new ArgumentOutOfRangeException(nameof(source));
        if (pcm.Length % PcmAudioFrame.BytesPerFrame != 0)
            throw new ArgumentException("Audio packets must contain complete stereo PCM frames.", nameof(pcm));
        lock (_gate)
        {
            if (!ObserveClock(clock)) return;
            if (!clock.IsRunning || clock.IsPaused || clock.IsStopped) return;
            long packetTicks = (long)((Int128)(pcm.Length / PcmAudioFrame.BytesPerFrame)
                * TimeSpan.TicksPerSecond / PcmAudioFrame.SampleRate);
            long age = wallTicks - qpcPosition;
            long startTicks = timestampReliable && qpcPosition > 0 && age >= 0
                ? clock.Elapsed.Ticks - age : clock.Elapsed.Ticks - packetTicks;
            long start = FramesAt(startTicks);
            long first = Math.Max(Math.Max(Math.Max(start, _minimumInputFrame), _nextFrame),
                start + pcm.Length / PcmAudioFrame.BytesPerFrame - CapacityFrames);
            long last = Math.Min(start + pcm.Length / PcmAudioFrame.BytesPerFrame,
                FramesAt(clock.Elapsed.Ticks) + CapacityFrames);
            Source input = _sources[source];
            for (long frame = first; frame < last; frame++)
            {
                int offset = checked((int)(frame - start) * PcmAudioFrame.BytesPerFrame);
                int slot = (int)(frame % CapacityFrames);
                input.Left[slot] = silent ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
                input.Right[slot] = silent ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(pcm[(offset + 2)..]);
                input.Positions[slot] = frame;
            }
        }
    }

    // true/null ends the stream; false waits for the shared active clock.
    internal bool TryRead(RecordingClockState clock, out PcmAudioFrame? sample)
    {
        lock (_gate)
        {
            sample = null;
            if (!ObserveClock(clock)) return false;
            long available = FramesAt(clock.Elapsed.Ticks) - _nextFrame;
            if (clock.IsStopped && available <= 0) return true;
            if (clock.IsPaused) return false;
            bool bootstrap = _nextFrame == 0 && !clock.IsStopped;
            if (!bootstrap && !clock.IsStopped && (!clock.IsRunning ||
                FramesAt(clock.Elapsed.Ticks - InputWait.Ticks) - _nextFrame < PcmAudioFrame.FramesPerBlock))
                return false;
            int frames = clock.IsStopped ? (int)Math.Min(PcmAudioFrame.FramesPerBlock, available)
                : PcmAudioFrame.FramesPerBlock;
            byte[] output = new byte[frames * PcmAudioFrame.BytesPerFrame];
            if (!bootstrap) Mix(output, frames);
            sample = new(output, TimeSpan.FromTicks(TicksAt(_nextFrame)), TimeSpan.FromTicks(TicksAt(frames)));
            _nextFrame += frames;
            return true;
        }
    }

    internal void Clear()
    {
        lock (_gate) ClearSources();
    }

    private bool ObserveClock(RecordingClockState clock)
    {
        // Clock snapshots can reach this lock out of order on different inputs.
        if (clock.PauseGeneration < _pauseGeneration) return false;
        if (clock.PauseGeneration > _pauseGeneration)
        {
            long boundary = FramesAt(clock.LastPauseElapsed.Ticks);
            DiscardFrom(boundary);
            _minimumInputFrame = Math.Max(_nextFrame, boundary);
            _pauseGeneration = clock.PauseGeneration;
        }
        return true;
    }

    private void Mix(Span<byte> output, int frames)
    {
        for (int index = 0; index < frames; index++)
        {
            long position = _nextFrame + index;
            int slot = (int)(position % CapacityFrames);
            int left = 0, right = 0;
            foreach (Source source in _sources)
            {
                if (source.Positions[slot] != position) continue;
                left += source.Left[slot];
                right += source.Right[slot];
                source.Positions[slot] = -1;
            }
            int offset = index * PcmAudioFrame.BytesPerFrame;
            BinaryPrimitives.WriteInt16LittleEndian(output[offset..], (short)Math.Clamp(left, short.MinValue, short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(output[(offset + 2)..], (short)Math.Clamp(right, short.MinValue, short.MaxValue));
        }
    }

    private void ClearSources()
    {
        foreach (Source source in _sources) Array.Fill(source.Positions, -1);
    }

    private void DiscardFrom(long boundary)
    {
        foreach (Source source in _sources)
            for (int slot = 0; slot < source.Positions.Length; slot++)
                if (source.Positions[slot] >= boundary) source.Positions[slot] = -1;
    }

    private static long FramesAt(long ticks) => (long)((Int128)ticks * PcmAudioFrame.SampleRate / TimeSpan.TicksPerSecond);
    private static long TicksAt(long frames) => (long)((Int128)frames * TimeSpan.TicksPerSecond / PcmAudioFrame.SampleRate);

    private sealed class Source
    {
        internal readonly short[] Left = new short[CapacityFrames];
        internal readonly short[] Right = new short[CapacityFrames];
        internal readonly long[] Positions = Enumerable.Repeat(-1L, CapacityFrames).ToArray();
    }
}
