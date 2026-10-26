using System.Text;

namespace Ovi.Screenshot.Services;

internal sealed class AppLog(AppPaths paths)
{
    internal const int MaxFileBytes = 2 * 1024 * 1024;
    internal const int MaxEntryBytes = 32 * 1024;
    internal const int MaxMessageChars = 8 * 1024;
    internal static AppLog Shared { get; } = new(new AppPaths());
    private readonly object _gate = new();

    internal void Write(Exception error) => Write(error.ToString());

    internal void Write(string message)
    {
        try
        {
            byte[] entry = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} {BoundMessage(message)}\n");
            lock (_gate)
            {
                Directory.CreateDirectory(paths.Root);
                RotateIfNeeded(entry.Length);
                using var stream = File.Open(paths.Log, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(entry);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RotateIfNeeded(int entryBytes)
    {
        string previous = paths.Log + ".previous";
        // Old timing writers were unbounded. Do not preserve an oversized legacy
        // file as an archive outside the new retention limit.
        if (File.Exists(previous) && new FileInfo(previous).Length > MaxFileBytes) File.Delete(previous);
        if (!File.Exists(paths.Log)) return;
        long currentBytes = new FileInfo(paths.Log).Length;
        if (currentBytes > MaxFileBytes) File.Delete(paths.Log);
        else if (currentBytes + entryBytes > MaxFileBytes) File.Move(paths.Log, previous, overwrite: true);
    }

    private static string BoundMessage(string message)
    {
        if (message.Length <= MaxMessageChars) return message;
        int length = MaxMessageChars;
        if (char.IsHighSurrogate(message[length - 1])) length--;
        return message[..length] + " … [truncated]";
    }
}
