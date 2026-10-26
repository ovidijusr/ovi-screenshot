using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Services;
using Microsoft.UI.Dispatching;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;
using System.Buffers.Binary;

namespace Ovi.Screenshot.Testing;

internal static class ServiceSmoke
{
    internal static async Task VerifyAsync(string directory)
    {
        var paths = new AppPaths(directory);
        Directory.CreateDirectory(paths.Root);
        await VerifyCaptureSoundAsync();
        await VerifySettingsAsync(paths);
        await VerifyRecordingPreferencesAsync(paths);
        await CaptureStorageSmoke.VerifyAsync(Path.Combine(paths.Root, "capture-fixture"));
        await LogSmoke.VerifyAsync(Path.Combine(paths.Root, "logs"));
        await VerifyRecoveryLifetimeAsync(paths);
        await VerifyRecordingRecoveryAsync(paths);
    }

    private static async Task VerifyCaptureSoundAsync()
    {
        byte[] wave = await File.ReadAllBytesAsync(CaptureSound.AssetPath);
        Require(wave.Length > 44 && wave.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && wave.AsSpan(8, 4).SequenceEqual("WAVE"u8) && wave.AsSpan(36, 4).SequenceEqual("data"u8),
            "The capture sound was missing or was not the expected WAV asset.");
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(24, 4));
        Require(sampleRate == 48000 && BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(22, 2)) == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(34, 2)) == 16,
            "The capture sound format changed unexpectedly.");
        int samples = (wave.Length - 44) / 2;
        int peak = 0;
        for (int index = 44; index < wave.Length; index += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(index, 2))));
        Require(samples / (double)sampleRate is >= 0.1 and <= 0.2 && peak is > 0 and < 8192
            && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44, 2)) == 0
            && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(wave.Length - 2, 2)) == 0,
            "The capture sound exceeded its short/quiet envelope or gained abrupt endpoints.");
    }

    private static async Task VerifyRecoveryLifetimeAsync(AppPaths paths)
    {
        var log = new AppLog(paths);
        var library = new RecordingLibrary(paths, log);
        var load = new TaskCompletionSource<IReadOnlyList<StoredRecording>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0, captureRequests = 0;
        var controller = new RecordingController(DispatcherQueue.GetForCurrentThread(), library, log,
            error => throw error, _ => Task.FromResult(false), _ => { requests++; return load.Task; });
        controller.CaptureRequested += () => captureRequests++;
        Require(controller.IsBusy, "A new controller accepted work before recovery started.");
        Task recovery = controller.RecoverAsync();
        Require(ReferenceEquals(recovery, controller.RecoverAsync()) && requests == 1,
            "Recovery was duplicated instead of sharing its initialization task.");
        controller.Toggle();
        Require(controller.IsBusy && captureRequests == 0, "Recording selection started while recovery was pending.");
        bool startRejected = false;
        try { await controller.StartAsync(0, default, default, default, CancellationToken.None); }
        catch (InvalidOperationException) { startRejected = true; }
        Require(startRejected, "Recording started before pending work was recovered.");
        load.SetResult([]);
        await recovery;
        controller.Toggle();
        Require(!controller.IsBusy && captureRequests == 1, "Successful empty recovery did not enable recording.");
        await controller.ShutdownAsync();

        var late = new TaskCompletionSource<IReadOnlyList<StoredRecording>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken recoveryCancellation = default;
        var closing = new RecordingController(DispatcherQueue.GetForCurrentThread(), library, log,
            error => throw error, _ => Task.FromResult(false), cancellation => { recoveryCancellation = cancellation; return late.Task; });
        Task lateRecovery = closing.RecoverAsync();
        Task shutdown = closing.ShutdownAsync();
        Require(recoveryCancellation.IsCancellationRequested && !shutdown.IsCompleted,
            "Shutdown did not cancel and drain pending recovery.");
        late.SetResult([new StoredRecording(Path.Combine(paths.Recordings, "late.mp4"), TimeSpan.FromSeconds(1))]);
        await Task.WhenAll(lateRecovery, shutdown);
        Require(!closing.HasPendingRecording && ReferenceEquals(shutdown, closing.ShutdownAsync()),
            "Late recovery repopulated a stopped controller or restarted shutdown.");

        var cancelled = new RecordingController(DispatcherQueue.GetForCurrentThread(), library, log,
            error => throw error, _ => Task.FromResult(false), async cancellation =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return Array.Empty<StoredRecording>();
            });
        Task cancelledRecovery = cancelled.RecoverAsync();
        await cancelled.ShutdownAsync();
        await cancelledRecovery;
        Require(!cancelled.HasPendingRecording, "Cancelled recovery restored pending work.");
    }

    private static async Task VerifyRecordingPreferencesAsync(AppPaths paths)
    {
        var store = new SettingsStore(paths.Settings);
        var window = new SettingsWindow();
        var controller = new RecordingSettingsController(store, window, store.Load().Recording, error => throw error);
        try
        {
            var chosen = new RecordingOptions(60, true, true, "fixture-output", "fixture-mic");
            controller.Apply(chosen);
            Require(await controller.ForRecordingAsync(CancellationToken.None) == chosen,
                "The next recording did not wait for its accepted preferences.");
            var saved = store.Load();
            Require(saved.Shortcut == "Shift+Win+3" && saved.Recording == chosen,
                "Recording toolbar preferences did not survive reload alongside the shortcut.");
        }
        finally { await controller.StopAsync(); window.Close(); }
    }

    private static async Task VerifySettingsAsync(AppPaths paths)
    {
        var store = new SettingsStore(paths.Settings);
        Require(store.Load().Shortcut == SettingsStore.DefaultShortcut, "Missing settings did not use the default.");
        await File.WriteAllTextAsync(paths.Settings, """{"Shortcut":"Shift+Win+3"}""");
        Require(store.Load() is { Shortcut: "Shift+Win+3", Warning: null } legacy
            && legacy.Recording == new RecordingOptions(), "Legacy settings lost their shortcut or recording defaults.");
        await store.SaveAsync("Shift+Win+3");
        Require(store.Load() is { Shortcut: "Shift+Win+3", Warning: null }, "Settings did not survive reload.");
        Require(store.Load().Recording == new RecordingOptions(), "Legacy shortcut settings did not receive recording defaults.");
        var recording = new RecordingOptions(60, true, true, "fixture-output", "fixture-input");
        await Task.WhenAll(store.SaveRecordingAsync(recording), store.SaveAsync("Ctrl+Shift+5"));
        var combined = store.Load();
        Require(combined.Shortcut == "Ctrl+Shift+5" && combined.Recording == recording,
            "Concurrent shortcut and recording updates replaced each other's settings.");
        await store.SaveAsync("Shift+Win+3");
        Require(store.Load().Recording == recording, "Rebinding the capture key reset recording preferences.");
        string saved = await File.ReadAllTextAsync(paths.Settings);
        bool invalidFps = false;
        try { await store.SaveRecordingAsync(new RecordingOptions(0)); }
        catch (ArgumentException) { invalidFps = true; }
        Require(invalidFps && await File.ReadAllTextAsync(paths.Settings) == saved,
            "Invalid recording settings replaced saved preferences.");
        await File.WriteAllTextAsync(paths.Settings, """{"Shortcut":"Shift+Win+3","Recording":{"FramesPerSecond":0}}""");
        Require(store.Load() is { Shortcut: "Shift+Win+3", Warning: not null } invalid
            && invalid.Recording == new RecordingOptions(), "Invalid recording preferences reset a valid shortcut.");
        await File.WriteAllTextAsync(paths.Settings, saved);
        bool rejected = false;
        try { await AtomicJsonFile.WriteAsync(paths.Settings, new BrokenSettings()); }
        catch { rejected = true; }
        Require(rejected && await File.ReadAllTextAsync(paths.Settings) == saved, "Failed settings serialization replaced valid settings.");
        Require(!Directory.EnumerateFiles(paths.Root, "*.tmp").Any(), "Atomic settings left a temporary file after failure.");
        await File.WriteAllTextAsync(paths.Settings, "{broken");
        Require(store.Load() is { Shortcut: SettingsStore.DefaultShortcut, Warning: not null }, "Malformed settings prevented default startup.");
        await store.SaveAsync("Shift+Win+3");
    }

    private static async Task VerifyRecordingRecoveryAsync(AppPaths paths)
    {
        var log = new AppLog(paths);
        var library = new RecordingLibrary(paths, log);
        string path = library.NewPath();
        byte[] pixels = new byte[160 * 96 * 4];
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        int frame = 0;
        await using (var encoder = await Mp4FrameEncoder.StartAsync(160, 96, path, NextSample, CancellationToken.None))
        {
            await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await encoder.CompleteAsync();
        }
        await VideoPreviewSmoke.VerifyAsync(path, Path.Combine(paths.Root, "video-preview-fixture"), Win32.PointerMonitor().Info.Work);
        await VerifyRecordingAutoSaveAsync(paths, library, log, path);
        var expected = await library.RememberAsync(path, TimeSpan.FromMilliseconds(200));
        var recovered = await new RecordingLibrary(paths, log).RecoverAsync();
        Require(recovered.Count == 1 && recovered[0].Path == expected.Path && recovered[0].Recovered, "Completed recording was not recovered after reopening its store.");
        var controller = new RecordingController(DispatcherQueue.GetForCurrentThread(), library, log,
            error => throw error, _ => Task.FromResult(false));
        await controller.RecoverAsync();
        Require(controller.HasPendingRecording, "Recording controller lost recovered work.");
        await controller.ShutdownAsync();
        await controller.ShutdownAsync();
        Require(File.Exists(path), "Shutdown discarded a recording that had not been saved.");

        string invalid = library.NewPath();
        await File.WriteAllBytesAsync(invalid, [0, 1, 2, 3]);
        Require((await library.RecoverAsync()).Count == 1, "An incomplete recording prevented recovery of valid clips.");
        await library.RememberAsync(invalid, TimeSpan.FromSeconds(30));
        Require((await library.RecoverAsync()).Count == 1, "Stale metadata restored a damaged recording as pending work.");
        await library.DeleteAsync(invalid);

        string validMetadata = Path.ChangeExtension(path, ".json");
        await File.WriteAllTextAsync(validMetadata, "{broken");
        Require((await library.RecoverAsync()).Count == 1, "Malformed metadata hid a valid completed recording.");
        await library.RememberAsync(path, expected.Duration);

        string expired = library.NewPath();
        File.Copy(path, expired);
        await library.RememberAsync(expired, expected.Duration);
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
        await library.RecoverAsync();
        Require(!File.Exists(expired) && !File.Exists(Path.ChangeExtension(expired, ".json")), "Expired cached recording was not removed.");

        string metadata = Path.ChangeExtension(path, ".json");
        File.SetAttributes(metadata, FileAttributes.ReadOnly);
        try { await library.DeleteAsync(path); }
        finally { if (File.Exists(metadata)) File.SetAttributes(metadata, FileAttributes.Normal); }
        Require(!File.Exists(path) && (await library.RecoverAsync()).Count == 0, "Metadata cleanup failure left a discarded recording pending.");
        bool outsideRejected = false;
        try { await library.DeleteAsync(Path.Combine(paths.Root, "outside.mp4")); }
        catch (ArgumentException) { outsideRejected = true; }
        Require(outsideRejected, "Recording cleanup accepted a path outside its owned cache.");

        ValueTask<BgraVideoFrame?> NextSample(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (frame == 6) return ValueTask.FromResult<BgraVideoFrame?>(null);
            return ValueTask.FromResult<BgraVideoFrame?>(new BgraVideoFrame(pixels,
                TimeSpan.FromTicks(frame++ * Mp4FrameEncoder.FrameInterval.Ticks), Mp4FrameEncoder.FrameInterval));
        }
    }

    private static async Task VerifyRecordingAutoSaveAsync(AppPaths paths, RecordingLibrary library, AppLog log, string generatedVideo)
    {
        string cached = library.NewPath();
        File.Copy(generatedVideo, cached);
        var pending = await library.RememberAsync(cached, TimeSpan.FromMilliseconds(200));
        var storage = new CaptureStorage(Path.Combine(paths.Root, "recording-auto-save"));
        StoredCaptureFile? saved = null;
        var controller = new RecordingController(DispatcherQueue.GetForCurrentThread(), library, log,
            error => throw error, _ => Task.FromResult(false),
            _ => Task.FromResult<IReadOnlyList<StoredRecording>>([pending]), storage);
        controller.RecordingSaved = (capture, _) => { saved = capture; return Task.CompletedTask; };
        await controller.RecoverAsync();
        Require(saved is not null && File.Exists(saved.Path) && !File.Exists(cached) && !controller.HasPendingRecording,
            "Recovered recording was not automatically saved and released from the cache.");
        byte[] savedBytes = await File.ReadAllBytesAsync(saved!.Path);
        byte[] sourceBytes = await File.ReadAllBytesAsync(generatedVideo);
        Require(savedBytes.AsSpan().SequenceEqual(sourceBytes),
            "Automatically saved recording did not preserve the source bytes.");
        await controller.ShutdownAsync();

        string blocked = Path.Combine(paths.Root, "unavailable-destination");
        await File.WriteAllTextAsync(blocked, "fixture");
        string retry = library.NewPath();
        File.Copy(generatedVideo, retry);
        var retryPending = await library.RememberAsync(retry, TimeSpan.FromMilliseconds(200));
        var failing = new RecordingController(DispatcherQueue.GetForCurrentThread(), library, log,
            error => throw error, _ => Task.FromResult(false),
            _ => Task.FromResult<IReadOnlyList<StoredRecording>>([retryPending]), new CaptureStorage(blocked));
        await failing.RecoverAsync();
        Require(failing.HasPendingRecording && File.Exists(retry),
            "A failed automatic save lost its recoverable recording.");
        await failing.ShutdownAsync();
        Require(File.Exists(retry), "Shutdown discarded a recording after automatic save failed.");
        await library.DeleteAsync(retry);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BrokenSettings
    {
        public string Shortcut => throw new IOException("Synthetic settings serialization failure.");
    }
}
