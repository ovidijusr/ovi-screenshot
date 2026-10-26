using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Ovi.Screenshot.Capture;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Ovi.RecordingSmoke;

internal static class AudioMuxSmoke
{
    internal static async Task VerifyAsync(string originalPath)
    {
        foreach (int fps in new[] { 15, 24, 30, 60 }) await VerifyFixedRateAsync(originalPath, fps);
        await VerifyPauseAsync(originalPath);
    }

    private static async Task VerifyFixedRateAsync(string originalPath, int fps)
    {
        string path = Sibling(originalPath, $"-{fps}fps-audio.mp4");
        var options = new RecordingOptions(fps, SystemAudio: true);
        byte[] pixels = CornerMarkers();
        int video = 0, audio = 0;
        await using (var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, VideoAsync, options, AudioAsync, CancellationToken.None))
        {
            await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            await encoder.CompleteAsync();
            Require(encoder.ProcessedSampleCount is > 0 && encoder.ProcessedSampleCount <= fps,
                "Video sample accounting included audio or failed to observe native consumption.");
            Require(encoder.ProcessedAudioSampleCount is > 0 and <= 50 && encoder.PeakAudioSampleCount <= Mp4FrameEncoder.AudioSampleCapacity,
                "Audio consumption did not release its bounded in-flight samples.");
        }
        var file = await StorageFile.GetFileFromPathAsync(path);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var videoProperties = clip.GetVideoEncodingProperties();
        double actualFps = videoProperties.FrameRate.Numerator / (double)videoProperties.FrameRate.Denominator;
        Require(Math.Abs(actualFps - fps) < 0.02 && videoProperties.Width == 160 && videoProperties.Height == 96,
            $"Configured {fps} fps/dimensions were not preserved ({actualFps:F3} fps).");
        var audioProperties = (await MediaEncodingProfile.CreateFromFileAsync(file)).Audio;
        Require(audioProperties is not null && string.Equals(audioProperties.Subtype, MediaEncodingSubtypes.Aac, StringComparison.OrdinalIgnoreCase) &&
            audioProperties.SampleRate == PcmAudioFrame.SampleRate && audioProperties.ChannelCount == PcmAudioFrame.Channels,
            "The MP4 does not contain the requested 48 kHz stereo AAC track.");
        Require(clip.OriginalDuration.TotalSeconds is >= 0.9 and <= 1.15, "Muxed clip duration drifted from its generated one-second source.");
        await VerifyVideoCornersAsync(clip);
        byte[] pcm = await DecodePcmAsync(path);
        VerifyTone(pcm, expectedSeconds: 1);
        Console.WriteLine($"PASS {fps} fps H.264 + 48 kHz stereo AAC: exact generated cadence, upright corners, decoded 440/660 Hz tones, bounded audio samples.");

        ValueTask<BgraVideoFrame?> VideoAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int index = video++;
            return ValueTask.FromResult(index >= fps ? null : new BgraVideoFrame(pixels,
                TimeSpan.FromTicks((long)index * TimeSpan.TicksPerSecond / fps), options.FrameInterval));
        }
        ValueTask<PcmAudioFrame?> AudioAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int index = audio++;
            return ValueTask.FromResult(index >= 50 ? null : Tone(index * PcmAudioFrame.FramesPerBlock, PcmAudioFrame.FramesPerBlock));
        }
    }

    private static async Task VerifyPauseAsync(string originalPath)
    {
        string path = Sibling(originalPath, "-audio-pause.mp4");
        var options = new RecordingOptions(24, SystemAudio: true);
        using var timeline = new RecordingTimeline(options.FrameInterval);
        timeline.PublishPixels(CornerMarkers());
        int nextAudioFrame = 0, samples = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wall = Stopwatch.StartNew();
        TimeSpan elapsed;
        await using (var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, VideoAsync, options, AudioAsync, CancellationToken.None))
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(230);
            timeline.Pause();
            var paused = timeline.Clock;
            await Task.Delay(60); // Allow one already supplied sample to finish its handoff.
            int count = Volatile.Read(ref samples);
            await Task.Delay(220);
            Require(timeline.Clock.Elapsed == paused.Elapsed && timeline.Clock.IsPaused && !timeline.Clock.IsRunning &&
                Volatile.Read(ref samples) == count, "Paused mux emitted audio or counted paused wall time.");
            timeline.Resume();
            await Task.Delay(40);
            Require(!timeline.Clock.IsRunning && Volatile.Read(ref samples) == count, "Audio ran before a fresh resumed video frame.");
            timeline.PublishPixels(CornerMarkers());
            await Task.Delay(230);
            timeline.Pause();
            elapsed = timeline.Elapsed;
            timeline.Stop();
            await encoder.CompleteAsync(drainAudio: true).WaitAsync(TimeSpan.FromSeconds(10));
            Require(encoder.PeakAudioSampleCount <= Mp4FrameEncoder.AudioSampleCapacity, "Paused mux exceeded bounded audio retention.");
        }
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        Require(Math.Abs(clip.OriginalDuration.TotalSeconds - elapsed.TotalSeconds) < 0.12 &&
            clip.OriginalDuration < wall.Elapsed - TimeSpan.FromMilliseconds(200), "Paused time leaked into the muxed MP4 duration.");
        VerifyTone(await DecodePcmAsync(path), elapsed.TotalSeconds);
        Console.WriteLine($"PASS audio/video pause/resume and Stop-while-paused: {elapsed.TotalSeconds:F3}s active over {wall.Elapsed.TotalSeconds:F3}s wall; decoded audio has no pause gap.");

        async ValueTask<BgraVideoFrame?> VideoAsync(CancellationToken cancellation)
        {
            var frame = await timeline.GetNextSampleAsync(cancellation);
            first.TrySetResult();
            return frame;
        }
        async ValueTask<PcmAudioFrame?> AudioAsync(CancellationToken cancellation)
        {
            // Synthetic PCM uses the production clock, never opens an audio device.
            if (nextAudioFrame == 0)
            {
                nextAudioFrame = PcmAudioFrame.FramesPerBlock;
                Interlocked.Increment(ref samples);
                return new PcmAudioFrame(new byte[PcmAudioFrame.BytesPerBlock], TimeSpan.Zero, PcmAudioFrame.BlockDuration);
            }
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var clock = timeline.Clock;
                long available = clock.Elapsed.Ticks * PcmAudioFrame.SampleRate / TimeSpan.TicksPerSecond;
                int count = (int)Math.Min(PcmAudioFrame.FramesPerBlock, Math.Max(0, available - nextAudioFrame));
                if (clock.IsStopped && count == 0) return null;
                if ((!clock.IsPaused && clock.IsRunning && count == PcmAudioFrame.FramesPerBlock) || (clock.IsStopped && count > 0))
                {
                    var frame = Tone(nextAudioFrame, count);
                    nextAudioFrame += count;
                    Interlocked.Increment(ref samples);
                    return frame;
                }
                await Task.Delay(2, cancellation);
            }
        }
    }

    private static PcmAudioFrame Tone(int startFrame, int count)
    {
        byte[] pcm = new byte[count * PcmAudioFrame.BytesPerFrame];
        for (int index = 0; index < count; index++)
        {
            double time = (startFrame + index) / (double)PcmAudioFrame.SampleRate;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 4), (short)(Math.Sin(2 * Math.PI * 440 * time) * 12000));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 4 + 2), (short)(Math.Sin(2 * Math.PI * 660 * time) * 12000));
        }
        return new PcmAudioFrame(pcm, TimeSpan.FromTicks((long)startFrame * TimeSpan.TicksPerSecond / PcmAudioFrame.SampleRate),
            TimeSpan.FromTicks((long)count * TimeSpan.TicksPerSecond / PcmAudioFrame.SampleRate));
    }

    private static async Task<byte[]> DecodePcmAsync(string path)
    {
        var source = await StorageFile.GetFileFromPathAsync(path);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var destination = await folder.CreateFileAsync(Path.GetFileNameWithoutExtension(path) + ".decoded.wav", CreationCollisionOption.FailIfExists);
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(PcmAudioFrame.SampleRate, PcmAudioFrame.Channels, PcmAudioFrame.BitsPerSample);
        var transcoder = new MediaTranscoder();
        var prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile);
        Require(prepared.CanTranscode, "Windows could not decode the generated AAC track to PCM.");
        await prepared.TranscodeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        byte[] wav = await File.ReadAllBytesAsync(destination.Path);
        Require(wav.Length > 44 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE", "Decoded output is not a WAV container.");
        bool format = false;
        for (int offset = 12; offset <= wav.Length - 8;)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4));
            int start = offset + 8;
            Require(length <= wav.Length - start, "Decoded WAV contains an invalid chunk.");
            string id = Encoding.ASCII.GetString(wav, offset, 4);
            if (id == "fmt ")
            {
                Require(length >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(start + 2)) == 2 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(start + 4)) == PcmAudioFrame.SampleRate &&
                    BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(start + 14)) == 16, "Decoded audio PCM layout changed.");
                format = true;
            }
            if (id == "data") { Require(format, "WAV has no PCM format."); return wav.AsSpan(start, (int)length).ToArray(); }
            offset = checked(start + (int)length + (int)(length & 1));
        }
        throw new InvalidOperationException("Decoded WAV has no PCM data.");
    }

    private static void VerifyTone(byte[] pcm, double expectedSeconds)
    {
        int frames = pcm.Length / PcmAudioFrame.BytesPerFrame;
        Require(Math.Abs(frames / (double)PcmAudioFrame.SampleRate - expectedSeconds) < 0.12, "Decoded audio duration differs from active recording time.");
        // AAC priming/padded final quanta can ring around zero; inspect the
        // sustained signal, away from both codec boundaries.
        int start = (int)PcmAudioFrame.SampleRate / 10;
        int end = Math.Min(frames, Math.Min((int)(PcmAudioFrame.SampleRate * 0.9),
            (int)(expectedSeconds * PcmAudioFrame.SampleRate) - (int)PcmAudioFrame.SampleRate / 20));
        Require(end > start + 1000, "Decoded audio is too short to verify its tones.");
        for (int channel = 0; channel < 2; channel++)
        {
            double energy = 0;
            int crossings = 0;
            short previous = 0;
            for (int frame = start; frame < end; frame++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * 4 + channel * 2));
                energy += (double)sample * sample;
                if (frame != start && previous < 0 && sample >= 0) crossings++;
                previous = sample;
            }
            double frequency = crossings * PcmAudioFrame.SampleRate / (double)(end - start);
            double rms = Math.Sqrt(energy / (end - start));
            Require(rms > 1000 && Math.Abs(frequency - (channel == 0 ? 440 : 660)) < 25,
                $"Decoded AAC channel {channel} is silent, has a pause gap, or lost its stereo tone ({frequency:F1} Hz, RMS {rms:F0}).");
        }
    }

    private static byte[] CornerMarkers()
    {
        byte[] pixels = new byte[160 * 96 * 4];
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 160; x++)
        {
            int index = (y * 160 + x) * 4;
            pixels[index] = y >= 48 && x < 80 ? (byte)240 : (byte)20;
            pixels[index + 1] = x >= 80 ? (byte)240 : (byte)20;
            pixels[index + 2] = (y < 48 && x < 80) || (y >= 48 && x >= 80) ? (byte)240 : (byte)20;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static async Task VerifyVideoCornersAsync(MediaClip clip)
    {
        var composition = new MediaComposition(); composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(.5), 160, 96, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        bool Marker(int x, int y, int channel) => pixels[(y * 160 + x) * 4 + channel] > 150;
        Require(Marker(40, 24, 0) && Marker(120, 24, 1) && Marker(40, 72, 2) && Marker(120, 72, 0) && Marker(120, 72, 1),
            "Audio mux changed video channel order or top-down orientation.");
    }

    private static string Sibling(string path, string suffix) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
