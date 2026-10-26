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
internal sealed class RecordingSetupWindow : Window
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox _fps = new() { Width = 104, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _system = new() { Content = "System", MinWidth = 78, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _microphone = new() { Content = "Mic", MinWidth = 60, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _outputDevice = new() { Header = "System sound device", MinWidth = 240 };
    private readonly ComboBox _microphoneDevice = new() { Header = "Microphone device", MinWidth = 240 };
    private readonly Button _record = new() { Content = "Record", MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _devicesButton;
    private readonly nint _handle;
    private nint _selector;
    private bool _started, _closed, _selectionMode, _closingSafely, _actionTaken, _keyboardFocusPending, _hadSelectionForeground;
    internal nint Handle => _handle;
    internal bool IsClosed => _closed;

    internal event Action<RecordingOptions>? RecordRequested;
    internal event Action? CancelRequested;
    internal event Action? FocusSelectionRequested;

    internal RecordingSetupWindow(Win32.Rect selectionBounds, Win32.Rect workArea,
        RecordingOptions initial, AudioDeviceCatalog devices)
    {
        initial.Validate();
        ArgumentNullException.ThrowIfNull(devices);
        Title = "Recording setup";
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
        actions.Children.Add(_fps); actions.Children.Add(_system); actions.Children.Add(_microphone);
        actions.Children.Add(settings); actions.Children.Add(_record); actions.Children.Add(cancel);
        surface.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; Cancel(); }
            else if (args.Key == VirtualKey.F6) { args.Handled = true; FocusSelectionRequested?.Invoke(); }
        };
        surface.Loaded += (_, _) =>
        {
            _loaded.TrySetResult();
            if (_keyboardFocusPending) { _keyboardFocusPending = false; _fps.Focus(FocusState.Keyboard); }
        };
        Content = surface;

        AppWindow.Move(new PointInt32(workArea.Left, workArea.Top));
        double scale = Math.Max(1, Win32.GetDpiForWindow(window) / 96d);
        int margin = Math.Min((int)Math.Round(12 * scale), Math.Max(0, Math.Min(workArea.Width, workArea.Height) / 4));
        int width = Math.Min((int)Math.Round(452 * scale), Math.Max(1, workArea.Width - 2 * margin));
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

    internal void SetCanRecord(bool canRecord)
    {
        if (!_closed && !_closingSafely && !_actionTaken) _record.IsEnabled = canRecord;
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
        if (_fps.IsLoaded) _fps.Focus(FocusState.Keyboard);
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
    internal static async Task VerifySyntheticAsync(Win32.Rect workArea, Func<FrameworkElement, Task>? render = null)
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
            var live = new RecordingSetupWindow(selection, workArea, initial, devices);
            int starts = 0, cancels = 0;
            RecordingOptions? chosen = null;
            live.RecordRequested += options => { starts++; chosen = options; };
            live.CancelRequested += () => cancels++;
            live.AttachOwner(ownerHandle);
            nint foreground = Win32.GetForegroundWindow();
            live.ShowForSelection();
            await live._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
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
            live.SetCanRecord(true);
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

            var cancelLive = new RecordingSetupWindow(selection, workArea, new(), devices);
            int notifications = 0;
            cancelLive.CancelRequested += () => notifications++;
            cancelLive.AttachOwner(ownerHandle);
            cancelLive.ShowForSelection();
            await cancelLive._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelLive.Cancel(); cancelLive.Cancel();
            cancelLive.CloseSafely();
            Require(notifications == 1 && cancelLive._closed, "Cancel did not notify once and close cleanly");

            var closedLive = new RecordingSetupWindow(selection, workArea, new(), devices);
            notifications = 0;
            closedLive.CancelRequested += () => notifications++;
            closedLive.AttachOwner(ownerHandle);
            closedLive.ShowForSelection();
            await closedLive._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            closedLive.Close();
            Require(notifications == 1, "closing the toolbar did not cancel selection");

            var focusLive = new RecordingSetupWindow(selection, workArea, new(), devices);
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
    }
}
