using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace Ovi.Screenshot.Views;

// The thumbnail is a normal preview payload; file operations always use the MP4.
internal sealed class VideoPreview
{
    private readonly Func<Task> _deleteOwnedFile;
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private StorageFile? _export;
    private bool _ownedDeleted;

    private VideoPreview(string path, CaptureResult thumbnail, Func<Task> deleteOwnedFile)
    {
        Path = path; Thumbnail = thumbnail; _deleteOwnedFile = deleteOwnedFile;
    }

    internal string Path { get; }
    internal CaptureResult Thumbnail { get; }
    internal bool IsDeleted => Thumbnail.IsDeleted;

    internal static async Task<VideoPreview> CreateAsync(string savedPath, Win32.Rect workArea,
        Func<Task> deleteOwnedFile, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(deleteOwnedFile);
        string fullPath = System.IO.Path.GetFullPath(savedPath);
        if (!System.IO.Path.IsPathFullyQualified(savedPath) ||
            !string.Equals(System.IO.Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A saved MP4 file is required.", nameof(savedPath));
        var file = await StorageFile.GetFileFromPathAsync(fullPath).AsTask(cancellation);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellation);
        var properties = clip.GetVideoEncodingProperties();
        if (properties.Width == 0 || properties.Height == 0 || clip.OriginalDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("The saved recording does not contain playable video.");
        double scale = Math.Min(1, Math.Min(600d / properties.Width, 360d / properties.Height));
        int width = Math.Max(1, (int)Math.Round(properties.Width * scale));
        int height = Math.Max(1, (int)Math.Round(properties.Height * scale));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var position = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMilliseconds(500).Ticks, clip.OriginalDuration.Ticks / 2));
        using var frame = await composition.GetThumbnailAsync(position, width, height, VideoFramePrecision.NearestFrame).AsTask(cancellation);
        var decoder = await BitmapDecoder.CreateAsync(frame).AsTask(cancellation);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellation);
        byte[] pixels = data.DetachPixelData();
        // Decoder output is bounded above; Rust provides the same thumbnail and PNG
        // storage contract used by screenshots, not video encoding.
        var thumbnail = await Task.Run(() => CaptureImageFactory.Create(new CapturedDisplay(width, height, pixels),
            new PixelSelection(0, 0, width, height), workArea), cancellation);
        return new VideoPreview(fullPath, thumbnail, deleteOwnedFile);
    }

    internal async Task<StorageFile> ExportAsync(CancellationToken cancellation = default)
    {
        await _exportGate.WaitAsync(cancellation);
        try
        {
            if (_ownedDeleted || IsDeleted) throw new InvalidOperationException("The recording was deleted.");
            string? cachedPath = _export?.Path;
            if (cachedPath is not null && await Task.Run(() => File.Exists(Path) && File.Exists(cachedPath), cancellation))
                return _export!;
            return _export = await VideoFileCache.ExportAsync(Path, this, cancellation);
        }
        finally { _exportGate.Release(); }
    }

    internal async Task PrepareDataAsync(DataPackage data, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        StorageFile file = await ExportAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        data.RequestedOperation = DataPackageOperation.Copy;
        data.SetStorageItems(new IStorageItem[] { file }, readOnly: true);
    }

    // Caller reserves every matching preview before invoking this callback.
    internal async Task DeleteOwnedFileAsync()
    {
        if (_ownedDeleted) return;
        await _deleteOwnedFile();
        _ownedDeleted = true;
    }
}
