using System.Text.Json;
using Windows.Storage;

namespace Ovi.Screenshot.Services;

internal sealed record StoredRecording(string Path, TimeSpan Duration, bool Recovered = false);

internal sealed class RecordingLibrary(AppPaths paths, AppLog log)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    internal string NewPath()
    {
        Directory.CreateDirectory(paths.Recordings);
        return System.IO.Path.Combine(paths.Recordings, $"Recording-{Guid.NewGuid():N}.mp4");
    }

    internal async Task<StoredRecording> RememberAsync(string path, TimeSpan duration)
    {
        ValidatePath(path);
        try { await AtomicJsonFile.WriteAsync(MetadataPath(path), new Metadata(duration.Ticks)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Write(error); }
        return new(path, duration);
    }

    internal async Task<IReadOnlyList<StoredRecording>> RecoverAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var recovered = new List<StoredRecording>();
        Directory.CreateDirectory(paths.Recordings);
        foreach (string path in Directory.EnumerateFiles(paths.Recordings, "Recording-*.mp4"))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                ValidatePath(path);
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - Retention)
                {
                    await DeleteAsync(path);
                    continue;
                }
                TimeSpan duration = await ReadDurationAsync(path, cancellation);
                if (duration > TimeSpan.Zero) recovered.Add(new(path, duration, Recovered: true));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                log.Write(error);
            }
        }
        return recovered;
    }

    internal async Task<StoredRecording?> FindRecoverableAsync(string path)
    {
        try
        {
            ValidatePath(path);
            TimeSpan duration = await ReadVideoDurationAsync(path);
            return duration > TimeSpan.Zero ? await RememberAsync(path, duration) : null;
        }
        catch (Exception error) { log.Write(error); return null; }
    }

    internal Task DeleteAsync(string path)
    {
        ValidatePath(path);
        return Task.Run(() =>
        {
            File.Delete(path);
            try { File.Delete(MetadataPath(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Write(error); }
        });
    }

    private async Task<TimeSpan> ReadDurationAsync(string path, CancellationToken cancellation)
    {
        TimeSpan videoDuration = await ReadVideoDurationAsync(path, cancellation);
        if (videoDuration <= TimeSpan.Zero) return TimeSpan.Zero;
        if (File.Exists(MetadataPath(path)))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(MetadataPath(path), cancellation));
                if (metadata is { DurationTicks: > 0 }) return TimeSpan.FromTicks(metadata.DurationTicks);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { log.Write(error); }
        }
        return videoDuration;
    }

    private static async Task<TimeSpan> ReadVideoDurationAsync(string path, CancellationToken cancellation = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation);
        var video = await file.Properties.GetVideoPropertiesAsync().AsTask(cancellation);
        return video.Width > 0 && video.Height > 0 ? video.Duration : TimeSpan.Zero;
    }

    private void ValidatePath(string path)
    {
        string directory = System.IO.Path.GetFullPath(paths.Recordings);
        string fullPath = System.IO.Path.GetFullPath(path);
        string name = System.IO.Path.GetFileName(fullPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetDirectoryName(fullPath), directory) ||
            name.Length != 46 || !name.StartsWith("Recording-", StringComparison.Ordinal) ||
            !name.EndsWith(".mp4", StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(10, 32), "N", out _))
            throw new ArgumentException("The recording is outside this app's cache.", nameof(path));
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("A recording cache entry was redirected.");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recording cache directory was redirected.");
    }

    private static string MetadataPath(string path) => System.IO.Path.ChangeExtension(path, ".json");
    private sealed record Metadata(long DurationTicks);
}
