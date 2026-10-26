using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Services;
using Microsoft.UI.Dispatching;

namespace Ovi.Screenshot.Testing;

internal static class ServiceSmoke
{
    internal static async Task VerifyAsync(string directory)
    {
        var paths = new AppPaths(directory);
        Directory.CreateDirectory(paths.Root);
        await VerifySettingsAsync(paths);
        await LogSmoke.VerifyAsync(Path.Combine(paths.Root, "logs"));
        await VerifyRecoveryLifetimeAsync(paths);
        await VerifyRecordingRecoveryAsync(paths);
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

    private static async Task VerifySettingsAsync(AppPaths paths)
    {
        var store = new SettingsStore(paths.Settings);
        Require(store.Load().Shortcut == SettingsStore.DefaultShortcut, "Missing settings did not use the default.");
        await store.SaveAsync("Shift+Win+3");
        Require(store.Load() is { Shortcut: "Shift+Win+3", Warning: null }, "Settings did not survive reload.");
        string saved = await File.ReadAllTextAsync(paths.Settings);
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BrokenSettings
    {
        public string Shortcut => throw new IOException("Synthetic settings serialization failure.");
    }
}
