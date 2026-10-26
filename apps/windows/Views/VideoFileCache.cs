using System.Diagnostics;
using System.Collections.Concurrent;
using Windows.Storage;

namespace Ovi.Screenshot.Views;

// Independent exports remain available after the user deletes or closes a preview.
internal static class VideoFileCache
{
    private const string Prefix = "video-";
    private static readonly string DirectoryPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OviScreenshot", "VideoExports"));
    private static long _nextCleanupUtcTicks;
    private static readonly ConcurrentDictionary<string, WeakReference<object>> LiveExports = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<StorageFile> ExportAsync(string sourcePath, object owner, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string path = await Task.Run(async () =>
        {
            ValidateDirectory();
            Directory.CreateDirectory(DirectoryPath);
            CleanupWhenDue();
            string destination = OwnedPath(System.IO.Path.Combine(DirectoryPath, $"{Prefix}{Guid.NewGuid():N}.mp4"));
            bool created = false;
            try
            {
                await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    created = true;
                    await source.CopyToAsync(target, cancellation);
                    await target.FlushAsync(cancellation);
                }
                LiveExports[destination] = new WeakReference<object>(owner);
                return destination;
            }
            catch { if (created) TryDelete(destination); throw; }
        }, cancellation);
        try { return await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation); }
        catch { await Task.Run(() => TryDelete(path)); throw; }
    }

    private static void CleanupWhenDue()
    {
        long now = DateTime.UtcNow.Ticks, due = Volatile.Read(ref _nextCleanupUtcTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextCleanupUtcTicks, now + TimeSpan.TicksPerDay, due) != due) return;
        try
        {
            foreach (var pair in LiveExports)
                if (!pair.Value.TryGetTarget(out _)) LiveExports.TryRemove(pair.Key, out _);
            var cutoff = new DateTime(now, DateTimeKind.Utc) - TimeSpan.FromDays(7);
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "video-*.mp4", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string owned = OwnedPath(path);
                    if (LiveExports.TryGetValue(owned, out var owner))
                    {
                        if (owner.TryGetTarget(out _)) continue;
                        LiveExports.TryRemove(owned, out _);
                    }
                    if (File.GetLastWriteTimeUtc(owned) < cutoff) TryDelete(owned);
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _nextCleanupUtcTicks, 0);
            Trace.WriteLine(error);
        }
    }

    private static string OwnedPath(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        string name = System.IO.Path.GetFileName(fullPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetDirectoryName(fullPath), DirectoryPath) ||
            name.Length != Prefix.Length + 36 || !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !name.EndsWith(".mp4", StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(Prefix.Length, 32), "N", out _))
            throw new ArgumentException("The video export is outside the app cache.", nameof(path));
        ValidateDirectory();
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("The video export was redirected.");
        return fullPath;
    }

    private static void ValidateDirectory()
    {
        for (DirectoryInfo? directory = new(DirectoryPath); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                throw new IOException("The video export directory was redirected.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(OwnedPath(path)); LiveExports.TryRemove(path, out _); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }
}
