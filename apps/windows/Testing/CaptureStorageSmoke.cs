using Ovi.Screenshot.Services;

namespace Ovi.Screenshot.Testing;

internal static class CaptureStorageSmoke
{
    // The caller supplies an isolated fixture directory, never the user's Pictures folder.
    internal static async Task VerifyAsync(string directory)
    {
        string root = System.IO.Path.Combine(directory, "auto-saves");
        var storage = new CaptureStorage(root);
        Require(!Directory.Exists(root), "Constructing storage created the destination folder.");
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await storage.SaveScreenshotAsync(png, cancellation.Token); throw new InvalidOperationException("Cancelled save succeeded."); }
            catch (OperationCanceledException) { }
        }
        Require(!Directory.Exists(root), "A cancelled save created the destination folder.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var release = new ManualResetEventSlim())
        {
            var slow = new CaptureStorage(System.IO.Path.Combine(directory, "slow-save"), () =>
            {
                entered.TrySetResult();
                release.Wait();
                return Guid.NewGuid();
            });
            Task<StoredCaptureFile>? pending = null;
            Task caller = Task.Run(() => { pending = slow.SaveScreenshotAsync(png); returned.TrySetResult(); });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bool didReturn = await Task.WhenAny(returned.Task, Task.Delay(500)) == returned.Task;
                release.Set();
                await caller;
                await pending!;
                Require(didReturn, "Saving blocked the calling thread on filesystem work.");
            }
            finally { release.Set(); }
        }

        StoredCaptureFile[] captures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => storage.SaveScreenshotAsync(png)));
        Require(captures.Select(capture => capture.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == captures.Length,
            "Concurrent screenshots did not receive unique names.");
        foreach (var capture in captures)
            Require((await File.ReadAllBytesAsync(capture.Path)).AsSpan().SequenceEqual(png), "A saved screenshot changed its bytes.");

        string source = System.IO.Path.Combine(directory, "generated-recording.mp4");
        byte[] video = new byte[1024 * 1024];
        Random.Shared.NextBytes(video);
        await File.WriteAllBytesAsync(source, video);
        var savedVideo = await storage.SaveRecordingAsync(source);
        Require(File.Exists(source) && (await File.ReadAllBytesAsync(savedVideo.Path)).AsSpan().SequenceEqual(video),
            "Saving a recording changed or removed its source.");
        string independent = System.IO.Path.Combine(directory, "independent.mp4");
        File.Copy(savedVideo.Path, independent);
        await savedVideo.DeleteAsync();
        await savedVideo.DeleteAsync();
        Require(!File.Exists(savedVideo.Path) && File.Exists(source) && File.Exists(independent),
            "Deleting a capture removed its source or independent copy.");

        var replacement = captures[0];
        string moved = System.IO.Path.Combine(directory, "moved-original.png");
        File.Move(replacement.Path, moved);
        byte[] sentinel = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(replacement.Path, sentinel);
        bool refused = false;
        try { await replacement.DeleteAsync(); }
        catch (IOException) { refused = true; }
        Require(refused && (await File.ReadAllBytesAsync(replacement.Path)).AsSpan().SequenceEqual(sentinel),
            "Deleting an old capture removed a replacement at the same path.");

        Guid fixedId = Guid.Parse("8bbd71ed-8e8d-4733-96d9-eeb081873a59");
        var collision = new CaptureStorage(root, () => fixedId, () => new DateTime(2026, 1, 1));
        var existing = await collision.SaveScreenshotAsync(png);
        byte[] secondBytes = [9, 9, 9];
        var second = await collision.SaveScreenshotAsync(secondBytes);
        Require(existing.Path != second.Path && (await File.ReadAllBytesAsync(existing.Path)).AsSpan().SequenceEqual(png)
            && (await File.ReadAllBytesAsync(second.Path)).AsSpan().SequenceEqual(secondBytes),
            "A duplicate save replaced an existing capture or failed to choose another name.");

        bool missingRejected = false;
        try { await storage.SaveRecordingAsync(System.IO.Path.Combine(directory, "missing.mp4")); }
        catch (FileNotFoundException) { missingRejected = true; }
        Require(missingRejected && File.Exists(source) && !Directory.EnumerateFiles(root, ".ovi-*.tmp").Any(),
            "A failed save lost its recording source or left a partial file.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
