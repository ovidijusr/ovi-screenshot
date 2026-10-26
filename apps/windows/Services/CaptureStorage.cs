using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ovi.Screenshot.Services;

// Each published file is independent of the app's temporary capture cache.
internal sealed class CaptureStorage
{
    private readonly string _directory;
    private readonly Func<Guid> _nextId;
    private readonly Func<DateTime> _now;

    internal CaptureStorage(string? directory = null, Func<Guid>? nextId = null, Func<DateTime>? now = null)
    {
        if (directory is null)
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) throw new IOException("The Pictures folder is unavailable.");
            directory = System.IO.Path.Combine(pictures, "Ovi Screenshot");
        }
        _directory = System.IO.Path.GetFullPath(directory);
        _nextId = nextId ?? Guid.NewGuid;
        _now = now ?? (() => DateTime.Now);
    }

    internal Task<StoredCaptureFile> SaveScreenshotAsync(byte[] png, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0) throw new ArgumentException("The screenshot is empty.", nameof(png));
        return Task.Run(() => PublishAsync("Screenshot", ".png",
            (destination, token) => destination.WriteAsync(png, token).AsTask(), cancellation), cancellation);
    }

    internal Task<StoredCaptureFile> SaveRecordingAsync(string sourcePath, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = System.IO.Path.GetFullPath(sourcePath);
        return Task.Run(() => PublishAsync("Recording", ".mp4", async (destination, token) =>
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(destination, 65536, token).ConfigureAwait(false);
        }, cancellation), cancellation);
    }

    private async Task<StoredCaptureFile> PublishAsync(string label, string extension,
        Func<FileStream, CancellationToken, Task> write, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        string id = _nextId().ToString("N");
        string stamp = _now().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        string temporary = System.IO.Path.Combine(_directory, $".ovi-{id}.tmp");
        bool created = false;
        OwnedFileIdentity? identity = null;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                identity = OwnedFileIdentity.Read(stream.SafeFileHandle);
                await write(stream, cancellation).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
            }
            // Cancellation before this atomic rename leaves no published capture.
            cancellation.ThrowIfCancellationRequested();
            for (int index = 1; index <= 10_000; index++)
            {
                string suffix = index == 1 ? "" : $" ({index})";
                string destination = System.IO.Path.Combine(_directory, $"{label} {stamp}{suffix}{extension}");
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    created = false;
                    return new StoredCaptureFile(_directory, destination, identity.Value);
                }
                catch (IOException) when (File.Exists(destination)) { }
            }
            throw new IOException("Could not find an available name for the capture.");
        }
        finally
        {
            if (created && identity is { } original)
            {
                // The temp name is not enough: it may have been replaced since
                // its handle closed. Delete only the file we actually created.
                try { await new StoredCaptureFile(_directory, temporary, original).DeleteAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
            }
        }
    }
}

// Only a file published by CaptureStorage can yield this deletion capability.
// Deletion checks the Windows file ID on the same handle used to delete, so a
// replacement at the same pathname is not mistaken for the captured file.
internal sealed class StoredCaptureFile
{
    private readonly string _directory;
    private readonly OwnedFileIdentity _identity;
    private readonly SemaphoreSlim _deleteGate = new(1, 1);
    private bool _deleted;

    internal StoredCaptureFile(string directory, string path, OwnedFileIdentity identity)
    {
        _directory = directory; Path = path; _identity = identity;
    }

    internal string Path { get; }

    internal async Task DeleteAsync()
    {
        await _deleteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_deleted) return;
            await Task.Run(DeleteFile).ConfigureAwait(false);
            _deleted = true;
        }
        finally { _deleteGate.Release(); }
    }

    private void DeleteFile()
    {
        string fullPath = System.IO.Path.GetFullPath(Path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetDirectoryName(fullPath), _directory))
            throw new IOException("The saved capture is outside its original folder.");
        using var handle = Native.CreateFile(fullPath, Native.Delete | Native.ReadAttributes,
            Native.ShareRead | Native.ShareWrite, 0, Native.OpenExisting, Native.OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return; // Already removed.
            throw new Win32Exception(error, "Could not open the saved capture for deletion.");
        }
        if (OwnedFileIdentity.Read(handle) != _identity)
            throw new IOException("The saved capture was replaced; it was not deleted.");
        byte delete = 1; // FILE_DISPOSITION_INFO uses a one-byte BOOLEAN.
        if (!Native.SetFileInformationByHandle(handle, 4, ref delete, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not delete the saved capture.");
    }

    private static class Native
    {
        internal const uint Delete = 0x00010000, ReadAttributes = 0x80;
        internal const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3, OpenReparsePoint = 0x00200000;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
            uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);
    }
}

internal readonly record struct OwnedFileIdentity(uint Volume, uint IndexHigh, uint IndexLow)
{
    internal static OwnedFileIdentity Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the saved capture.");
        if ((info.Attributes & 0x400) != 0) throw new IOException("The saved capture was redirected.");
        if (info.IndexHigh == 0 && info.IndexLow == 0) throw new IOException("The saved capture could not be identified safely.");
        return new(info.Volume, info.IndexHigh, info.IndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal FileTime Created, Accessed, Modified;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
