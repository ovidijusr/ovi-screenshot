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

    internal CaptureResult(int Width, int Height, byte[] Png, int PreviewWidth, int PreviewHeight, byte[] PreviewRgba, Win32.Rect WorkArea)
    {
        this.Width = Width; this.Height = Height; this.PreviewWidth = PreviewWidth; this.PreviewHeight = PreviewHeight;
        this.PreviewRgba = PreviewRgba; this.WorkArea = WorkArea;
        _png = new ScreenshotPngStorage(Png);
    }

    internal Task<byte[]> ReadPngAsync(CancellationToken cancellationToken = default) => _png.ReadAsync(cancellationToken);
    internal Task ReleaseResidentPngAsync(CancellationToken cancellationToken = default) => _png.ReleaseResidentAsync(cancellationToken);

    internal static async Task VerifySyntheticStorageAsync(byte[] png)
    {
        var capture = new CaptureResult(1, 1, png, 1, 1, new byte[4], default);
        var copy = capture with { };
        byte[] lease = await capture.ReadPngAsync();
        if (!ReferenceEquals(capture.PngStorageIdentity, copy.PngStorageIdentity) || !ReferenceEquals(lease, png))
            throw new InvalidOperationException("Capture copies duplicated their resident PNG storage.");
        await capture.ReleaseResidentPngAsync();
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

        var rejected = new ScreenshotPngStorage([1, 2, 3]);
        try
        {
            await rejected.ReleaseResidentAsync(CancellationToken.None);
            throw new InvalidOperationException("Invalid PNG data was published to the export cache.");
        }
        catch (ArgumentException) { }
        if (rejected.ResidentBytes != 3 || !(await rejected.ReadAsync(CancellationToken.None)).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new InvalidOperationException("A failed PNG spill discarded its original image.");
    }
}

internal sealed class ScreenshotPngStorage(byte[] png)
{
    private readonly SemaphoreSlim _access = new(1, 1);
    private byte[]? _resident = png ?? throw new ArgumentNullException(nameof(png));
    private string? _path;
    internal long ResidentBytes => Volatile.Read(ref _resident)?.LongLength ?? 0;

    internal async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? resident = Volatile.Read(ref _resident);
        if (resident is not null) return resident;
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (_resident is not null) return _resident;
            if (_path is null) throw new InvalidOperationException("The screenshot has no image data.");
            // The caller owns this read lease; do not silently repopulate the resident budget.
            return await File.ReadAllBytesAsync(_path, cancellationToken);
        }
        finally { _access.Release(); }
    }

    internal async Task ReleaseResidentAsync(CancellationToken cancellationToken)
    {
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (_resident is null) return;
            if (_path is null)
            {
                var file = await ScreenshotFileCache.ExportFileAsync(_resident, cancellationToken);
                ScreenshotFileCache.KeepAlive(file.Path, this);
                _path = file.Path;
            }
            // Only release after the complete file is durable. Failure preserves the in-memory image.
            Volatile.Write(ref _resident, null);
        }
        finally { _access.Release(); }
    }
}
