using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Interop;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ovi.Screenshot.Testing;

internal sealed class NativeUiSmoke(SettingsWindow settings)
{
    private readonly SettingsWindow _settings = settings;
    private readonly PreviewStack _previews = new();
    private readonly Win32.Rect _recordingWorkArea = Win32.PointerMonitor().Info.Work;
    private RecordingControlWindow? _recordingControl;

    internal async Task<string> RunAsync(string marker)
    {
        using var icon = new TrayIcon();
        _recordingControl = new RecordingControlWindow(_recordingWorkArea);
        _recordingControl.ShowWithoutActivation();
        try
        {
            await WindowCapture.VerifySyntheticCleanupAsync();
            await ServiceSmoke.VerifyAsync(Path.ChangeExtension(marker, ".service-cache"));
            _settings.SetShortcutEditingEnabled(false);
            _settings.StartKeyCapture();
            if (_settings.IsLearningShortcut) throw new InvalidOperationException("Shortcut editing was not blocked during an update.");
            _settings.SetShortcutEditingEnabled(true);
            await VerifyRecordingUiAsync(marker);
            _recordingControl.MarkFinalizing();
            _recordingControl.MarkReady();
            _recordingControl.ShowSaveFailure();
            await VerifyPreviewAndDragAsync();
            await VerifyPreviewDeletionAsync();
            string details = await VerifyTrayAndSelectionAsync();
            await SelectionWindow.RenderCancelAppearanceAsync(Path.ChangeExtension(marker, ".cancel.png"));
            await VerifyWindowCaptureAsync(marker);
            await RenderDocumentationImagesAsync(marker);
            return "PASS recording outline, WinUI recording states, countdown completion/cancel, window-selection geometry, generated-window capture/PNG alpha, preview stack, shared-capture trash/busy protection/adaptive actions, drag payload, tray menu, shortcuts and bitmap geometry. " + details + " Documentation images render native app controls with generated demo content, not a live capture. No desktop capture or cross-app launch/drop performed.";
        }
        finally
        {
            await _previews.ShutdownAsync();
            _recordingControl.CloseSafely();
        }
    }

    private async Task VerifyRecordingUiAsync(string? marker)
    {
        RecordingBoundaryWindow.VerifySyntheticLayout();
        var area = _recordingWorkArea;
        using (var boundary = new RecordingBoundaryWindow(new Win32.Rect
        {
            Left = area.Left + 40, Top = area.Top + 40,
            Right = Math.Min(area.Right - 40, area.Left + 360), Bottom = Math.Min(area.Bottom - 40, area.Top + 240)
        }, area))
        {
            boundary.Show();
            if (!boundary.IsVisible || boundary.VisibleStripCount != 4) throw new InvalidOperationException("Recording area outline did not show all edges.");
            boundary.SetPaused(true);
            boundary.SetPaused(false);
        }
        var control = _recordingControl!;
        control.VerifySyntheticStates();
        control.SetElapsed(TimeSpan.FromSeconds(83));
        control.SetPaused(true);
        await Task.Delay(100);
        if (marker is not null) await RenderFixtureAsync((FrameworkElement)control.Content, Path.ChangeExtension(marker, ".recording.png"));
        control.SetPaused(false);
        var countdown = new RecordingCountdownWindow(_recordingWorkArea, _recordingWorkArea);
        try
        {
            Task<bool> start = countdown.RunAsync(CancellationToken.None);
            await Task.Delay(100);
            if (countdown.CurrentNumber != 3 || countdown.IsClosed) throw new InvalidOperationException("Recording countdown did not show its initial state.");
            if (marker is not null) await RenderFixtureAsync((FrameworkElement)countdown.Content, Path.ChangeExtension(marker, ".countdown.png"));
            if (!await start.WaitAsync(TimeSpan.FromSeconds(5)) || !countdown.IsClosed)
                throw new InvalidOperationException("Recording countdown did not finish.");
        }
        finally { countdown.Cancel(); }
        var cancelled = new RecordingCountdownWindow(_recordingWorkArea, _recordingWorkArea);
        try
        {
            Task<bool> start = cancelled.RunAsync(CancellationToken.None);
            await Task.Delay(100);
            cancelled.Cancel();
            if (await start.WaitAsync(TimeSpan.FromSeconds(2)) || !cancelled.IsClosed)
                throw new InvalidOperationException("Cancelled countdown would start a recording.");
        }
        finally { cancelled.Cancel(); }
    }

    private static async Task RenderFixtureAsync(FrameworkElement content, string path)
    {
        // Render only our generated WinUI fixture; never read a desktop/window surface.
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = File.Open(path, FileMode.CreateNew);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private async Task RenderDocumentationImagesAsync(string marker)
    {
        CaptureResult capture = await CreateDocumentationCaptureAsync();
        _previews.Show(capture);
        var preview = _previews.Windows.Single(window => ReferenceEquals(window.Capture, capture));
        var surface = (Grid)preview.Content;
        try
        {
            preview.Activate();
            await WaitForLoadedAsync(surface);
            var thumbnail = surface.Children.OfType<Image>().Single();
            if (!thumbnail.Focus(FocusState.Keyboard))
                throw new InvalidOperationException("Documentation preview could not receive keyboard focus.");
            await Task.Delay(100);
            bool actionsVisible = surface.Children.OfType<Border>().Any(border =>
                border.Visibility == Visibility.Visible && border.Child is Grid actions &&
                actions.Visibility == Visibility.Visible && actions.Children.OfType<Button>().Count() == 4);
            if (!actionsVisible)
                throw new InvalidOperationException("Documentation preview did not show its real Copy, Save, Delete and Close controls.");
            await RenderFixtureAsync(surface, Path.ChangeExtension(marker, ".screenshot-preview.png"));
        }
        finally { preview.Close(); }

        _settings.CancelKeyCapture();
        _settings.SetShortcut("Ctrl+Shift+4");
        _settings.SetShortcutEditingEnabled(true);
        _settings.SetStatus("");
        _settings.Activate();
        await Task.Delay(100);
        await RenderFixtureAsync((FrameworkElement)_settings.Content, Path.ChangeExtension(marker, ".settings.png"));
    }

    private static async Task WaitForLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded) return;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        element.Loaded += OnLoaded;
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { element.Loaded -= OnLoaded; }
    }

    private async Task VerifyPreviewDeletionAsync()
    {
        byte[] pixels = new byte[80 * 48 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 45; pixels[offset + 1] = 110;
            pixels[offset + 2] = 190; pixels[offset + 3] = 255;
        }
        CaptureResult CreateCapture() => CaptureImageFactory.Create(new CapturedDisplay(80, 48, pixels),
            new PixelSelection(0, 0, 80, 48), _recordingWorkArea);
        var capture = CreateCapture();
        var clone = capture with { };
        byte[] portraitPixels = new byte[160 * 4];
        for (int offset = 0; offset < portraitPixels.Length; offset += 4)
        {
            portraitPixels[offset] = 80; portraitPixels[offset + 1] = 150;
            portraitPixels[offset + 2] = 110; portraitPixels[offset + 3] = 255;
        }
        var survivor = CaptureImageFactory.Create(new CapturedDisplay(1, 160, portraitPixels),
            new PixelSelection(0, 0, 1, 160), _recordingWorkArea);
        CaptureResult? latest = capture;
        int deletedEvents = 0;
        void OnDeleted(CaptureResult deleted)
        {
            deletedEvents++;
            if (latest is not null && ReferenceEquals(latest.PngStorageIdentity, deleted.PngStorageIdentity)) latest = null;
        }
        _previews.Deleted += OnDeleted;
        try
        {
            _previews.Show(capture); _previews.Show(clone); _previews.Show(survivor);
            var selected = _previews.Windows.Single(window => ReferenceEquals(window.Capture, capture));
            var peer = _previews.Windows.Single(window => ReferenceEquals(window.Capture, clone));
            var narrow = _previews.Windows.Single(window => ReferenceEquals(window.Capture, survivor));
            Grid Actions(PreviewWindow window) => ((Grid)window.Content).Children.OfType<Border>()
                .Select(border => border.Child).OfType<Grid>().Single(grid => grid.Children.OfType<Button>().Count() == 4);
            narrow.Activate();
            await WaitForLoadedAsync((FrameworkElement)narrow.Content);
            ((Grid)narrow.Content).Children.OfType<Image>().Single().Focus(FocusState.Keyboard);
            await Task.Delay(50);
            var narrowActions = Actions(narrow);
            var narrowSurface = (Grid)narrow.Content;
            if (narrowActions.ColumnDefinitions.Count != 2 || narrowActions.RowDefinitions.Count != 2 ||
                narrowActions.ActualWidth + 16 > narrowSurface.ActualWidth || narrowActions.ActualHeight + 16 > narrowSurface.ActualHeight)
                throw new InvalidOperationException("The four preview actions were clipped on a narrow capture.");
            selected.Activate();
            await WaitForLoadedAsync((FrameworkElement)selected.Content);
            var thumbnail = ((Grid)selected.Content).Children.OfType<Image>().Single();
            if (!thumbnail.Focus(FocusState.Keyboard)) throw new InvalidOperationException("Delete fixture could not focus its preview.");
            await Task.Delay(50);
            Button Trash(PreviewWindow window) => Actions(window).Children.OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button) == "DeleteScreenshot");
            var trash = Trash(selected);
            if (AutomationProperties.GetName(trash) != "Delete screenshot")
                throw new InvalidOperationException("The trash action has no accessible name.");

            // The same gate is held by Copy/Save/Open and an active native drag.
            var busy = peer.TryBeginLinkedOperation() ?? throw new InvalidOperationException("The peer preview could not reserve an operation.");
            try
            {
                if (trash.IsEnabled || Trash(peer).IsEnabled || _previews.CanDelete(capture))
                    throw new InvalidOperationException("A matching busy preview did not disable deletion across clones.");
                await selected.DeleteAsync();
                if (capture.IsDeleted) throw new InvalidOperationException("Deletion bypassed a matching preview's active operation.");
            }
            finally { peer.CompleteLinkedOperation(busy); }
            if (!trash.IsEnabled) throw new InvalidOperationException("Trash did not recover after the peer operation completed.");

            await capture.ReleaseResidentPngAsync();
            byte[] png = await capture.ReadPngAsync();
            var export = await PreviewDragData.PrepareAsync(new DataPackage(), png);
            // The actual button and focused Delete key both use this same handler.
            await selected.DeleteAsync();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while ((!capture.IsDeleted || !selected.IsClosed || !peer.IsClosed) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!capture.IsDeleted || !clone.IsDeleted || !selected.IsClosed || !peer.IsClosed || deletedEvents != 1 || latest is not null)
                throw new InvalidOperationException("The trash handler did not delete shared storage, close clones and notify the latest-capture owner exactly once.");
            if (_previews.Windows.Count != 1 || survivor.IsDeleted)
                throw new InvalidOperationException("Deleting one capture changed an unrelated preview.");
            if (!(await File.ReadAllBytesAsync(export.Path)).AsSpan().SequenceEqual(png))
                throw new InvalidOperationException("Deleting internal storage changed an exported image attachment.");
            _previews.Show(capture); _previews.Show(clone);
            if (_previews.Windows.Count != 1) throw new InvalidOperationException("Reopen resurrected a deleted capture.");
            _previews.CloseAll();
            _previews.Show(survivor);
            if (_previews.Windows.Count != 1 || survivor.IsDeleted)
                throw new InvalidOperationException("Ordinary dismissal unexpectedly deleted the screenshot.");
        }
        finally
        {
            _previews.Deleted -= OnDeleted;
            _previews.CloseAll();
        }
    }

    private async Task<CaptureResult> CreateDocumentationCaptureAsync()
    {
        // This is authored example content, rendered directly from our own XAML
        // visual tree. It never reads pixels from the desktop or another app.
        var fixture = new Window { Title = "Generated documentation example" };
        var content = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Width="600" Height="400" Padding="26" Background="#FFF3F6FD" RowSpacing="16">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
                <StackPanel Spacing="5">
                    <TextBlock Text="OVI  /  DEMO NOTE" FontSize="16" FontWeight="SemiBold" Foreground="#FF476394"/>
                    <TextBlock Text="Capture. Keep. Share." Margin="0,40,0,0"
                               FontSize="34" FontWeight="SemiBold" Foreground="#FF14233E"/>
                    <TextBlock Text="A useful detail, ready for the conversation." FontSize="20" Foreground="#FF5D6B82"/>
                </StackPanel>
                <Border Grid.Row="1" Background="#FF14233E" CornerRadius="12" Padding="20,14">
                    <StackPanel VerticalAlignment="Center" Spacing="7">
                        <TextBlock FontFamily="Consolas" FontSize="23">
                            <Run Text="const " Foreground="#FFC5B8FF"/>
                            <Run Text="image = " Foreground="#FFE8EDF6"/>
                            <Run Text="capture();" Foreground="#FF8FD7E8"/>
                        </TextBlock>
                        <TextBlock FontFamily="Consolas" FontSize="23">
                            <Run Text="share" Foreground="#FF8FD7E8"/>
                            <Run Text="(image);" Foreground="#FFE8EDF6"/>
                        </TextBlock>
                        <TextBlock Text="// Keep building." FontFamily="Consolas" FontSize="23" Foreground="#FF9EB4CD"/>
                    </StackPanel>
                </Border>
                <TextBlock Grid.Row="2" Text="Generated example · not a live capture"
                           FontSize="16" Foreground="#FF687992"/>
            </Grid>
            """);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.Loaded += (_, _) => loaded.TrySetResult();
        fixture.Content = content;
        ((OverlappedPresenter)fixture.AppWindow.Presenter).SetBorderAndTitleBar(false, false);
        fixture.AppWindow.Move(new Windows.Graphics.PointInt32(_recordingWorkArea.Left + 40, _recordingWorkArea.Top + 40));
        double scale = Math.Max(96, Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(fixture))) / 96d;
        fixture.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(600 * scale), (int)Math.Round(400 * scale)));
        try
        {
            fixture.Activate();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(content);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                throw new InvalidOperationException("The generated documentation example did not render.");
            byte[] bgra = (await bitmap.GetPixelsAsync()).ToArray();
            var image = new CapturedDisplay(bitmap.PixelWidth, bitmap.PixelHeight,
                WindowCapture.StraightRgbaFromPremultipliedBgra(bgra));
            return await Task.Run(() => CaptureImageFactory.Create(image,
                new PixelSelection(0, 0, image.Width, image.Height), _recordingWorkArea));
        }
        finally { fixture.Close(); }
    }

    private async Task<string> VerifyTrayAndSelectionAsync()
    {
        SelectionWindow.VerifySyntheticWindowSelection();
        WindowImageStyle.VerifySyntheticShadow();
        ShortcutKeyCapture.VerifySyntheticLearning();
        GlobalCaptureShortcut.VerifySyntheticBinding();
        using (var binding = new GlobalCaptureShortcut(CaptureShortcut.Parse("Shift+Win+3"), _settings!.DispatcherQueue, () => { }))
        {
            if (binding.Enabled) throw new InvalidOperationException("Prepared shortcut hook must start disabled.");
            binding.Enabled = false; // Exercise its thread/reset acknowledgement without intercepting input.
        }
        // Validate hook installation/cleanup synchronously; never pump or inject input.
        using (var listener = new ShortcutKeyCapture(WinRT.Interop.WindowNative.GetWindowHandle(_settings!), _settings!.DispatcherQueue, _ => { }, _ => { })) { }
        var shortcut = CaptureShortcut.Parse("F17");
        if (shortcut.Key != 0x80 || shortcut.Modifiers != 0 || shortcut.Label != "F17")
            throw new InvalidOperationException("F17 is not mapped to its native key code.");
        var legacy = CaptureShortcut.Parse("Ctrl+Shift+4");
        if (legacy.Modifiers != 6 || legacy.Key != '4') throw new InvalidOperationException("Existing shortcut parsing changed.");
        var macKey = CaptureShortcut.FromKeyCode(12, 0x2c);
        if (CaptureShortcut.Parse(macKey.Label) != macKey || macKey.Label != "Shift+Win+PrintScreen")
            throw new InvalidOperationException("Hardware shortcut round-trip changed.");
        var mediaKey = CaptureShortcut.FromKeyCode(0, 0xb6);
        if (CaptureShortcut.Parse(mediaKey.Label) != mediaKey) throw new InvalidOperationException("Media key shortcut round-trip changed.");
        MonitorCapture.VerifySyntheticBitmap();
        var area = Win32.PointerMonitor().Info.Work;
        Exception? menuFailure = null;
        using var menu = new TrayMenuWindow(() => { }, () => { }, () => { }, () => { }, () => { });
        menu.Failed += error => menuFailure = error;
        menu.ShowAt(new Win32.Point { X = area.Right - 24, Y = area.Bottom - 24 }, false);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!menu.IsOpen && menuFailure is null && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (menuFailure is not null) throw new InvalidOperationException("Tray menu could not open.", menuFailure);
        if (!menu.IsOpen) throw new InvalidOperationException("Tray menu did not open after its XAML root loaded.");
        string menuTheme = ((FrameworkElement)menu.Content).ActualTheme.ToString();
        menu.Dismiss();
        if (menu.IsOpen) throw new InvalidOperationException("Tray menu did not close.");

        var bounds = new Win32.Rect
        {
            Left = area.Left + 24, Top = area.Top + 24,
            Right = Math.Min(area.Right, area.Left + 344), Bottom = Math.Min(area.Bottom, area.Top + 224)
        };
        byte[] pixels = new byte[bounds.Width * bounds.Height * 4];
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        var display = new CapturedDisplay(bounds.Width, bounds.Height, pixels);
        var overlay = new SelectionWindow(display, bounds, BitmapSources.ToPremultipliedBgra(pixels));
        try
        {
            Task<PixelSelection?> selection = overlay.SelectAsync();
            await Task.Delay(200);
            if (!overlay.IsVisible) throw new InvalidOperationException("Selection overlay closed before cancellation.");
            overlay.Cancel();
            if (await selection.WaitAsync(TimeSpan.FromSeconds(2)) is not null)
                throw new InvalidOperationException("Selection cancellation produced an image.");
        }
        finally { overlay.Cancel(); }
        return $"Tray theme: {menuTheme}.";
    }

    private async Task VerifyWindowCaptureAsync(string? marker)
    {
        // A generated window belonging to this process only. Never capture the desktop.
        var fixture = new Window { Title = "Synthetic capture fixture" };
        var content = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid.RowDefinitions><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
                <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
                <Border Background="#FFF01414"/>
                <Border Grid.Column="1" Background="#FF14F014"/>
                <Border Grid.Row="1" Background="#FF1414F0"/>
                <Border Grid.Row="1" Grid.Column="1" Background="#FFF0F014"/>
            </Grid>
            """);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.Loaded += (_, _) => loaded.TrySetResult();
        fixture.Content = content;
        ((OverlappedPresenter)fixture.AppWindow.Presenter).SetBorderAndTitleBar(true, false);
        fixture.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(_recordingWorkArea.Left + 60, _recordingWorkArea.Top + 60, 320, 220));
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(fixture);
        Win32.TryRoundWindow(handle);
        try
        {
            fixture.Activate();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            await Task.Run(Win32.FlushDesktopComposition);
            if (!WindowTargets.TryGetBounds(handle, out var bounds)) throw new InvalidOperationException("Synthetic window bounds are unavailable.");
            var image = await WindowCapture.AcquireAsync(new WindowCaptureTarget(handle, bounds, "Synthetic capture fixture")
                { ProcessId = (uint)Environment.ProcessId }, CancellationToken.None);
            int width = image.Width, height = image.Height;
            bool Marker(int x, int y, int red, int green, int blue)
            {
                int offset = (y * image.Width + x) * 4;
                return Math.Abs(image.Rgba[offset] - red) < 40 && Math.Abs(image.Rgba[offset + 1] - green) < 40 && Math.Abs(image.Rgba[offset + 2] - blue) < 40;
            }
            if (!Marker(width / 4, height / 4, 240, 20, 20) || !Marker(width * 3 / 4, height / 4, 20, 240, 20) ||
                !Marker(width / 4, height * 3 / 4, 20, 20, 240) || !Marker(width * 3 / 4, height * 3 / 4, 240, 240, 20))
                throw new InvalidOperationException("Window capture changed the generated source's channels or orientation.");
            if (marker is not null)
            {
                var result = CaptureImageFactory.Create(image, new PixelSelection(0, 0, image.Width, image.Height), _recordingWorkArea, windowShadow: true);
                await File.WriteAllBytesAsync(Path.ChangeExtension(marker, ".window.png"), await result.ReadPngAsync());
            }
        }
        finally { fixture.Close(); }
    }

    private async Task VerifyPreviewAndDragAsync()
    {
        var workArea = Win32.PointerMonitor().Info.Work;
        byte[] pixels = new byte[32 * 20 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 32;
            pixels[offset + 1] = 96;
            pixels[offset + 2] = 160;
            pixels[offset + 3] = 255;
        }
        var capture = CaptureImageFactory.Create(new CapturedDisplay(32, 20, pixels), new PixelSelection(0, 0, 32, 20), workArea);
        _previews.Show(capture);
        _previews.Show(capture with { });
        _previews.Show(capture with { });
        if (_previews.Windows.Count != 3) throw new InvalidOperationException("Previews replaced previous captures.");
        foreach (var preview in _previews.Windows)
        {
            var position = preview.AppWindow.Position;
            var size = preview.PixelSize;
            if (position.X < workArea.Left || position.Y < workArea.Top || position.X + size.Width > workArea.Right || position.Y + size.Height > workArea.Bottom)
                throw new InvalidOperationException("Preview stack extends outside the display work area.");
        }
        _previews.Suspend();
        if (_previews.Windows.Any(window => window.AppWindow.IsVisible)) throw new InvalidOperationException("Previews were not hidden before capture.");
        _previews.Restore();
        if (_previews.Windows.Any(window => !window.AppWindow.IsVisible)) throw new InvalidOperationException("Previews were not restored after capture.");
        _previews.Windows[0].Close();
        if (_previews.Windows.Count != 2) throw new InvalidOperationException("Closing a preview removed the wrong capture.");

        byte[] png = await capture.ReadPngAsync();
        await CaptureResult.VerifySyntheticStorageAsync(png);
        await PreviewOperationGate.VerifySyntheticAsync();
        PreviewGeometry.VerifySynthetic();
        var package = new DataPackage();
        var export = await PreviewDragData.PrepareAsync(package, png);
        var data = package.GetView();
        if (package.RequestedOperation != DataPackageOperation.Copy || !data.Contains(StandardDataFormats.StorageItems) || !data.Contains(StandardDataFormats.Bitmap) || data.Contains(StandardDataFormats.Text))
            throw new InvalidOperationException("Drag data does not contain native image/file representations.");
        var items = await data.GetStorageItemsAsync();
        if (items.Count != 1) throw new InvalidOperationException("Drag payload must contain exactly one image file.");
        byte[] exportedBytes = await File.ReadAllBytesAsync(items[0].Path);
        if (!png.AsSpan().SequenceEqual(exportedBytes))
            throw new InvalidOperationException("Dragged file differs from the exported screenshot.");
        var reference = await data.GetBitmapAsync();
        using (var stream = await reference.OpenReadAsync())
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            byte[] bitmapBytes = new byte[checked((int)stream.Size)];
            await reader.LoadAsync((uint)bitmapBytes.Length);
            reader.ReadBytes(bitmapBytes);
            if (!png.AsSpan().SequenceEqual(bitmapBytes)) throw new InvalidOperationException("Dragged bitmap differs from the exported screenshot.");
        }
        data.ReportOperationCompleted(DataPackageOperation.Copy);
        _previews.CloseAll();
        if (!File.Exists(export.Path)) throw new InvalidOperationException("Drag export was deleted before a destination could consume it.");

        var negativeArea = new Win32.Rect { Left = -1600, Top = -200, Right = 0, Bottom = 900 };
        var sizes = new[] { new Windows.Graphics.SizeInt32(276, 160), new Windows.Graphics.SizeInt32(276, 80), new Windows.Graphics.SizeInt32(276, 180) };
        var positions = PreviewStackLayout.Arrange(sizes, negativeArea, 20, 8);
        for (int index = 0; index < sizes.Length; index++)
        {
            if (positions[index].X < negativeArea.Left || positions[index].Y < negativeArea.Top || positions[index].X + sizes[index].Width > negativeArea.Right || positions[index].Y + sizes[index].Height > negativeArea.Bottom)
                throw new InvalidOperationException("Negative-origin preview placement is incorrect.");
            if (index > 0 && positions[index - 1].Y + sizes[index - 1].Height > positions[index].Y)
                throw new InvalidOperationException("Preview stack overlaps despite available vertical space.");
        }
        var smallArea = new Win32.Rect { Left = 0, Top = 0, Right = 320, Bottom = 240 };
        var crowded = Enumerable.Repeat(new Windows.Graphics.SizeInt32(276, 180), 10).ToArray();
        var cascade = PreviewStackLayout.Arrange(crowded, smallArea, 20, 8);
        if (cascade.Any(point => point.X < 0 || point.Y < 0 || point.X + 276 > 320 || point.Y + 180 > 240))
            throw new InvalidOperationException("Crowded preview stack extends off screen.");
    }

}
