using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Storage;

namespace Ovi.Screenshot.Views;

// Published exports and private payload backings have distinct owned filenames.
internal static class ScreenshotFileCache
{
    private const string ExportPrefix = "drag-", BackingPrefix = "payload-";
    private static readonly string DirectoryPath = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OviScreenshot", "DragExports"));
    private static readonly ConcurrentDictionary<string, WeakReference<object>> LivePayloads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static long _nextCleanupUtcTicks;

    internal static async Task<StorageFile> ExportFileAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        string path = await WritePngAsync(png, ExportPrefix, cancellationToken);
        try { return await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken); }
        catch { await Task.Run(() => TryDelete(path)); throw; }
    }

    internal static async Task<string> CreateBackingFileAsync(byte[] png, object owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string path = await WritePngAsync(png, BackingPrefix, cancellationToken);
        LivePayloads[path] = new WeakReference<object>(owner);
        return path;
    }

    internal static Task<byte[]> ReadBackingFileAsync(string path, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        string owned = ValidateOwnedPath(path, BackingPrefix);
        return await File.ReadAllBytesAsync(owned, cancellationToken);
    }, cancellationToken);

    internal static Task DeleteBackingFileAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        string owned = ValidateOwnedPath(path, BackingPrefix);
        // Missing files/directories are already deleted; other failures must reach
        // storage before it drops its resident data or commits the deleted state.
        try { File.Delete(owned); }
        catch (DirectoryNotFoundException) { }
        LivePayloads.TryRemove(owned, out _);
    }, cancellationToken);

    private static Task<string> WritePngAsync(byte[] png, string prefix, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(png);
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new ArgumentException("The screenshot is not a PNG image.", nameof(png));
        ValidateCacheDirectory();
        Directory.CreateDirectory(DirectoryPath);
        await CleanupWhenDueAsync(cancellationToken);
        string path = ValidateOwnedPath(Path.Combine(DirectoryPath, $"{prefix}{Guid.NewGuid():N}.png"), prefix);
        bool created = false;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(png, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            return path;
        }
        catch { if (created) TryDelete(path); throw; }
    }, cancellationToken);

    internal static void DeleteUnpublished(StorageFile file)
    {
        if (IsOwnedFile(file.Path, ExportPrefix)) TryDelete(file.Path);
    }

    private static bool IsOwnedFile(string path, string? requiredPrefix = null)
    {
        string name = Path.GetFileName(path);
        string prefix = requiredPrefix ?? (name.StartsWith(ExportPrefix, StringComparison.Ordinal) ? ExportPrefix : BackingPrefix);
        return name.Length == prefix.Length + 36 && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(".png", StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _)
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath);
    }

    private static string ValidateOwnedPath(string path, string? requiredPrefix = null)
    {
        string fullPath = Path.GetFullPath(path);
        if (!IsOwnedFile(fullPath, requiredPrefix)) throw new ArgumentException("The screenshot file is outside its owned cache.", nameof(path));
        ValidateCacheDirectory();
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("The screenshot cache file was redirected.");
        return fullPath;
    }

    private static void ValidateCacheDirectory()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(DirectoryPath); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                throw new IOException("The screenshot cache directory was redirected.");
        }
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
            ValidateCacheDirectory();
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.png", SearchOption.TopDirectoryOnly))
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
        try { File.Delete(ValidateOwnedPath(path)); }
        catch (IOException error) { Trace.WriteLine(error); }
        catch (UnauthorizedAccessException error) { Trace.WriteLine(error); }
    }
}
