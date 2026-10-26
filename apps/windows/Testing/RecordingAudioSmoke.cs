using System.Buffers.Binary;
using Ovi.Screenshot.Capture;

namespace Ovi.Screenshot.Testing;

internal static class RecordingAudioSmoke
{
    private static readonly long Epoch = TimeSpan.FromSeconds(10).Ticks;

    internal static async Task VerifyAsync()
    {
        VerifyMixAndTiming();
        VerifyPauseWithoutReads();
        VerifyUnobservedPause();
        VerifyWallClockSkewWithoutPause();
        VerifyBoundedBacklog();
        VerifyFinalPartialBlock();
        await VerifyStoppedInputsDrainAsync();
        await VerifyProviderLifetimeAsync();
    }

    private static void VerifyMixAndTiming()
    {
        var buffer = Started(2);
        byte[] first = Pcm(20_000, -20_000);
        Publish(buffer, 0, first, startMs: 20, elapsedMs: 60);
        Array.Clear(first); // The callback span must not remain borrowed.
        Publish(buffer, 1, Pcm(20_000, -20_000), startMs: 20, elapsedMs: 60);
        Publish(buffer, 1, Pcm(123, -456), startMs: 40, elapsedMs: 80);
        Publish(buffer, 0, Pcm(700, -900), startMs: 60, elapsedMs: 80);
        Require(!buffer.TryRead(Clock(139), out _), "Audio did not wait for late native packets.");
        PcmAudioFrame mixed = Read(buffer, 140);
        Check(mixed, 20, short.MaxValue, short.MinValue);
        Check(Read(buffer, 160), 40, 123, -456);
        Check(Read(buffer, 180), 60, 700, -900);
        Check(Read(buffer, 200), 80, 0, 0);
        Require(BinaryPrimitives.ReadInt16LittleEndian(mixed.Pcm) == short.MaxValue,
            "A later read mutated an already-published PCM array.");
    }

    private static void VerifyPauseWithoutReads()
    {
        var buffer = Started(1);
        Publish(buffer, 0, Pcm(900, 900), 20, 60);
        buffer.Publish(0, Pcm(999, 999), false, Wall(40), true,
            new(TimeSpan.FromMilliseconds(60), false, true, false, 1, TimeSpan.FromMilliseconds(60)), Wall(80));
        // A delayed pre-pause packet arrives after resume. It must not reappear.
        buffer.Publish(0, Pcm(888, 888), false, Wall(40), true, Clock(80, 1, 60), Wall(1080));
        buffer.Publish(0, Pcm(300, -300), false, Wall(1080), true, Clock(100, 1, 60), Wall(1100));
        Check(Read(buffer, 200, 1, 60), 20, 900, 900);
        for (int timestamp = 40; timestamp < 80; timestamp += 20)
            Check(Read(buffer, 200, 1, 60), timestamp, 0, 0);
        Check(Read(buffer, 200, 1, 60), 80, 300, -300);
    }

    private static void VerifyUnobservedPause()
    {
        var buffer = Started(1);
        Publish(buffer, 0, Pcm(500, 500), 20, 60);
        // Neither a consumer nor an endpoint callback ran during the pause.
        // The clock's explicit generation still reports that missed pause.
        buffer.Publish(0, Pcm(700, 700), false, Wall(40), true, Clock(80, 1, 60), Wall(1080));
        buffer.Publish(0, Pcm(321, -123), false, Wall(1080), true, Clock(100, 1, 60), Wall(1100));
        // A second input's older snapshot must not overwrite current PCM.
        buffer.Publish(0, Pcm(999, 999), false, Wall(1080), true, Clock(100), Wall(1100));
        Check(Read(buffer, 200, 1, 60), 20, 500, 500);
        for (int timestamp = 40; timestamp < 80; timestamp += 20)
            Check(Read(buffer, 200, 1, 60), timestamp, 0, 0);
        Check(Read(buffer, 200, 1, 60), 80, 321, -123);
    }

    private static void VerifyWallClockSkewWithoutPause()
    {
        var buffer = Started(1);
        Publish(buffer, 0, Pcm(456, -654), 20, 60);
        // Scheduling can delay a callback after its clock snapshot. A 10ms
        // elapsed/wall discrepancy does not mean that the user paused.
        buffer.Publish(0, ReadOnlySpan<byte>.Empty, false, Wall(70), true, Clock(60), Wall(70));
        Check(Read(buffer, 140), 20, 456, -654);
    }

    private static void VerifyBoundedBacklog()
    {
        var buffer = Started(1);
        byte[] oversized = Pcm(77, -77, RecordingAudioBuffer.CapacityFrames * 2);
        Publish(buffer, 0, oversized, 20, 2020);
        for (int timestamp = 20; timestamp < 1020; timestamp += 20)
            Check(Read(buffer, 2140), timestamp, 0, 0);
        Check(Read(buffer, 2140), 1020, 77, -77);
        Check(Read(buffer, 2140), 1040, 77, -77);
    }

    private static void VerifyFinalPartialBlock()
    {
        var buffer = Started(1);
        Publish(buffer, 0, Pcm(111, -222, PcmAudioFrame.FramesPerBlock * 2), 20, 80);
        var stopped = new RecordingClockState(TimeSpan.FromMilliseconds(55), false, false, true);
        Require(buffer.TryRead(stopped, out PcmAudioFrame? first) && first is not null,
            "Stopped audio did not drain its queued full block.");
        Check(first!, 20, 111, -222);
        Require(buffer.TryRead(stopped, out PcmAudioFrame? tail) && tail is not null,
            "Stopped audio lost the partial final block.");
        Require(tail!.Timestamp == TimeSpan.FromMilliseconds(40) && tail.Duration == TimeSpan.FromMilliseconds(15)
            && tail.Pcm.Length == 720 * PcmAudioFrame.BytesPerFrame, "Final audio extended beyond the active video duration.");
        Require(buffer.TryRead(stopped, out PcmAudioFrame? end) && end is null,
            "Audio did not end after draining the final active interval.");
        var silent = Started(0);
        Require(silent.TryRead(stopped, out PcmAudioFrame? silence) && silence is not null,
            "An idle system source could not finalize with silence.");
        Check(silence!, 20, 0, 0);
    }

    private static async Task VerifyProviderLifetimeAsync()
    {
        // No inputs are opened: only the same production pacing/lifetime code runs.
        await using var capture = new RecordingAudioCapture(() => default, 0);
        PcmAudioFrame? bootstrap = await capture.GetNextSampleAsync(CancellationToken.None);
        Require(bootstrap is not null, "An audio-first mux request deadlocked before video clock startup.");
        Check(bootstrap!, 0, 0, 0);
        using var cancelled = new CancellationTokenSource();
        Task<PcmAudioFrame?> read = capture.GetNextSampleAsync(cancelled.Token).AsTask();
        cancelled.Cancel();
        try { await read; throw new InvalidOperationException("An idle audio request ignored cancellation."); }
        catch (OperationCanceledException) { }
        Task<PcmAudioFrame?> pending = capture.GetNextSampleAsync(CancellationToken.None).AsTask();
        Require(!pending.IsCompleted, "An idle audio clock ran ahead of the video clock.");
        Task stopped = capture.StopInputsAsync();
        await stopped;
        Require(ReferenceEquals(stopped, capture.StopInputsAsync()), "Stopping inputs did not share one cleanup task.");
        Require(!pending.IsCompleted, "Stopping inputs prematurely ended the audio sample provider.");
        await capture.DisposeAsync();
        Require(await pending.WaitAsync(TimeSpan.FromSeconds(2)) is null,
            "Disposal did not unblock a waiting audio sample provider.");
        Require(!capture.Failure.IsCompleted, "Normal cancellation/disposal was reported as a device failure.");
        await capture.DisposeAsync();
    }

    private static async Task VerifyStoppedInputsDrainAsync()
    {
        var buffer = Started(1);
        Publish(buffer, 0, Pcm(777, -888, PcmAudioFrame.FramesPerBlock * 2), 20, 80);
        var finalClock = new RecordingClockState(TimeSpan.FromMilliseconds(55), false, false, true);
        // The injected production buffer has generated PCM; no native input exists.
        await using var capture = new RecordingAudioCapture(() => finalClock, buffer);
        await capture.StopInputsAsync();
        PcmAudioFrame? full = await capture.GetNextSampleAsync(CancellationToken.None);
        Require(full is not null, "Stopping audio inputs discarded the queued final interval.");
        Check(full!, 20, 777, -888);
        PcmAudioFrame? partial = await capture.GetNextSampleAsync(CancellationToken.None);
        Require(partial is not null && partial.Timestamp == TimeSpan.FromMilliseconds(40)
            && partial.Duration == TimeSpan.FromMilliseconds(15)
            && BinaryPrimitives.ReadInt16LittleEndian(partial.Pcm) == 777,
            "Stopping audio inputs corrupted the partial buffered tail.");
        Require(await capture.GetNextSampleAsync(CancellationToken.None) is null,
            "A stopped audio clock did not end after the preserved tail drained.");
        Require(!capture.Failure.IsCompleted, "Expected input shutdown was reported as an audio failure.");
    }

    private static RecordingAudioBuffer Started(int sources)
    {
        var buffer = new RecordingAudioBuffer(sources);
        Require(buffer.TryRead(default, out PcmAudioFrame? bootstrap) && bootstrap is not null,
            "Audio bootstrap was unavailable before the video clock started.");
        Check(bootstrap!, 0, 0, 0);
        Require(!buffer.TryRead(Clock(20), out _), "Audio incorrectly skipped its input wait.");
        return buffer;
    }

    private static PcmAudioFrame Read(RecordingAudioBuffer buffer, int elapsedMs,
        long pauseGeneration = 0, int pauseElapsedMs = 0)
    {
        Require(buffer.TryRead(Clock(elapsedMs, pauseGeneration, pauseElapsedMs), out PcmAudioFrame? frame) && frame is not null,
            "A due synthetic audio block was unavailable.");
        return frame!;
    }

    private static void Publish(RecordingAudioBuffer buffer, int source, byte[] pcm, int startMs, int elapsedMs)
        => buffer.Publish(source, pcm, false, Wall(startMs), true, Clock(elapsedMs), Wall(elapsedMs));

    private static byte[] Pcm(short left, short right, int frames = PcmAudioFrame.FramesPerBlock)
    {
        byte[] pcm = new byte[frames * PcmAudioFrame.BytesPerFrame];
        for (int offset = 0; offset < pcm.Length; offset += PcmAudioFrame.BytesPerFrame)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), left);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset + 2), right);
        }
        return pcm;
    }

    private static void Check(PcmAudioFrame frame, int timestampMs, short left, short right)
    {
        Require(frame.Timestamp == TimeSpan.FromMilliseconds(timestampMs)
            && frame.Duration == PcmAudioFrame.BlockDuration && frame.Pcm.Length == PcmAudioFrame.BytesPerBlock,
            "Audio sample timestamps/format drifted.");
        for (int offset = 0; offset < frame.Pcm.Length; offset += PcmAudioFrame.BytesPerFrame)
            Require(BinaryPrimitives.ReadInt16LittleEndian(frame.Pcm.AsSpan(offset)) == left
                && BinaryPrimitives.ReadInt16LittleEndian(frame.Pcm.AsSpan(offset + 2)) == right,
                "Audio mixing, quiet-gap placement or source ownership was incorrect.");
    }

    private static long Wall(int milliseconds) => Epoch + TimeSpan.FromMilliseconds(milliseconds).Ticks;
    private static RecordingClockState Clock(int milliseconds, long pauseGeneration = 0, int pauseElapsedMs = 0)
        => new(TimeSpan.FromMilliseconds(milliseconds), true, false, false,
            pauseGeneration, TimeSpan.FromMilliseconds(pauseElapsedMs));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
