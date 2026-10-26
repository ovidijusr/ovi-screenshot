using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Views;

// Record copies share one immutable PNG payload, including its resident/disk lifetime.
internal sealed record CaptureResult
{
    private readonly ScreenshotPngStorage _png;
    internal int Width { get; init; }
    internal int Height { get; init; }
    internal int PreviewWidth { get; init; }
    internal int PreviewHeight { get; init; }
    internal byte[] PreviewRgba { get; init; }
    internal Win32.Rect WorkArea { get; init; }
    internal long ResidentPngBytes => _png.ResidentBytes;
    internal object PngStorageIdentity => _png;
    internal bool IsDeleted => _png.IsDeleted;

    internal CaptureResult(int Width, int Height, byte[] Png, int PreviewWidth, int PreviewHeight, byte[] PreviewRgba, Win32.Rect WorkArea)
    {
        this.Width = Width; this.Height = Height; this.PreviewWidth = PreviewWidth; this.PreviewHeight = PreviewHeight;
        this.PreviewRgba = PreviewRgba; this.WorkArea = WorkArea;
        _png = new ScreenshotPngStorage(Png);
    }

    internal Task<byte[]> ReadPngAsync(CancellationToken cancellationToken = default) => _png.ReadAsync(cancellationToken);
    internal Task ReleaseResidentPngAsync(CancellationToken cancellationToken = default) => _png.ReleaseResidentAsync(cancellationToken);
    internal Task DeleteAsync(CancellationToken cancellationToken = default) => _png.DeleteAsync(cancellationToken);

    internal static async Task VerifySyntheticStorageAsync(byte[] png)
    {
        var capture = new CaptureResult(1, 1, png, 1, 1, new byte[4], default);
        var copy = capture with { };
        byte[] lease = await capture.ReadPngAsync();
        if (!ReferenceEquals(capture.PngStorageIdentity, copy.PngStorageIdentity) || !ReferenceEquals(lease, png))
            throw new InvalidOperationException("Capture copies duplicated their resident PNG storage.");
        await capture.ReleaseResidentPngAsync();
        string backing = capture._png.BackingPath ?? throw new InvalidOperationException("PNG spilling did not create a backing file.");
        if (capture.ResidentPngBytes != 0 || copy.ResidentPngBytes != 0 || !lease.AsSpan().SequenceEqual(png))
            throw new InvalidOperationException("PNG spilling invalidated an active read lease or retained resident data.");
        byte[] restored = await copy.ReadPngAsync();
        if (!restored.AsSpan().SequenceEqual(png) || capture.ResidentPngBytes != 0)
            throw new InvalidOperationException("PNG disk reads changed image bytes or bypassed the resident budget.");
        await copy.ReleaseResidentPngAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await capture.ReadPngAsync(cancelled.Token);
            throw new InvalidOperationException("A cancelled PNG read unexpectedly completed.");
        }
        catch (OperationCanceledException) { }

        bool deletionCancelled = false;
        try { await capture.DeleteAsync(cancelled.Token); }
        catch (OperationCanceledException) { deletionCancelled = true; }
        Require(deletionCancelled && !capture.IsDeleted, "Cancelled deletion changed the capture.");

        var exported = await ScreenshotFileCache.ExportFileAsync(png);
        bool exportRejected = false;
        try { await ScreenshotFileCache.DeleteBackingFileAsync(exported.Path); }
        catch (ArgumentException) { exportRejected = true; }
        Require(exportRejected && File.Exists(exported.Path), "Backing deletion accepted a published export path.");

        FileAttributes attributes = File.GetAttributes(backing);
        File.SetAttributes(backing, attributes | FileAttributes.ReadOnly);
        bool deletionFailed = false;
        try { await capture.DeleteAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { deletionFailed = true; }
        finally { File.SetAttributes(backing, attributes); }
        Require(deletionFailed && !capture.IsDeleted && !copy.IsDeleted && File.Exists(backing),
            "Failed backing deletion discarded the screenshot or marked it deleted.");
        Require((await copy.ReadPngAsync()).AsSpan().SequenceEqual(png), "Failed deletion made the screenshot unreadable.");

        await copy.DeleteAsync();
        Require(capture.IsDeleted && copy.IsDeleted && capture.ResidentPngBytes == 0 && !File.Exists(backing),
            "Deletion was not shared across capture copies or left its backing file.");
        Require(lease.AsSpan().SequenceEqual(png) && restored.AsSpan().SequenceEqual(png) && File.Exists(exported.Path),
            "Deletion invalidated an acquired read lease or removed a published export.");
        bool readRejected = false;
        try { await capture.ReadPngAsync(); }
        catch (InvalidOperationException) { readRejected = true; }
        Require(readRejected, "A deleted screenshot was readable.");
        await copy.ReleaseResidentPngAsync();
        await Task.WhenAll(capture.DeleteAsync(), copy.DeleteAsync());
        Require(capture._png.BackingPath is null && !File.Exists(backing), "Spilling or repeated deletion resurrected a screenshot.");

        var resident = new ScreenshotPngStorage(png);
        byte[] residentLease = await resident.ReadAsync(CancellationToken.None);
        await resident.DeleteAsync(CancellationToken.None);
        await resident.ReleaseResidentAsync(CancellationToken.None);
        Require(resident.IsDeleted && resident.ResidentBytes == 0 && resident.BackingPath is null
            && residentLease.AsSpan().SequenceEqual(png), "Deleting resident data created backing storage or invalidated its lease.");

        var missing = new ScreenshotPngStorage(png);
        await missing.ReleaseResidentAsync(CancellationToken.None);
        await ScreenshotFileCache.DeleteBackingFileAsync(missing.BackingPath!);
        await missing.DeleteAsync(CancellationToken.None);
        Require(missing.IsDeleted, "An already absent backing file prevented deletion.");

        var rejected = new ScreenshotPngStorage([1, 2, 3]);
        try
        {
            await rejected.ReleaseResidentAsync(CancellationToken.None);
            throw new InvalidOperationException("Invalid PNG data was published to the export cache.");
        }
        catch (ArgumentException) { }
        if (rejected.ResidentBytes != 3 || !(await rejected.ReadAsync(CancellationToken.None)).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new InvalidOperationException("A failed PNG spill discarded its original image.");
        await rejected.DeleteAsync(CancellationToken.None);

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

internal sealed class ScreenshotPngStorage(byte[] png)
{
    private readonly SemaphoreSlim _access = new(1, 1);
    private byte[]? _resident = png ?? throw new ArgumentNullException(nameof(png));
    private string? _path;
    private bool _deleted;
    internal long ResidentBytes => Volatile.Read(ref _resident)?.LongLength ?? 0;
    internal bool IsDeleted => Volatile.Read(ref _deleted);
    internal string? BackingPath => Volatile.Read(ref _path);

    internal async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (_deleted) throw new InvalidOperationException("This screenshot has been deleted.");
            if (_resident is not null) return _resident;
            if (_path is null) throw new InvalidOperationException("The screenshot has no image data.");
            // The caller owns this read lease; do not silently repopulate the resident budget.
            return await ScreenshotFileCache.ReadBackingFileAsync(_path, cancellationToken);
        }
        finally { _access.Release(); }
    }

    internal async Task ReleaseResidentAsync(CancellationToken cancellationToken)
    {
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (_deleted || _resident is null) return;
            if (_path is null)
            {
                _path = await ScreenshotFileCache.CreateBackingFileAsync(_resident, this, cancellationToken);
            }
            // Only release after the complete file is durable. Failure preserves the in-memory image.
            Volatile.Write(ref _resident, null);
        }
        finally { _access.Release(); }
    }

    internal async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (_deleted) return;
            if (_path is not null) await ScreenshotFileCache.DeleteBackingFileAsync(_path, cancellationToken);
            _path = null;
            Volatile.Write(ref _resident, null);
            Volatile.Write(ref _deleted, true);
        }
        finally { _access.Release(); }
    }
}
