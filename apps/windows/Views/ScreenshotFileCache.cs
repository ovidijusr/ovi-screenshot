using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Storage;

namespace Ovi.Screenshot.Views;

// Durable handoff files are shared by open/drag and resident-PNG spilling, not UI controls.
internal static class ScreenshotFileCache
{
    private static readonly string DirectoryPath = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OviScreenshot", "DragExports"));
    private static readonly ConcurrentDictionary<string, WeakReference<object>> LivePayloads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static long _nextCleanupUtcTicks;

    internal static void KeepAlive(string path, object owner) => LivePayloads[path] = new WeakReference<object>(owner);

    internal static async Task<StorageFile> ExportFileAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new ArgumentException("The screenshot is not a PNG image.", nameof(png));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        await CleanupWhenDueAsync(cancellationToken);
        string path = Path.Combine(DirectoryPath, $"drag-{Guid.NewGuid():N}.png");
        bool created = false;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(png, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            return await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
        }
        catch { if (created) TryDelete(path); throw; }
    }

    internal static void DeleteUnpublished(StorageFile file)
    {
        if (IsOwnedFile(file.Path)) TryDelete(file.Path);
    }

    private static bool IsOwnedFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length == 41 && name.StartsWith("drag-", StringComparison.Ordinal) &&
            name.EndsWith(".png", StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan(5, 32), "N", out _) &&
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath);
    }

    private static async Task CleanupWhenDueAsync(CancellationToken cancellationToken)
    {
        long now = DateTime.UtcNow.Ticks, due = Volatile.Read(ref _nextCleanupUtcTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextCleanupUtcTicks, now + TimeSpan.TicksPerDay, due) != due) return;
        try { await Task.Run(() => CleanupExpiredFiles(new DateTime(now, DateTimeKind.Utc) - Retention), cancellationToken); }
        catch { Interlocked.Exchange(ref _nextCleanupUtcTicks, 0); throw; }
    }

    private static void CleanupExpiredFiles(DateTime cutoffUtc)
    {
        try
        {
            if ((File.GetAttributes(DirectoryPath) & System.IO.FileAttributes.ReparsePoint) != 0) return;
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "drag-*.png", SearchOption.TopDirectoryOnly))
            {
                if (!IsOwnedFile(path)) continue;
                if (LivePayloads.TryGetValue(path, out var owner))
                {
                    if (owner.TryGetTarget(out _)) continue;
                    LivePayloads.TryRemove(path, out _);
                }
                try
                {
                    if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                    if (File.GetLastWriteTimeUtc(path) < cutoffUtc) TryDelete(path);
                }
                catch (IOException error) { Trace.WriteLine(error); }
                catch (UnauthorizedAccessException error) { Trace.WriteLine(error); }
            }
        }
        catch (IOException error) { Trace.WriteLine(error); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException error) { Trace.WriteLine(error); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error); }
    }
}
