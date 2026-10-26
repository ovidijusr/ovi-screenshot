using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using System.Runtime.InteropServices;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Windows.Graphics;
using Windows.System;

namespace Ovi.Screenshot.Views;

// The coordinator owns selection, persistence and recording startup. This
// toolbar can coexist with the selection overlay without taking initial focus.
internal sealed class CaptureSetupWindow : Window
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RadioButton _screenshotMode = new() { Content = "Screenshot", VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _videoModeButton = new() { Content = "Video", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _fps = new() { Width = 104, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _system = new() { Content = "System", MinWidth = 78, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _microphone = new() { Content = "Mic", MinWidth = 60, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _outputDevice = new() { Header = "System sound device", MinWidth = 240 };
    private readonly ComboBox _microphoneDevice = new() { Header = "Microphone device", MinWidth = 240 };
    private readonly Button _record = new() { Content = "Record", MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _devicesButton;
    private readonly nint _handle;
    private readonly Win32.Rect _workArea;
    private readonly double _scale;
    private readonly int _margin;
    private nint _selector;
    private bool _started, _closed, _selectionMode, _closingSafely, _actionTaken, _keyboardFocusPending, _hadSelectionForeground, _isVideo;
    internal nint Handle => _handle;
    internal bool IsClosed => _closed;

    internal event Action<RecordingOptions>? RecordRequested;
    internal event Action? ScreenshotRequested;
    internal event Action<bool>? VideoModeChanged;
    internal event Action? CancelRequested;
    internal event Action? FocusSelectionRequested;

    internal CaptureSetupWindow(Win32.Rect selectionBounds, Win32.Rect workArea,
        RecordingOptions initial, AudioDeviceCatalog devices, bool initialVideo = true)
    {
        initial.Validate();
        ArgumentNullException.ThrowIfNull(devices);
        _isVideo = initialVideo;
        _workArea = workArea;
        Title = "Capture setup";
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _handle = window;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        long extended = Win32.GetWindowLongPtr(window, Win32.GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(window, Win32.GwlExStyle, new nint(extended | Win32.WsExToolWindow));
        Win32.SetWindowDisplayAffinity(window, 0x11);
        Win32.TryRoundWindow(window);
        Win32.DisableWindowTransitions(window);

        var surface = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                    CornerRadius="{ThemeResource OverlayCornerRadius}">
                <ScrollViewer HorizontalScrollBarVisibility="Auto" HorizontalScrollMode="Enabled"
                              VerticalScrollBarVisibility="Disabled" VerticalScrollMode="Disabled">
                    <StackPanel x:Name="Actions" Orientation="Horizontal" Spacing="4" Padding="8"
                                VerticalAlignment="Center"/>
                </ScrollViewer>
            </Border>
            """);
        var actions = (StackPanel)surface.FindName("Actions");
        string modeGroup = $"CaptureMode_{Guid.NewGuid():N}";
        _screenshotMode.GroupName = _videoModeButton.GroupName = modeGroup;
        _screenshotMode.IsChecked = !initialVideo;
        _videoModeButton.IsChecked = initialVideo;
        _screenshotMode.Checked += (_, _) => SetMode(false);
        _videoModeButton.Checked += (_, _) => SetMode(true);
        foreach (int fps in new[] { 15, 24, 30, 60 })
        {
            var item = new ComboBoxItem { Content = $"{fps} fps", Tag = fps };
            _fps.Items.Add(item);
            if (fps == initial.FramesPerSecond) _fps.SelectedItem = item;
        }
        _system.IsChecked = initial.SystemAudio;
        _microphone.IsChecked = initial.Microphone;
        PopulateDevices(_outputDevice, devices.Outputs, initial.OutputDeviceId);
        PopulateDevices(_microphoneDevice, devices.Microphones, initial.MicrophoneDeviceId);

        var deviceOptions = new StackPanel { Spacing = 12, Padding = new Thickness(4) };
        deviceOptions.Children.Add(_outputDevice);
        deviceOptions.Children.Add(_microphoneDevice);
        var settings = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 15 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Flyout = new Flyout { Content = deviceOptions }
        };
        _devicesButton = settings;
        _record.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var cancel = new Button
        {
            Content = new FontIcon { Glyph = "\uE8BB", FontSize = 13 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
        };
        Identify(_fps, "RecordingSetupFrameRate", "Recording frame rate");
        Identify(_screenshotMode, "CaptureModeScreenshot", "Screenshot mode");
        Identify(_videoModeButton, "CaptureModeVideo", "Video mode");
        AutomationProperties.SetHelpText(_screenshotMode, "Press F6 to return to the capture selection.");
        AutomationProperties.SetHelpText(_fps, "Press F6 to return to the capture selection.");
        Identify(_system, "RecordingSetupSystemAudio", "Record system sound");
        Identify(_microphone, "RecordingSetupMicrophone", "Record microphone");
        Identify(_outputDevice, "RecordingSetupOutputDevice", "System sound output device");
        Identify(_microphoneDevice, "RecordingSetupMicrophoneDevice", "Microphone device");
        Identify(settings, "RecordingSetupDevices", "Choose audio devices");
        Identify(_record, "StartRecording", "Start recording");
        Identify(cancel, "CancelRecordingSetup", "Cancel recording");
        ToolTipService.SetToolTip(_system, "Record system sound");
        ToolTipService.SetToolTip(_microphone, "Record microphone");
        ToolTipService.SetToolTip(_fps, "Frame rate (F6 returns to selection)");
        ToolTipService.SetToolTip(settings, "Audio devices");
        ToolTipService.SetToolTip(cancel, "Cancel (Esc)");
        _record.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Cancel();
        actions.Children.Add(_screenshotMode); actions.Children.Add(_videoModeButton);
        actions.Children.Add(_fps); actions.Children.Add(_system); actions.Children.Add(_microphone);
        actions.Children.Add(settings); actions.Children.Add(_record); actions.Children.Add(cancel);
        UpdateModeControls();
        surface.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; Cancel(); }
            else if (args.Key == VirtualKey.F6) { args.Handled = true; FocusSelectionRequested?.Invoke(); }
        };
        surface.Loaded += (_, _) =>
        {
            _loaded.TrySetResult();
            if (_keyboardFocusPending) { _keyboardFocusPending = false; _screenshotMode.Focus(FocusState.Keyboard); }
        };
        Content = surface;

        AppWindow.Move(new PointInt32(workArea.Left, workArea.Top));
        double scale = Math.Max(1, Win32.GetDpiForWindow(window) / 96d);
        int margin = Math.Min((int)Math.Round(12 * scale), Math.Max(0, Math.Min(workArea.Width, workArea.Height) / 4));
        _scale = scale; _margin = margin;
        int width = ModeWidth(initialVideo);
        int height = Math.Min((int)Math.Round(56 * scale), Math.Max(1, workArea.Height - 2 * margin));
        long centerX = ((long)selectionBounds.Left + selectionBounds.Right) / 2;
        long below = (long)selectionBounds.Bottom + margin;
        long above = (long)selectionBounds.Top - height - margin;
        long y = below + height <= (long)workArea.Bottom - margin ? below
            : above >= (long)workArea.Top + margin ? above : (long)workArea.Bottom - height - margin;
        int x = (int)Math.Clamp(centerX - width / 2, (long)workArea.Left + margin, (long)workArea.Right - width - margin);
        int top = (int)Math.Clamp(y, (long)workArea.Top + margin, (long)workArea.Bottom - height - margin);
        AppWindow.MoveAndResize(new RectInt32(x, top, width, height));
        Closed += (_, _) =>
        {
            _closed = true;
            NotifyCancel();
        };
        Activated += (_, args) =>
        {
            if (!_selectionMode) return;
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                ObserveSelectionForeground();
                DispatcherQueue.TryEnqueue(ObserveSelectionForeground);
                return;
            }
            // A WinUI Flyout may activate another owned HWND. Check after the
            // native activation transition finishes, not inside its callback.
            DispatcherQueue.TryEnqueue(CancelIfFocusLeftSelection);
        };
    }

    // Ownership keeps the toolbar above the selector; selector activation also
    // reasserts its z-order so the controls stay reachable.
    internal void AttachOwner(nint selector)
    {
        if (_started || _closed || selector == 0) throw new InvalidOperationException("A visible selection owner is required.");
        const int ownerIndex = -8; // GWLP_HWNDPARENT for a top-level window.
        Win32.SetWindowLongPtr(_handle, ownerIndex, selector);
        if (Win32.GetWindowLongPtr(_handle, ownerIndex) != selector)
            throw new InvalidOperationException("The recording toolbar could not be attached to the selector.");
        _selector = selector;
    }

    internal void ShowForSelection()
    {
        if (_started || _closed) throw new InvalidOperationException("Recording setup can only be shown once.");
        _started = true;
        _selectionMode = true;
        _record.IsEnabled = false;
        ObserveSelectionForeground();
        AppWindow.Show(false);
        DispatcherQueue.TryEnqueue(ObserveSelectionForeground);
    }

    internal void SetCanCapture(bool canRecord)
    {
        if (!_closed && !_closingSafely && !_actionTaken) _record.IsEnabled = canRecord;
    }

    internal void SetVideoAvailable(bool available)
    {
        if (_closed || _closingSafely) return;
        _videoModeButton.IsEnabled = available;
        string hint = available ? "Record a video" : "Save or discard the previous recording first";
        ToolTipService.SetToolTip(_videoModeButton, hint);
        AutomationProperties.SetHelpText(_videoModeButton, hint);
        if (!available && _isVideo) _screenshotMode.IsChecked = true;
    }

    internal void SetDevices(AudioDeviceCatalog devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (_closed || _closingSafely) return;
        string? output = SelectedDevice(_outputDevice);
        string? microphone = SelectedDevice(_microphoneDevice);
        PopulateDevices(_outputDevice, devices.Outputs, output);
        PopulateDevices(_microphoneDevice, devices.Microphones, microphone);
    }

    internal void ActivateForKeyboard()
    {
        if (_closed || _closingSafely || _actionTaken || !_selectionMode) return;
        Activate();
        if (_screenshotMode.IsLoaded) _screenshotMode.Focus(FocusState.Keyboard);
        else _keyboardFocusPending = true;
    }

    internal void RaiseWithoutActivation()
    {
        if (_closed || _closingSafely || _actionTaken || !_selectionMode || !AppWindow.IsVisible) return;
        const uint noSize = 0x0001, noMove = 0x0002;
        if (!Win32.SetWindowPos(_handle, Win32.HwndTopmost, 0, 0, 0, 0, noSize | noMove | Win32.SwpNoActivate))
            throw new InvalidOperationException("The recording toolbar could not be kept above the selector.");
    }

    internal void CloseSafely()
    {
        _closingSafely = true;
        if (!_closed) Close();
    }

    // The coordinator closes the window after the cancellation notification.
    internal void Cancel()
    {
        NotifyCancel();
    }

    private void NotifyCancel()
    {
        if (!_selectionMode || _closingSafely || _actionTaken) return;
        _actionTaken = true;
        CancelRequested?.Invoke();
    }

    private void CancelIfFocusLeftSelection()
    {
        if (_closed || _closingSafely || _actionTaken || _selector == 0) return;
        nint foreground = Win32.GetForegroundWindow();
        if (IsSelectionForeground(foreground)) { _hadSelectionForeground = true; return; }
        if (!_hadSelectionForeground) return;
        Cancel();
    }

    private void ObserveSelectionForeground()
    {
        if (!_closed && IsSelectionForeground(Win32.GetForegroundWindow())) _hadSelectionForeground = true;
    }

    private bool IsSelectionForeground(nint foreground)
    {
        nint root = foreground == 0 ? 0 : GetAncestor(foreground, 3); // GA_ROOTOWNER
        return foreground != 0 && (foreground == _selector || foreground == _handle || root == _selector || root == _handle);
    }

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    private void Confirm()
    {
        if (!_record.IsEnabled || _actionTaken) return;
        if (!_isVideo)
        {
            _actionTaken = true;
            ScreenshotRequested?.Invoke();
            return;
        }
        var options = new RecordingOptions((int)((ComboBoxItem)_fps.SelectedItem).Tag,
            _system.IsChecked == true, _microphone.IsChecked == true,
            ((AudioDeviceChoice)((ComboBoxItem)_outputDevice.SelectedItem).Tag).Id,
            ((AudioDeviceChoice)((ComboBoxItem)_microphoneDevice.SelectedItem).Tag).Id);
        options.Validate();
        if (_selectionMode)
        {
            _actionTaken = true;
            RecordRequested?.Invoke(options);
        }
    }

    private void SetMode(bool video)
    {
        if (_isVideo == video) return;
        _isVideo = video;
        UpdateModeControls();
        ResizeForMode();
        VideoModeChanged?.Invoke(video);
    }

    private int ModeWidth(bool video) => Math.Min((int)Math.Round((video ? 700 : 420) * _scale),
        Math.Max(1, _workArea.Width - 2 * _margin));

    private void ResizeForMode()
    {
        if (_closed || _closingSafely) return;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        int width = ModeWidth(_isVideo);
        long center = (long)position.X + size.Width / 2;
        int left = (int)Math.Clamp(center - width / 2, (long)_workArea.Left + _margin, (long)_workArea.Right - width - _margin);
        AppWindow.MoveAndResize(new RectInt32(left, position.Y, width, size.Height));
    }

    private void UpdateModeControls()
    {
        var visibility = _isVideo ? Visibility.Visible : Visibility.Collapsed;
        _fps.Visibility = _system.Visibility = _microphone.Visibility = _devicesButton.Visibility = visibility;
        _record.Content = _isVideo ? "Record" : "Capture";
        AutomationProperties.SetName(_record, _isVideo ? "Start recording" : "Capture screenshot");
    }

    private static void PopulateDevices(ComboBox combo, IReadOnlyList<AudioDeviceChoice> devices, string? selectedId)
    {
        combo.Items.Clear();
        var choices = new List<AudioDeviceChoice> { devices.FirstOrDefault(device => device.Id is null) ?? new(null, "System default") };
        choices.AddRange(devices.Where(device => device.Id is not null).DistinctBy(device => device.Id!, StringComparer.Ordinal));
        if (selectedId is not null && choices.All(device => device.Id != selectedId))
            choices.Add(new(selectedId, "Unavailable device"));
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice.Name, Tag = choice };
            combo.Items.Add(item);
            if (choice.Id == selectedId) combo.SelectedItem = item;
        }
    }

    private static string? SelectedDevice(ComboBox combo)
        => combo.SelectedItem is ComboBoxItem { Tag: AudioDeviceChoice device } ? device.Id : null;

    private static void Identify(DependencyObject control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }

    // Only generated choices and our own window are used by this fixture.
    internal static async Task VerifySyntheticAsync(Win32.Rect workArea, Func<FrameworkElement, Task>? render = null,
        Func<FrameworkElement, Task>? renderScreenshot = null)
    {
        var selection = new Win32.Rect { Left = workArea.Left + 30, Top = workArea.Top + 30,
            Right = workArea.Left + 230, Bottom = workArea.Top + 130 };
        var devices = new AudioDeviceCatalog([new(null, "System default"), new("fixture-output", "Demo speakers")],
            [new(null, "System default"), new("fixture-mic", "Demo microphone")]);
        var initial = new RecordingOptions(24, false, true, "missing-output", "fixture-mic");

        var ownerContent = new Grid();
        var ownerLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ownerContent.Loaded += (_, _) => ownerLoaded.TrySetResult();
        var owner = new Window { Content = ownerContent };
        try
        {
            owner.Activate();
            await ownerLoaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            nint ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            var live = new CaptureSetupWindow(selection, workArea, initial, devices);
            int starts = 0, cancels = 0;
            var modes = new List<bool>();
            RecordingOptions? chosen = null;
            live.RecordRequested += options => { starts++; chosen = options; };
            live.CancelRequested += () => cancels++;
            live.VideoModeChanged += modes.Add;
            live.AttachOwner(ownerHandle);
            nint foreground = Win32.GetForegroundWindow();
            live.ShowForSelection();
            await live._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(60);
            RequireNormalWidthFits(live, "video");
            var position = live.AppWindow.Position;
            var size = live.AppWindow.Size;
            Require(position.X >= workArea.Left && position.Y >= workArea.Top &&
                position.X + size.Width <= workArea.Right && position.Y + size.Height <= workArea.Bottom,
                "toolbar placement escaped the work area");
            Require(live._outputDevice.SelectedItem is ComboBoxItem { Content: "Unavailable device" },
                "an unavailable saved audio device was silently replaced");
            Require(Win32.GetWindowLongPtr(live._handle, -8) == ownerHandle,
                "the toolbar lost its selection owner");
            Require(Win32.GetForegroundWindow() == foreground, "showing the toolbar took initial focus from the selector");
            if (render is not null) await render((FrameworkElement)live.Content);
            live.ActivateForKeyboard();
            await Task.Delay(50);
            live._devicesButton.Flyout!.ShowAt(live._devicesButton);
            await Task.Delay(75);
            live.CancelIfFocusLeftSelection();
            Require(cancels == 0 && owner.AppWindow.IsVisible, "the audio devices flyout cancelled selection");
            live._devicesButton.Flyout!.Hide();
            owner.Activate();
            await Task.Delay(50);
            live.RaiseWithoutActivation();
            Require(cancels == 0 && !live._closed, "returning focus to the selector cancelled recording setup");
            live.Confirm();
            Require(starts == 0 && !live._record.IsEnabled, "Record became available before choosing an area");
            live._screenshotMode.IsChecked = true;
            await Task.Delay(60);
            Require(modes.SequenceEqual([false]) && live._fps.Visibility == Visibility.Collapsed &&
                (string)live._record.Content == "Capture", "Screenshot mode did not hide video settings and offer Capture");
            RequireNormalWidthFits(live, "screenshot after switching");
            live._videoModeButton.IsChecked = true;
            await Task.Delay(60);
            Require(modes.SequenceEqual([false, true]) && live._fps.Visibility == Visibility.Visible &&
                (string)live._record.Content == "Record", "Video mode did not restore recording settings");
            RequireNormalWidthFits(live, "video after switching");
            live.SetCanCapture(true);
            live._fps.SelectedIndex = 3;
            live._system.IsChecked = true;
            live._outputDevice.SelectedIndex = 1;
            live.SetDevices(new([], []));
            Require(SelectedDevice(live._outputDevice) == "fixture-output" && live._system.IsChecked == true &&
                SelectedDevice(live._microphoneDevice) == "fixture-mic" && (int)((ComboBoxItem)live._fps.SelectedItem).Tag == 60,
                "refreshing devices replaced the user's draft choices");
            live.Confirm();
            Require(starts == 1 && chosen == (initial with { FramesPerSecond = 60, SystemAudio = true, OutputDeviceId = "fixture-output" }),
                "Record did not publish selected options");
            live.CloseSafely();
            Require(live._closed && cancels == 0, "a programmatic close emitted cancellation");

            var still = new CaptureSetupWindow(selection, workArea, initial, devices, initialVideo: false);
            int screenshots = 0, accidentalRecords = 0;
            still.ScreenshotRequested += () => screenshots++;
            still.RecordRequested += _ => accidentalRecords++;
            still.AttachOwner(ownerHandle);
            still.ShowForSelection();
            await still._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(60);
            RequireNormalWidthFits(still, "initial screenshot");
            Require(still._fps.Visibility == Visibility.Collapsed && (string)still._record.Content == "Capture",
                "Screenshot mode did not initialize as a compact capture toolbar");
            still.SetVideoAvailable(false);
            Require(!still._videoModeButton.IsEnabled && still._screenshotMode.IsEnabled,
                "A pending recording did not leave screenshot mode available");
            still.SetVideoAvailable(true);
            Require(still._videoModeButton.IsEnabled, "Video mode did not return after pending work was resolved");
            still.Confirm();
            Require(screenshots == 0 && !still._record.IsEnabled,
                "Capture became available before choosing an area");
            if (renderScreenshot is not null) await renderScreenshot((FrameworkElement)still.Content);
            still.SetCanCapture(true);
            still.Confirm(); still.Confirm();
            Require(screenshots == 1 && accidentalRecords == 0, "Capture did not request exactly one screenshot");
            still.CloseSafely();

            var cancelLive = new CaptureSetupWindow(selection, workArea, new(), devices);
            int notifications = 0;
            cancelLive.CancelRequested += () => notifications++;
            cancelLive.AttachOwner(ownerHandle);
            cancelLive.ShowForSelection();
            await cancelLive._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelLive.Cancel(); cancelLive.Cancel();
            cancelLive.CloseSafely();
            Require(notifications == 1 && cancelLive._closed, "Cancel did not notify once and close cleanly");

            var closedLive = new CaptureSetupWindow(selection, workArea, new(), devices);
            notifications = 0;
            closedLive.CancelRequested += () => notifications++;
            closedLive.AttachOwner(ownerHandle);
            closedLive.ShowForSelection();
            await closedLive._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            closedLive.Close();
            Require(notifications == 1, "closing the toolbar did not cancel selection");

            var focusLive = new CaptureSetupWindow(selection, workArea, new(), devices);
            var other = new Window { Content = new Grid() };
            try
            {
                notifications = 0;
                focusLive.CancelRequested += () => notifications++;
                focusLive.AttachOwner(ownerHandle);
                focusLive.ShowForSelection();
                await focusLive._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
                focusLive.ActivateForKeyboard();
                await Task.Delay(50);
                focusLive.ObserveSelectionForeground();
                bool hadForeground = focusLive._hadSelectionForeground;
                other.Activate();
                await Task.Delay(100);
                Require(notifications == (hadForeground ? 1 : 0),
                    "focus-loss cancellation did not respect actual foreground ownership");
            }
            finally { focusLive.CloseSafely(); other.Close(); }
        }
        finally { owner.Close(); }

        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException($"Recording setup regression: {scenario}.");
        }

        static void RequireNormalWidthFits(CaptureSetupWindow toolbar, string mode)
        {
            int idealWidth = (int)Math.Round((toolbar._isVideo ? 700 : 420) * toolbar._scale);
            if (toolbar._workArea.Width - 2 * toolbar._margin < idealWidth) return;
            var surface = (Border)toolbar.Content;
            surface.UpdateLayout();
            var scroll = (ScrollViewer)surface.Child;
            Require(scroll.ViewportWidth > 0 && scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                $"{mode} controls overflow: extent {scroll.ExtentWidth:F1}, viewport {scroll.ViewportWidth:F1}");
        }
    }
}
