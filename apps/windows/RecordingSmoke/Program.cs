using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Ovi.Screenshot.Capture;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace Ovi.RecordingSmoke;

internal static class Program
{
    [MTAThread]
    private static async Task<int> Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Supply a new absolute .mp4 output path. This test generates synthetic frames only.");
            return 1;
        }
        string path = Path.GetFullPath(args[0]);
        try
        {
            await CheckBufferOwnershipAsync();
            CheckCallbackCleanup();
            var framePool = new VideoFrameBufferPool(160 * 96 * 4);
            VideoFrameLease? latestFixtureFrame = null;
            int frameIndex = 0;
            try
            {
                await using var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, NextFrame, CancellationToken.None);
                await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                await encoder.CompleteAsync();
                Require(encoder.ProcessedSampleCount > 0, "Native Processed events did not release any encoded sample leases.");
            }
            finally { latestFixtureFrame?.Dispose(); }
            Require(framePool.LeasedBufferCount == 0 && framePool.AllocatedCount <= 4, "Encoded sample leases were not returned to the bounded pool.");
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Require(bytes.Length > 1024 && bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8), "MP4 container was not finalized.");
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.GetVideoPropertiesAsync();
            Require(properties.Width == 160 && properties.Height == 96, "Encoded dimensions differ from 160x96.");
            Require(properties.Duration.TotalSeconds is >= 0.85 and <= 1.15, $"Wrong encoded duration: {properties.Duration}.");
            var clip = await MediaClip.CreateFromFileAsync(file);
            Require(clip.GetVideoEncodingProperties().Subtype == "H264", "Output codec is not H.264.");
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(0.5), 160, 96, VideoFramePrecision.NearestFrame);
            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            using var decoded = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            Require(decoded.PixelWidth == 160 && decoded.PixelHeight == 96, "The encoded frame could not be decoded.");
            byte[] decodedPixels = new byte[160 * 96 * 4];
            decoded.CopyToBuffer(decodedPixels.AsBuffer());
            string decodedPath = Path.ChangeExtension(path, ".decoded.png");
            using (var imageStream = File.Create(decodedPath).AsRandomAccessStream())
            {
                var png = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, imageStream);
                png.SetSoftwareBitmap(decoded);
                await png.FlushAsync();
            }
            Console.WriteLine($"Decoded orientation: {properties.Orientation}; corners TL={Corner(16,12)} TR={Corner(144,12)} BL={Corner(16,84)} BR={Corner(144,84)}");
            Require(Color(16, 12, 2) > 180 && Color(16, 12, 0) < 80 && Color(16, 12, 1) < 80, "Video orientation: red top-left marker moved.");
            Require(Color(144, 12, 1) > 180 && Color(144, 12, 0) < 80, "Video orientation: green top-right marker moved.");
            Require(Color(16, 84, 0) > 180 && Color(16, 84, 2) < 80, "Video orientation: blue bottom-left marker moved.");
            Require(Color(144, 84, 2) > 180 && Color(144, 84, 1) > 180 && Color(144, 84, 0) < 80, "Video orientation: yellow bottom-right marker moved.");
            await CheckTimelineTransitionsAsync();
            await CheckPauseResumeEncodingAsync(path);
            await CheckStaticStopAsync(path);
            await CheckEncoderAbortCleanupAsync(path);
            await CheckEncoderFailureCleanupAsync(path);
            Console.WriteLine($"PASS synthetic H.264 MP4; {bytes.Length} bytes; 160x96; {properties.Duration.TotalSeconds:F3}s; frame decoded. No desktop capture. Output: {path}");
            return 0;

            ValueTask<BgraVideoFrame?> NextFrame(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frameIndex == 30) return ValueTask.FromResult<BgraVideoFrame?>(null);
                int current = frameIndex++;
                var lease = framePool.TryRent();
                if (lease is not null)
                {
                    byte[] pixels = lease.Pixels;
                    for (int y = 0; y < 96; y++)
                    for (int x = 0; x < 160; x++)
                    {
                        int offset = (y * 160 + x) * 4;
                        bool movingBlock = x >= current * 3 && x < current * 3 + 24 && y >= 28 && y < 68;
                        pixels[offset] = movingBlock ? (byte)20 : (byte)80;
                        pixels[offset + 1] = movingBlock ? (byte)190 : (byte)45;
                        pixels[offset + 2] = movingBlock ? (byte)240 : (byte)30;
                        pixels[offset + 3] = 255;
                        if (x < 32 && y < 24) { pixels[offset] = 20; pixels[offset + 1] = 20; pixels[offset + 2] = 240; }
                        else if (x >= 128 && y < 24) { pixels[offset] = 20; pixels[offset + 1] = 240; pixels[offset + 2] = 20; }
                        else if (x < 32 && y >= 72) { pixels[offset] = 240; pixels[offset + 1] = 20; pixels[offset + 2] = 20; }
                        else if (x >= 128 && y >= 72) { pixels[offset] = 20; pixels[offset + 1] = 240; pixels[offset + 2] = 240; }
                    }
                    latestFixtureFrame?.Dispose();
                    latestFixtureFrame = lease;
                }
                var timestamp = TimeSpan.FromTicks((long)current * TimeSpan.TicksPerSecond / 30);
                return ValueTask.FromResult<BgraVideoFrame?>(new BgraVideoFrame(latestFixtureFrame!.Retain(), timestamp, Mp4FrameEncoder.FrameInterval));
            }

            byte Color(int x, int y, int channel) => decodedPixels[(y * 160 + x) * 4 + channel];
            string Corner(int x, int y) => $"{Color(x,y,2)},{Color(x,y,1)},{Color(x,y,0)}";
        }
        catch (Exception error) { Console.Error.WriteLine($"FAIL {error}"); return 1; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckStaticStopAsync(string originalPath)
    {
        string path = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + "-static-stop.mp4");
        byte[] pixels = new byte[160 * 96 * 4];
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        using var timeline = new RecordingTimeline();
        timeline.PublishPixels(pixels);
        var firstSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, SampleAsync, CancellationToken.None))
        {
            await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
            timeline.Pause();
            TimeSpan paused = timeline.Elapsed;
            await Task.Delay(80);
            Require(timeline.Elapsed == paused && timeline.IsPaused, "The paused recording clock kept running.");
            timeline.Stop();
            await encoder.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        var file = await StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        Require(properties.Duration.TotalSeconds is >= 0.1 and <= 1.5, "Stopping a static frame source produced the wrong duration.");
        Console.WriteLine($"PASS stop while paused; finalized {properties.Duration.TotalSeconds:F3}s MP4.");

        async ValueTask<BgraVideoFrame?> SampleAsync(CancellationToken cancellationToken)
        {
            BgraVideoFrame? frame = await timeline.GetNextSampleAsync(cancellationToken);
            firstSample.TrySetResult();
            return frame;
        }
    }

    private static async Task CheckTimelineTransitionsAsync()
    {
        using var timeline = new RecordingTimeline();
        byte[] before = SolidBgra(20, 30, 220), during = SolidBgra(220, 20, 20), after = SolidBgra(20, 220, 30);
        timeline.PublishPixels(before);
        using BgraVideoFrame first = (await timeline.GetNextSampleAsync(CancellationToken.None))!;
        Require(first.Timestamp == TimeSpan.Zero, "Recording does not start at zero.");
        timeline.Pause();
        TimeSpan paused = timeline.Elapsed;
        Task<BgraVideoFrame?> pending = timeline.GetNextSampleAsync(CancellationToken.None).AsTask();
        timeline.PublishPixels(during);
        await Task.Delay(100);
        Require(!pending.IsCompleted && timeline.Elapsed == paused, "Pause emitted a sample or added time.");
        timeline.Resume();
        await Task.Delay(50);
        Require(!pending.IsCompleted && timeline.Elapsed == paused, "Resume reused stale pixels before a fresh frame.");
        timeline.PublishPixels(after);
        using BgraVideoFrame resumed = (await pending.WaitAsync(TimeSpan.FromSeconds(2)))!;
        Require(ReferenceEquals(resumed.Pixels, after), "Resume returned the pre-pause or paused frame.");
        Require(resumed.Timestamp > first.Timestamp && resumed.Timestamp < TimeSpan.FromMilliseconds(120), "Paused time leaked into media timestamps.");
        timeline.Pause();
        Task<BgraVideoFrame?> stopped = timeline.GetNextSampleAsync(CancellationToken.None).AsTask();
        timeline.Stop();
        Require(await stopped.WaitAsync(TimeSpan.FromSeconds(1)) is null, "Stop did not wake the paused sample request.");
        Console.WriteLine("PASS shared recording timeline: frozen pause clock, fresh-only resume, monotonic timestamps, stop wakes paused provider.");
    }

    private static async Task CheckPauseResumeEncodingAsync(string originalPath)
    {
        string path = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + "-pause-resume.mp4");
        using var timeline = new RecordingTimeline();
        var pool = new VideoFrameBufferPool(160 * 96 * 4);
        byte[] before = SolidBgra(15, 20, 235), during = SolidBgra(235, 20, 15), after = SolidBgra(15, 220, 20);
        before = PublishPooledFrame(timeline, pool, before);
        var beforeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new ConcurrentQueue<(bool After, bool During, TimeSpan Timestamp)>();
        int beforeCount = 0, afterCount = 0;
        var wall = Stopwatch.StartNew();
        TimeSpan active;
        await using (var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, SampleAsync, CancellationToken.None))
        {
            await beforeReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            timeline.Pause();
            TimeSpan paused = timeline.Elapsed;
            await Task.Delay(60); // Let a sample already supplied before Pause finish its handoff.
            int count = samples.Count;
            during = PublishPooledFrame(timeline, pool, during);
            await Task.Delay(450);
            Require(samples.Count == count && timeline.Elapsed == paused, "Paused MP4 provider emitted frames or counted wall time.");
            timeline.Resume();
            await Task.Delay(60);
            Require(samples.Count == count, "MP4 resume emitted a stale frame before fresh publication.");
            after = PublishPooledFrame(timeline, pool, after);
            await afterReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            active = timeline.Elapsed;
            timeline.Stop();
            await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await encoder.CompleteAsync();
        }
        Require(pool.LeasedBufferCount == 0, "Pause/resume left sample buffers leased after native completion.");
        var frames = samples.ToArray();
        Require(frames.All(frame => !frame.During), "Paused content was encoded.");
        for (int index = 1; index < frames.Length; index++)
            Require(frames[index].Timestamp > frames[index - 1].Timestamp && frames[index].Timestamp - frames[index - 1].Timestamp < TimeSpan.FromMilliseconds(120),
                "Paused interval created a media timestamp gap.");
        var file = await StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        Require(Math.Abs(properties.Duration.TotalSeconds - active.TotalSeconds) < 0.15, "MP4 duration differs from active recording time.");
        Require(properties.Duration < wall.Elapsed - TimeSpan.FromMilliseconds(350), "Paused wall time leaked into MP4 duration.");
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));
        byte[] red = await DecodeCenterAsync(composition, TimeSpan.Zero);
        TimeSpan greenTimestamp = frames.First(frame => frame.After).Timestamp;
        byte[] green = await DecodeCenterAsync(composition, greenTimestamp + TimeSpan.FromMilliseconds(15));
        Require(red[0] > 120 && red[1] < 90 && red[2] < 90, "The pre-pause image did not decode correctly.");
        Require(green[1] > 100 && green[0] < 90 && green[2] < 90, "Resume encoded a stale or black image.");
        Console.WriteLine($"PASS pause/resume MP4: {properties.Duration.TotalSeconds:F3}s active media over {wall.Elapsed.TotalSeconds:F3}s wall time; fresh resumed frame decoded; no pause gap.");

        async ValueTask<BgraVideoFrame?> SampleAsync(CancellationToken cancellationToken)
        {
            BgraVideoFrame? frame = await timeline.GetNextSampleAsync(cancellationToken);
            if (frame is null) return null;
            bool resumed = frame.Pixels[1] > 150;
            bool pausedContent = frame.Pixels[0] > 150;
            samples.Enqueue((resumed, pausedContent, frame.Timestamp));
            if (!resumed && Interlocked.Increment(ref beforeCount) >= 5) beforeReady.TrySetResult();
            if (resumed && Interlocked.Increment(ref afterCount) >= 5) afterReady.TrySetResult();
            return frame;
        }
    }

    private static byte[] PublishPooledFrame(RecordingTimeline timeline, VideoFrameBufferPool pool, byte[] pixels)
    {
        var lease = pool.TryRent() ?? throw new InvalidOperationException("The synthetic frame pool is unexpectedly exhausted.");
        byte[] published = lease.Pixels;
        pixels.CopyTo(published, 0);
        timeline.PublishFrame(lease);
        return published;
    }

    private static async Task CheckBufferOwnershipAsync()
    {
        var pool = new VideoFrameBufferPool(16, 2);
        using var timeline = new RecordingTimeline();
        var first = pool.TryRent()!;
        byte[] firstPixels = first.Pixels;
        firstPixels.AsSpan().Fill(42);
        timeline.PublishFrame(first);
        var sample = (await timeline.GetNextSampleAsync(CancellationToken.None))!;
        var second = pool.TryRent()!;
        second.Pixels.AsSpan().Fill(77);
        timeline.PublishFrame(second);
        Require(pool.TryRent() is null && pool.AllocatedCount == 2, "Pool grew or reused an encoder-owned buffer.");
        Task available = pool.WaitForAvailableAsync(CancellationToken.None);
        Require(!available.IsCompleted, "An exhausted pool did not wait for buffer availability.");
        using (var cancellation = new CancellationTokenSource())
        {
            Task cancelledWait = pool.WaitForAvailableAsync(cancellation.Token);
            cancellation.Cancel();
            bool cancelled = false;
            try { await cancelledWait; }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Stopping could not cancel an exhausted-buffer wait.");
        }
        Require(sample.Pixels.All(value => value == 42), "Replacing the latest frame overwrote the in-flight sample.");
        sample.Dispose();
        await available.WaitAsync(TimeSpan.FromSeconds(1));
        sample.Dispose();
        using (var recycled = pool.TryRent()!)
        {
            Require(ReferenceEquals(recycled.Pixels, firstPixels), "A released frame buffer was not reused.");
            recycled.Pixels.AsSpan().Fill(99);
            using var current = (await timeline.GetNextSampleAsync(CancellationToken.None))!;
            Require(current.Pixels.All(value => value == 77), "Recycling an old buffer changed the current frame.");
        }
        timeline.Stop();
        Require(pool.LeasedBufferCount == 0, "Timeline stop did not release its latest buffer.");
        for (int index = 0; index < 200; index++) pool.TryRent()!.Dispose();
        Require(pool.AllocatedCount == 2, "The bounded buffer pool continued allocating during reuse.");

        var paddedPool = new VideoFrameBufferPool(4 * 4 * 4, 1);
        using var padded = paddedPool.TryRent()!;
        padded.Pixels.AsSpan().Fill(0xcc);
        for (int index = 0; index < 3 * 3 * 4; index++) padded.Pixels[index] = (byte)index;
        padded.PadPackedCrop(3, 3, 4, 4);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            var pixel = padded.Pixels.AsSpan((y * 4 + x) * 4, 4);
            if (x < 3 && y < 3)
            {
                for (int channel = 0; channel < 4; channel++)
                    Require(pixel[channel] == (y * 3 + x) * 4 + channel, "In-place padding moved or reversed crop pixels.");
            }
            else Require(pixel.SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Odd recording padding is not opaque black.");
        }
        Console.WriteLine("PASS bounded frame ownership, delayed release, reuse, idempotent disposal and in-place odd-dimension padding.");
    }

    private static void CheckCallbackCleanup()
    {
        using var gate = new SemaphoreSlim(0, 1);
        Exception? reported = null;
        Mp4FrameEncoder.FinishRequest(() => throw new COMException("Synthetic native completion failure."),
            () => gate.Release(), error => reported = error);
        Require(reported is COMException && gate.Wait(0), "A failed native deferral completion stranded the request gate.");
        bool released = false;
        try
        {
            Mp4FrameEncoder.FinishRequest(() => throw new COMException(), () => released = true,
                _ => throw new InvalidOperationException("Synthetic failure reporter."));
        }
        catch (InvalidOperationException) { }
        Require(released, "The request gate was not released when failure reporting threw.");
        Console.WriteLine("PASS native callback cleanup: deferral/reporting failures always release the request gate.");
    }

    private static async Task CheckEncoderFailureCleanupAsync(string originalPath)
    {
        string path = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + "-failure.mp4");
        var pool = new VideoFrameBufferPool(160 * 96 * 4);
        var source = pool.TryRent()!;
        SolidBgra(20, 40, 210).CopyTo(source.Pixels, 0);
        int count = 0;
        bool failed = false;
        try
        {
            await using var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, NextFrame, CancellationToken.None);
            try { await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (error is not TimeoutException) { failed = true; }
            Require(failed, "A synthetic provider failure was not surfaced by the encoder.");
        }
        finally { source.Dispose(); }
        Require(pool.LeasedBufferCount == 0, "Encoding failure retained in-flight buffer leases after native termination.");
        Console.WriteLine("PASS provider failure: native encoding ends and all sample buffers return to the pool.");

        ValueTask<BgraVideoFrame?> NextFrame(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frame = count++;
            if (frame == 5) throw new InvalidOperationException("Synthetic frame provider failure.");
            return ValueTask.FromResult<BgraVideoFrame?>(new BgraVideoFrame(source.Retain(),
                TimeSpan.FromTicks(frame * Mp4FrameEncoder.FrameInterval.Ticks), Mp4FrameEncoder.FrameInterval));
        }
    }

    private static async Task CheckEncoderAbortCleanupAsync(string originalPath)
    {
        string path = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath) + "-abort.mp4");
        var pool = new VideoFrameBufferPool(160 * 96 * 4);
        using var timeline = new RecordingTimeline();
        PublishPooledFrame(timeline, pool, SolidBgra(20, 180, 40));
        var firstSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, SampleAsync, CancellationToken.None);
        try
        {
            await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(80);
            await Task.WhenAll(encoder.DisposeAsync().AsTask(), encoder.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await encoder.DisposeAsync(); timeline.Stop(); }
        Require(pool.LeasedBufferCount == 0, "Native abort or concurrent disposal retained sample buffer leases.");
        Console.WriteLine("PASS native abort and concurrent disposal: in-flight leases released after native termination.");

        async ValueTask<BgraVideoFrame?> SampleAsync(CancellationToken cancellationToken)
        {
            var frame = await timeline.GetNextSampleAsync(cancellationToken);
            firstSample.TrySetResult();
            return frame;
        }
    }

    private static byte[] SolidBgra(byte blue, byte green, byte red)
    {
        byte[] pixels = new byte[160 * 96 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static async Task<byte[]> DecodeCenterAsync(MediaComposition composition, TimeSpan timestamp)
    {
        using var thumbnail = await composition.GetThumbnailAsync(timestamp, 160, 96, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        int center = (48 * 160 + 80) * 4;
        return pixels.AsSpan(center, 4).ToArray();
    }
}
