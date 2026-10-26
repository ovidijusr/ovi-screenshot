using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ovi.Screenshot.Views;

// Only native data-package construction lives here. ScreenshotFileCache owns file retention.
internal static class PreviewDragData
{
    internal static async Task<StorageFile> PrepareAsync(DataPackage data, byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        StorageFile file = await ScreenshotFileCache.ExportFileAsync(png, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            data.RequestedOperation = DataPackageOperation.Copy;
            data.SetStorageItems(new IStorageItem[] { file }, readOnly: true);
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            // Retain after drop/cancel/preview closure: a destination can read asynchronously.
            return file;
        }
        catch
        {
            ScreenshotFileCache.DeleteUnpublished(file);
            throw;
        }
    }

    // Compatibility for callers outside previews while the cache API is adopted.
    internal static Task<StorageFile> ExportFileAsync(byte[] png, CancellationToken cancellationToken = default) =>
        ScreenshotFileCache.ExportFileAsync(png, cancellationToken);
}
