using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Windows.Graphics;
using Windows.System;

namespace Ovi.Screenshot.Views;

// The selected area is already known. This window only collects the options for
// the next recording; the capture coordinator owns persistence and startup.
internal sealed class RecordingSetupWindow : Window
{
    private readonly TaskCompletionSource<RecordingOptions?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ComboBox _fps = new() { Width = 104, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _system = new() { Content = "System", MinWidth = 78, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _microphone = new() { Content = "Mic", MinWidth = 60, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _outputDevice = new() { Header = "System sound device", MinWidth = 240 };
    private readonly ComboBox _microphoneDevice = new() { Header = "Microphone device", MinWidth = 240 };
    private readonly Button _record = new() { Content = "Record", MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private bool _started, _closed;

    internal RecordingSetupWindow(Win32.Rect selectionBounds, Win32.Rect workArea,
        RecordingOptions initial, AudioDeviceCatalog devices)
    {
        initial.Validate();
        ArgumentNullException.ThrowIfNull(devices);
        Title = "Recording setup";
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
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
        _record.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var cancel = new Button
        {
            Content = new FontIcon { Glyph = "\uE8BB", FontSize = 13 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
        };
        Identify(_fps, "RecordingSetupFrameRate", "Recording frame rate");
        Identify(_system, "RecordingSetupSystemAudio", "Record system sound");
        Identify(_microphone, "RecordingSetupMicrophone", "Record microphone");
        Identify(_outputDevice, "RecordingSetupOutputDevice", "System sound output device");
        Identify(_microphoneDevice, "RecordingSetupMicrophoneDevice", "Microphone device");
        Identify(settings, "RecordingSetupDevices", "Choose audio devices");
        Identify(_record, "StartRecording", "Start recording");
        Identify(cancel, "CancelRecordingSetup", "Cancel recording");
        ToolTipService.SetToolTip(_system, "Record system sound");
        ToolTipService.SetToolTip(_microphone, "Record microphone");
        ToolTipService.SetToolTip(settings, "Audio devices");
        ToolTipService.SetToolTip(cancel, "Cancel (Esc)");
        _record.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Cancel();
        actions.Children.Add(_fps); actions.Children.Add(_system); actions.Children.Add(_microphone);
        actions.Children.Add(settings); actions.Children.Add(_record); actions.Children.Add(cancel);
        surface.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; Cancel(); }
        };
        surface.Loaded += (_, _) => { _loaded.TrySetResult(); _record.Focus(FocusState.Programmatic); };
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
        Closed += (_, _) => { _closed = true; _result.TrySetResult(null); };
    }

    internal async Task<RecordingOptions?> RunAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("Recording setup can only run once.");
        _started = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_result.Task.IsCompleted) return await _result.Task;
            Activate();
            return await _result.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        finally { if (!_closed) Close(); }
    }

    // Safe to call while startup/shutdown is in flight; RunAsync closes the HWND.
    internal void Cancel() => _result.TrySetResult(null);

    private void Confirm()
    {
        var options = new RecordingOptions((int)((ComboBoxItem)_fps.SelectedItem).Tag,
            _system.IsChecked == true, _microphone.IsChecked == true,
            ((AudioDeviceChoice)((ComboBoxItem)_outputDevice.SelectedItem).Tag).Id,
            ((AudioDeviceChoice)((ComboBoxItem)_microphoneDevice.SelectedItem).Tag).Id);
        options.Validate();
        _result.TrySetResult(options);
    }

    private static void PopulateDevices(ComboBox combo, IReadOnlyList<AudioDeviceChoice> devices, string? selectedId)
    {
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
        var setup = new RecordingSetupWindow(selection, workArea, initial, devices);
        var result = setup.RunAsync(CancellationToken.None);
        await setup._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var position = setup.AppWindow.Position;
        var size = setup.AppWindow.Size;
        Require(position.X >= workArea.Left && position.Y >= workArea.Top &&
            position.X + size.Width <= workArea.Right && position.Y + size.Height <= workArea.Bottom,
            "toolbar placement escaped the work area");
        Require(setup._outputDevice.SelectedItem is ComboBoxItem { Content: "Unavailable device" },
            "an unavailable saved audio device was silently replaced");
        if (render is not null) await render((FrameworkElement)setup.Content);
        setup._fps.SelectedIndex = 3;
        setup._system.IsChecked = true;
        setup._microphone.IsChecked = false;
        setup.Confirm();
        Require(await result == (initial with { FramesPerSecond = 60, SystemAudio = true, Microphone = false }) && setup._closed,
            "Record did not return the chosen options or close the toolbar");

        var cancelled = new RecordingSetupWindow(selection, workArea, new(), devices);
        var cancelResult = cancelled.RunAsync(CancellationToken.None);
        await cancelled._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Cancel();
        Require(await cancelResult is null && cancelled._closed, "Cancel did not close recording setup");

        var shutdown = new RecordingSetupWindow(selection, workArea, new(), devices);
        using var cancellation = new CancellationTokenSource();
        var shutdownResult = shutdown.RunAsync(cancellation.Token);
        await shutdown._loaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        Require(await shutdownResult is null && shutdown._closed, "Shutdown did not cancel and close recording setup");

        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException($"Recording setup regression: {scenario}.");
        }
    }
}
