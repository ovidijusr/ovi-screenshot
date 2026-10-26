using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ovi.Screenshot.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;

namespace Ovi.Screenshot.Views;

internal sealed class PreviewWindow : Window
{
    private readonly CaptureResult _capture;
    private readonly VideoPreview? _video;
    private readonly PreviewStack _owner;
    private readonly nint _window;
    private readonly Grid _surface = new();
    private readonly Image _thumbnail;
    private readonly Grid _controls = new() { ColumnSpacing = 2, RowSpacing = 2, Visibility = Visibility.Collapsed };
    private readonly Border _controlHost;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Border _statusHost;
    private readonly Border? _playBadge;
    private readonly Button _copy;
    private readonly Button _save;
    private readonly Button _delete;
    private readonly Button _dismiss;
    private readonly WindowHook _themeHook;
    private readonly PreviewOperationGate _operations = new();
    // Cancel unanswered dialogs during shutdown, never an accepted image write.
    private readonly CancellationTokenSource _interactiveCancellation = new();
    private PreviewOperationGate.Operation? _dragOperation;
    private CancellationTokenSource? _dragPreparation;
    private DragStartingEventArgs? _dragStarting;
    private Win32.Rect _workArea;
    private double _displayScale = 1;
    private int _controlColumns;
    private bool _reportedBusy;
    private StorageFile? _openFile;
    private bool _closed, _pointerInside, _closePending, _windowActive, _keyboardMode, _displayRefreshQueued, _refreshingDisplay;

    internal CaptureResult Capture => _capture;
    internal VideoPreview? Video => _video;
    internal SizeInt32 PixelSize => AppWindow.Size;
    internal bool IsClosed => _closed;
    internal bool IsCaptureExcluded { get; }
    internal bool IsBusy => _operations.IsBusy;
    internal Win32.Rect CurrentWorkArea => _workArea;
    internal event Action? LayoutChanged;
    internal event Action<Exception>? Failed;

    internal PreviewWindow(CaptureResult capture, PreviewStack owner, VideoPreview? video = null)
    {
        _capture = capture; _owner = owner; _video = video;
        Title = video is null ? "Screenshot preview — Ovi Screenshot" : "Recording preview — Ovi Screenshot";
        _window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        // Keep the native frame so DWM owns rounded clipping and the outside shadow.
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true; presenter.IsResizable = false;
        presenter.IsMinimizable = false; presenter.IsMaximizable = false;
        long extended = Win32.GetWindowLongPtr(_window, Win32.GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(_window, Win32.GwlExStyle, new nint(extended | Win32.WsExToolWindow));
        IsCaptureExcluded = Win32.SetWindowDisplayAffinity(_window, 0x11);
        Win32.TryRoundWindow(_window);

        _thumbnail = new Image
        {
            Source = BitmapSources.FromRgba(capture.PreviewWidth, capture.PreviewHeight, capture.PreviewRgba),
            Stretch = Stretch.Uniform, CanDrag = true, IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(_thumbnail, video is null
            ? "Screenshot. Double-click or press Enter to open. Drag to another app, or use Copy, Save and Delete."
            : "Recording. Double-click or press Enter to play. Drag to another app, or use Copy, Save and Delete.");
        AutomationProperties.SetAutomationId(_thumbnail, video is null ? "ScreenshotThumbnail" : "RecordingThumbnail");
        _thumbnail.PointerPressed += (_, _) => _thumbnail.Focus(FocusState.Pointer);
        _thumbnail.DoubleTapped += (_, args) => { args.Handled = true; _ = OpenAsync(); };
        ToolTipService.SetToolTip(_thumbnail, video is null ? "Double-click to open" : "Double-click to play");
        _thumbnail.DragStarting += DragStarting;
        _thumbnail.DropCompleted += DropCompleted;
        _surface.Children.Add(_thumbnail);

        if (video is not null)
        {
            var play = new Border
            {
                Child = new FontIcon { Glyph = "\uE768", FontSize = 18,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] },
                Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"],
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Opacity = .92,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            AutomationProperties.SetAutomationId(play, "RecordingPlayBadge");
            _playBadge = play;
            _surface.Children.Add(play);
        }

        string kind = video is null ? "screenshot" : "recording";
        _copy = IconButton("\uE8C8", $"Copy {kind}", video is null ? "CopyScreenshot" : "CopyRecording", "Copy (Ctrl+C)");
        _save = IconButton("\uE74E", $"Save {kind}", video is null ? "SaveScreenshot" : "SaveRecording", "Save (Ctrl+S)");
        _delete = IconButton("\uE74D", $"Delete {kind}", video is null ? "DeleteScreenshot" : "DeleteRecording", $"Delete {kind} (Delete)");
        _dismiss = IconButton("\uE8BB", $"Dismiss {kind} preview", "DismissPreview", "Dismiss (Esc)");
        _copy.Click += async (_, _) => await CopyAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _dismiss.Click += (_, _) => RequestClose();
        _controls.Children.Add(_copy); _controls.Children.Add(_save); _controls.Children.Add(_delete); _controls.Children.Add(_dismiss);
        _controlHost = new Border
        {
            Child = _controls, Padding = new Thickness(3), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(5), Visibility = Visibility.Collapsed
        };
        _surface.Children.Add(_controlHost);

        AutomationProperties.SetAutomationId(_status, "PreviewStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _statusHost = new Border
        {
            Child = _status, Padding = new Thickness(7, 4, 7, 4), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5), Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _surface.Children.Add(_statusHost);
        _surface.PointerEntered += (_, _) => { _pointerInside = true; UpdateControls(); };
        _surface.PointerExited += (_, args) =>
        {
            var point = args.GetCurrentPoint(_surface).Position;
            // Child transitions must not blink controls while the pointer stays inside the image.
            if (point.X >= 0 && point.Y >= 0 && point.X < _surface.ActualWidth && point.Y < _surface.ActualHeight) return;
            _pointerInside = false; UpdateControls();
        };
        _surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { _keyboardMode = false; UpdateControls(); }), true);
        _surface.GotFocus += (_, _) =>
        {
            _keyboardMode = FocusManager.GetFocusedElement(_surface.XamlRoot) is UIElement focused && focused.FocusState == FocusState.Keyboard;
            UpdateControls();
        };
        _surface.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(UpdateControls);
        _surface.PreviewKeyDown += PreviewKeyDown;
        _surface.ActualThemeChanged += (_, _) => ApplyTheme();
        _themeHook = new WindowHook(_window, (message, _, _) =>
        {
            if (message is 0x001A or 0x031A)
                DispatcherQueue.TryEnqueue(() => { if (!_closed) ApplyTheme(); });
            if (message is 0x001A or 0x007E or 0x02E0) QueueDisplayRefresh();
            return null;
        });
        Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!_windowActive) _keyboardMode = false;
            UpdateControls();
        };
        Content = _surface;
        ApplyTheme();

        var initial = DisplayArea.GetFromRect(new RectInt32(capture.WorkArea.Left, capture.WorkArea.Top,
            capture.WorkArea.Width, capture.WorkArea.Height), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(initial.X, initial.Y));
        RefreshDisplayGeometry(notify: false);
        int margin = (int)Math.Round(20 * _displayScale);
        MoveTo(_workArea.Right - PixelSize.Width - margin, _workArea.Bottom - PixelSize.Height - margin);
        AppWindow.Changed += (_, args) =>
        {
            if (!_refreshingDisplay && (args.DidPositionChange || args.DidSizeChange)) QueueDisplayRefresh();
        };
        AppWindow.Closing += (_, args) => { if (IsBusy) { _closePending = true; args.Cancel = true; } };
        Closed += (_, _) =>
        {
            _closed = true;
            _operations.StopAccepting();
            _interactiveCancellation.Dispose();
            _themeHook.Dispose();
        };
    }

    internal void MoveTo(int x, int y)
    {
        if (!_closed && (AppWindow.Position.X != x || AppWindow.Position.Y != y)) AppWindow.Move(new PointInt32(x, y));
    }

    internal void RefreshDisplayGeometry(bool notify = true)
    {
        if (_closed || _refreshingDisplay) return;
        _refreshingDisplay = true;
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var work = new Win32.Rect { Left = area.X, Top = area.Y, Right = area.X + area.Width, Bottom = area.Y + area.Height };
            uint dpi = Win32.GetDpiForWindow(_window);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            var size = PreviewGeometry.Fit(_capture.Width, _capture.Height, work, scale, _statusHost.Visibility == Visibility.Visible);
            bool changed = !_workArea.Equals(work) || _displayScale != scale || PixelSize.Width != size.Width || PixelSize.Height != size.Height;
            _workArea = work; _displayScale = scale;
            if (PixelSize.Width != size.Width || PixelSize.Height != size.Height) AppWindow.Resize(size);
            ArrangeActionButtons(size.Width / scale);
            if (changed && notify) LayoutChanged?.Invoke();
        }
        finally { _refreshingDisplay = false; }
    }

    private void ArrangeActionButtons(double logicalWidth)
    {
        int columns = PreviewGeometry.ToolbarColumns(logicalWidth);
        if (_controlColumns == columns) return;
        _controlColumns = columns;
        _controls.ColumnDefinitions.Clear();
        _controls.RowDefinitions.Clear();
        for (int column = 0; column < columns; column++)
            _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int row = 0; row < 4 / columns; row++)
            _controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < _controls.Children.Count; index++)
        {
            var button = (Button)_controls.Children[index];
            Grid.SetColumn(button, index % columns);
            Grid.SetRow(button, index / columns);
        }
    }

    private void QueueDisplayRefresh()
    {
        if (_closed || _displayRefreshQueued) return;
        _displayRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _displayRefreshQueued = false;
            if (!_closed) RefreshDisplayGeometry();
        });
    }

    internal void StopAcceptingOperations()
    {
        if (_closed) return;
        _operations.StopAccepting();
        _interactiveCancellation.Cancel();
        if (_dragStarting is not null) _dragStarting.Cancel = true;
        _dragPreparation?.Cancel();
        // CancelDirectManipulations only cancels panning/zooming, not an active shell drag.
        // A drag already handed to Windows holds its operation until DropCompleted.
        if (_dragOperation is null) _thumbnail.CanDrag = false;
        UpdateControls();
    }

    internal Task WaitForIdleAsync() => _operations.WaitForIdleAsync();

    internal PreviewOperationGate.Operation? TryBeginLinkedOperation()
    {
        if (_closed || _capture.IsDeleted) return null;
        var operation = _operations.TryBegin();
        if (operation is null) return null;
        try { UpdateControls(); return operation; }
        catch { operation.Dispose(); throw; }
    }

    internal void CompleteLinkedOperation(PreviewOperationGate.Operation operation)
    {
        operation.Dispose();
        UpdateControls();
    }

    internal void RefreshActionAvailability() => UpdateControls();

    internal void Hide()
    {
        if (_closed) return;
        _pointerInside = false;
        AppWindow.Hide();
    }

    internal void ShowWithoutActivation()
    {
        if (_closed || _capture.IsDeleted || _video?.IsDeleted == true) return;
        _pointerInside = false;
        UpdateControls();
        if (!_closed) AppWindow.Show(false);
    }

    internal bool DeferCloseIfBusy()
    {
        if (!IsBusy) return false;
        _closePending = true;
        return true;
    }

    private void RequestClose()
    {
        if (!_closed && !DeferCloseIfBusy()) Close();
    }

    private static Button IconButton(string glyph, string name, string id, string tooltip)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 14 },
            Width = 28, Height = 28, MinWidth = 28, MinHeight = 28,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private void ApplyTheme()
    {
        _surface.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        _surface.CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"];
        _controlHost.Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"];
        _controlHost.Opacity = Win32.IsHighContrast ? 1 : .92;
        _statusHost.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        _status.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        if (_playBadge is not null)
        {
            _playBadge.Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"];
            _playBadge.Opacity = Win32.IsHighContrast ? 1 : .92;
            ((FontIcon)_playBadge.Child).Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
    }

    private bool HasFocusWithin()
    {
        if (!_windowActive || _surface.XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(_surface.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is Grid focusedGrid && focusedGrid == _surface) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private void UpdateControls()
    {
        if (_closed) return;
        bool visible = _pointerInside || (_keyboardMode && HasFocusWithin()) || IsBusy;
        _controls.Visibility = _controlHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        bool available = _operations.IsAccepting && !IsBusy && !_capture.IsDeleted && _video?.IsDeleted != true;
        _copy.IsEnabled = _save.IsEnabled = available;
        _delete.IsEnabled = _operations.IsAccepting && !IsBusy && _owner.CanDelete(_capture);
        if (_dragOperation is null) _thumbnail.CanDrag = available;
        if (_reportedBusy != IsBusy)
        {
            _reportedBusy = IsBusy;
            _owner.RefreshActionAvailability();
        }
        if (!_closed && _closePending && !IsBusy) Close();
    }

    private void PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!HasFocusWithin()) return;
        _keyboardMode = true;
        UpdateControls();
        if (args.Key == VirtualKey.Escape) { args.Handled = true; RequestClose(); return; }
        if (args.Key == VirtualKey.Delete) { args.Handled = true; _ = DeleteAsync(); return; }
        if (args.Key == VirtualKey.Enter && ReferenceEquals(FocusManager.GetFocusedElement(_surface.XamlRoot), _thumbnail))
        {
            args.Handled = true; _ = OpenAsync(); return;
        }
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!control || IsBusy) return;
        if (args.Key == VirtualKey.C) { args.Handled = true; _ = CopyAsync(); }
        else if (args.Key == VirtualKey.S) { args.Handled = true; _ = SaveAsync(); }
    }

    private async void DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        bool ownsOperation = false;
        try
        {
            var operation = _closed ? null : _operations.TryBegin();
            if (operation is null) { args.Cancel = true; return; }
            _dragOperation = operation; ownsOperation = true;
            _dragStarting = args;
            _dragPreparation = new CancellationTokenSource();
            ClearFeedback(); UpdateControls();
            args.AllowedOperations = DataPackageOperation.Copy;
            if (_video is null)
            {
                byte[] png = await _capture.ReadPngAsync(_dragPreparation.Token);
                await PreviewDragData.PrepareAsync(args.Data, png, _dragPreparation.Token);
            }
            else
            {
                await _video.PrepareDataAsync(args.Data, _dragPreparation.Token);
            }
            if (!_operations.IsAccepting) args.Cancel = true;
        }
        catch (OperationCanceledException) when (!_operations.IsAccepting) { args.Cancel = true; }
        catch (Exception error)
        {
            args.Cancel = true;
            ShowFailure("Drag failed", error);
        }
        finally
        {
            bool cancelled = args.Cancel;
            if (ownsOperation)
            {
                _dragStarting = null;
                _dragPreparation?.Dispose(); _dragPreparation = null;
            }
            try { deferral.Complete(); }
            catch (Exception error) { cancelled = true; ReportFailure(error); }
            if (ownsOperation && cancelled) CompleteDrag();
            UpdateControls();
        }
    }

    private void DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        CompleteDrag();
        UpdateControls();
    }

    private void CompleteDrag()
    {
        _dragOperation?.Dispose(); _dragOperation = null;
        if (!_operations.IsAccepting) _thumbnail.CanDrag = false;
    }

    private Task OpenAsync() => RunOperationAsync("Open failed", async () =>
    {
        string? savedPath = _video?.Path ?? _owner.SavedPath(_capture);
        if (savedPath is not null)
        {
            CancellationToken token = _interactiveCancellation.Token;
            var file = await StorageFile.GetFileFromPathAsync(savedPath).AsTask(token);
            Activate();
            if (!await Windows.System.Launcher.LaunchFileAsync(file).AsTask(token))
                throw new InvalidOperationException("Windows could not open the saved capture.");
            _closePending = true;
            return;
        }
        if (_openFile is null || !File.Exists(_openFile.Path))
        {
            byte[] png = await _capture.ReadPngAsync();
            _openFile = await ScreenshotFileCache.ExportFileAsync(png);
        }
        CancellationToken interactiveToken = _interactiveCancellation.Token;
        interactiveToken.ThrowIfCancellationRequested();
        Activate();
        if (!await Windows.System.Launcher.LaunchFileAsync(_openFile).AsTask(interactiveToken))
            throw new InvalidOperationException("Windows could not open the PNG. Use Save to choose a location.");
        _closePending = true;
    });

    private Task CopyAsync() => RunOperationAsync("Copy failed", async () =>
    {
        if (_video is not null)
        {
            var videoPackage = new DataPackage();
            await _video.PrepareDataAsync(videoPackage);
            Clipboard.SetContent(videoPackage); Clipboard.Flush();
            await ConfirmAndCloseAsync("Copied");
            return;
        }
        byte[] png = await _capture.ReadPngAsync();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream();
        }
        stream.Seek(0);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(package); Clipboard.Flush();
        await ConfirmAndCloseAsync("Copied");
    });

    private Task SaveAsync() => RunOperationAsync("Save failed", async () =>
    {
        if (_video is not null)
        {
            var videoPicker = new FileSavePicker { SuggestedFileName = $"Recording {DateTime.Now:yyyy-MM-dd HHmmss}" };
            videoPicker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
            WinRT.Interop.InitializeWithWindow.Initialize(videoPicker, _window);
            CancellationToken token = _interactiveCancellation.Token;
            var destination = await videoPicker.PickSaveFileAsync().AsTask(token);
            if (destination is null) return;
            if (!StringComparer.OrdinalIgnoreCase.Equals(destination.Path, _video.Path))
            {
                var source = await StorageFile.GetFileFromPathAsync(_video.Path);
                await source.CopyAndReplaceAsync(destination);
            }
            await ConfirmAndCloseAsync("Saved");
            return;
        }
        var picker = new FileSavePicker { SuggestedFileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}" };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window);
        CancellationToken interactiveToken = _interactiveCancellation.Token;
        interactiveToken.ThrowIfCancellationRequested();
        var file = await picker.PickSaveFileAsync().AsTask(interactiveToken);
        if (file is null) return;
        if (!_owner.IsSavedFile(_capture, file.Path))
        {
            byte[] png = await _capture.ReadPngAsync();
            await Windows.Storage.FileIO.WriteBytesAsync(file, png);
        }
        await ConfirmAndCloseAsync("Saved");
    });

    internal async Task DeleteAsync()
    {
        if (_closed || !_operations.IsAccepting || !_owner.CanDelete(_capture)) return;
        try { ClearFeedback(); await _owner.DeleteAsync(this); }
        catch (Exception error) { ShowFailure("Delete failed", error); }
    }

    private async Task RunOperationAsync(string failureMessage, Func<Task> operationBody)
    {
        if (_closed || _capture.IsDeleted || _video?.IsDeleted == true) return;
        var operation = _operations.TryBegin();
        if (operation is null) return;
        try
        {
            ClearFeedback(); UpdateControls();
            await operationBody();
        }
        catch (OperationCanceledException) when (!_operations.IsAccepting) { }
        catch (Exception error) { ShowFailure(failureMessage, error); }
        finally { operation.Dispose(); UpdateControls(); }
    }

    private void ShowFailure(string message, Exception error)
    {
        ReportFailure(error);
        if (_closed) return;
        ShowFeedback(message);
    }

    internal void ShowStorageFailure(Exception error) => ShowFailure("Image cache failed", error);

    private void ReportFailure(Exception error)
    {
        System.Diagnostics.Trace.WriteLine(error);
        try { Failed?.Invoke(error); }
        catch (Exception reportingError) { System.Diagnostics.Trace.WriteLine(reportingError); }
    }

    private void ShowFeedback(string message)
    {
        _status.Text = message;
        _statusHost.Visibility = Visibility.Visible;
        RefreshDisplayGeometry();
        FrameworkElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ClearFeedback()
    {
        _status.Text = "";
        _statusHost.Visibility = Visibility.Collapsed;
        RefreshDisplayGeometry();
    }

    private async Task ConfirmAndCloseAsync(string message)
    {
        if (_closed) return;
        ShowFeedback(message);
        if (_operations.IsAccepting) await Task.Delay(350);
        RequestClose();
    }
}
