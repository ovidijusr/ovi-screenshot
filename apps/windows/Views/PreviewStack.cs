using Ovi.Screenshot.Native;
using Windows.Graphics;

namespace Ovi.Screenshot.Views;

internal sealed class PreviewStack
{
    internal const long ResidentPngBudget = 64L * 1024 * 1024;
    private readonly List<PreviewWindow> _windows = [];
    private int _suspended;
    private bool _arranging, _stopping, _cacheRequested;
    private Task _cacheWork = Task.CompletedTask;
    private Task? _shutdown;

    internal IReadOnlyList<PreviewWindow> Windows => _windows.Where(window => !window.IsClosed).ToArray();
    internal bool IsBusy => _windows.Any(window => !window.IsClosed && window.IsBusy);
    internal long ResidentPngBytes => Windows.Select(window => window.Capture).DistinctBy(capture => capture.PngStorageIdentity).Sum(capture => capture.ResidentPngBytes);
    internal event Action<Exception>? Failed;

    internal void Show(CaptureResult capture)
    {
        if (_stopping) return;
        var existing = _windows.FirstOrDefault(window => !window.IsClosed && ReferenceEquals(window.Capture, capture));
        if (existing is not null)
        {
            if (_suspended == 0) existing.ShowWithoutActivation();
            return;
        }

        var preview = new PreviewWindow(capture);
        _windows.Add(preview);
        preview.LayoutChanged += Arrange;
        preview.Failed += ReportFailure;
        preview.Closed += (_, _) =>
        {
            preview.LayoutChanged -= Arrange;
            preview.Failed -= ReportFailure;
            _windows.Remove(preview);
            Arrange();
        };
        Arrange();
        if (_suspended == 0) preview.ShowWithoutActivation();
        RequestCacheTrim();
    }

    internal void Suspend()
    {
        _suspended++;
        if (_suspended != 1) return;
        foreach (var window in Windows) window.Hide();
    }

    internal void Restore()
    {
        if (_stopping) return;
        if (_suspended == 0 || --_suspended != 0) return;
        Arrange();
        foreach (var window in Windows) window.ShowWithoutActivation();
    }

    internal void CloseAll()
    {
        foreach (var window in Windows)
            if (!window.DeferCloseIfBusy()) window.Close();
    }

    internal Task WaitForCacheAsync() => _cacheWork;

    internal Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _stopping = true;
        var windows = Windows;
        foreach (var window in windows) window.StopAcceptingOperations();
        await Task.WhenAll(windows.Select(window => window.WaitForIdleAsync()).Append(_cacheWork));
        foreach (var window in windows) if (!window.IsClosed) window.Close();
    }

    private void RequestCacheTrim()
    {
        _cacheRequested = true;
        if (_cacheWork.IsCompleted) _cacheWork = TrimResidentPngsAsync();
    }

    private async Task TrimResidentPngsAsync()
    {
        while (_cacheRequested)
        {
            _cacheRequested = false;
            var captures = Windows.Select(window => window.Capture).DistinctBy(capture => capture.PngStorageIdentity).ToArray();
            long resident = captures.Sum(capture => capture.ResidentPngBytes);
            foreach (var capture in captures)
            {
                if (resident <= ResidentPngBudget) break;
                long bytes = capture.ResidentPngBytes;
                if (bytes == 0) continue;
                try { await capture.ReleaseResidentPngAsync(); resident -= bytes; }
                catch (Exception error)
                {
                    var preview = Windows.FirstOrDefault(window => ReferenceEquals(window.Capture.PngStorageIdentity, capture.PngStorageIdentity));
                    if (preview is not null) preview.ShowStorageFailure(error); else ReportFailure(error);
                }
            }
        }
    }

    private void ReportFailure(Exception error)
    {
        System.Diagnostics.Trace.WriteLine(error);
        try { Failed?.Invoke(error); }
        catch (Exception reportingError) { System.Diagnostics.Trace.WriteLine(reportingError); }
    }

    private void Arrange()
    {
        if (_arranging || _stopping) return;
        _arranging = true;
        try
        {
            var current = Windows;
            foreach (var window in current) window.RefreshDisplayGeometry(notify: false);
            var groups = current.GroupBy(window =>
            {
                var area = window.CurrentWorkArea;
                return (area.Left, area.Top, area.Right, area.Bottom);
            });
            foreach (var group in groups)
            {
                var windows = group.ToArray();
                var area = windows[^1].CurrentWorkArea;
                double scale = Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(windows[^1])) / 96d;
                var positions = PreviewStackLayout.Arrange(windows.Select(window => window.PixelSize).ToArray(), area,
                    (int)Math.Round(20 * scale), (int)Math.Round(8 * scale));
                for (int index = 0; index < windows.Length; index++)
                    windows[index].MoveTo(positions[index].X, positions[index].Y);
            }
        }
        finally { _arranging = false; }
    }
}

internal static class PreviewGeometry
{
    // Logical content bounds are recalculated for the CURRENT display; capture metadata stays immutable.
    internal static SizeInt32 Fit(int imageWidth, int imageHeight, Win32.Rect area, double scale, bool feedback)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        int margin = (int)Math.Round(20 * scale);
        double maxWidth = Math.Min(276, Math.Max(1, area.Width - margin * 2) / scale);
        double maxHeight = Math.Min(180, Math.Max(1, area.Height - margin * 2) / scale);
        double fit = Math.Min(maxWidth / imageWidth, maxHeight / imageHeight);
        int width = (int)Math.Round(Math.Min(maxWidth, Math.Max(108, imageWidth * fit)) * scale);
        int height = (int)Math.Round(Math.Min(maxHeight, Math.Max(feedback ? 76 : 40, imageHeight * fit)) * scale);
        return new SizeInt32(Math.Clamp(width, 1, Math.Max(1, area.Width)), Math.Clamp(height, 1, Math.Max(1, area.Height)));
    }

    internal static void VerifySynthetic()
    {
        var area = new Win32.Rect { Left = -1920, Top = -100, Right = 0, Bottom = 1080 };
        var normal = Fit(1000, 500, area, 1, false);
        var scaled = Fit(1000, 500, area, 2, false);
        if (normal.Width != 276 || normal.Height != 138 || scaled.Width != 552 || scaled.Height != 276)
            throw new InvalidOperationException("Preview logical size changed across DPI scales.");
        var tiny = Fit(1, 1000, new Win32.Rect { Right = 24, Bottom = 20 }, 2, true);
        if (tiny.Width > 24 || tiny.Height > 20 || tiny.Width < 1 || tiny.Height < 1)
            throw new InvalidOperationException("Preview sizing exceeded its current work area.");
        var feedback = Fit(1000, 10, area, 1, true);
        var retry = Fit(1000, 10, area, 2, false);
        if (feedback.Height < 76 || retry.Height != 80)
            throw new InvalidOperationException("Preview feedback sizing reused stale physical pixels.");
    }
}

internal static class PreviewStackLayout
{
    // Input is oldest to newest. Newest stays nearest the lower-right corner.
    internal static PointInt32[] Arrange(IReadOnlyList<SizeInt32> sizes, Win32.Rect area, int margin, int gap)
    {
        var positions = new PointInt32[sizes.Count];
        if (sizes.Count == 0) return positions;
        int insetX = Math.Clamp(margin, 0, Math.Max(0, (area.Width - 1) / 2));
        int insetY = Math.Clamp(margin, 0, Math.Max(0, (area.Height - 1) / 2));
        int left = area.Left + insetX, top = area.Top + insetY;
        int right = area.Right - insetX, bottom = area.Bottom - insetY;
        int columnRight = right, cursorBottom = bottom, columnWidth = 0;
        bool columnHasWindows = false;

        for (int index = sizes.Count - 1; index >= 0; index--)
        {
            var size = sizes[index];
            if (columnHasWindows && cursorBottom - size.Height < top)
            {
                columnRight -= columnWidth + gap;
                cursorBottom = bottom;
                columnWidth = 0;
                columnHasWindows = false;
            }
            if (columnRight - size.Width < left)
            {
                // If columns cannot fit, keep every capture inside the display in a
                // layered cascade. Closing the front cards reveals all older ones.
                int step = Math.Max(12, gap * 2);
                for (int cascade = sizes.Count - 1; cascade >= 0; cascade--)
                {
                    long offset = (long)(sizes.Count - 1 - cascade) * step;
                    positions[cascade] = new PointInt32(
                        ClampPosition((long)right - sizes[cascade].Width - offset, left, right - sizes[cascade].Width),
                        ClampPosition((long)bottom - sizes[cascade].Height - offset, top, bottom - sizes[cascade].Height));
                }
                return positions;
            }
            int x = ClampPosition((long)columnRight - size.Width, area.Left, area.Right - size.Width);
            int y = ClampPosition((long)cursorBottom - size.Height, area.Top, area.Bottom - size.Height);
            positions[index] = new PointInt32(x, y);
            cursorBottom = y - gap;
            columnWidth = Math.Max(columnWidth, size.Width);
            columnHasWindows = true;
        }
        return positions;
    }

    private static int ClampPosition(long value, int minimum, int maximum) =>
        (int)Math.Clamp(value, minimum, Math.Max(minimum, maximum));
}
