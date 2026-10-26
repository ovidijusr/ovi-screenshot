using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Views;

internal sealed class SettingsWindow : Window
{
    private readonly TextBox _shortcut = new() { Header = "Capture shortcut", Text = "Ctrl+Shift+4", MaxLength = 32 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _learn = new() { Content = "Set key", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _apply;
    private bool _editingEnabled = true;
    private ShortcutKeyCapture? _keyCapture;
    private readonly nint _window;
    internal event Action? CaptureRequested;
    internal event Action? HideRequested;
    internal event Action? QuitRequested;
    internal event Action<string>? ShortcutRequested;
    internal event Action<bool>? KeyCaptureChanged;
    internal bool IsLearningShortcut => _keyCapture is not null;

    internal SettingsWindow()
    {
        Title = "Settings — Ovi Screenshot";
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _window = window;
        double scale = Win32.GetDpiForWindow(window) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int margin = (int)Math.Round(24 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)Math.Round(380 * scale), Math.Max(1, workArea.Width - margin)),
            Math.Min((int)Math.Round(300 * scale), Math.Max(1, workArea.Height - margin))));
        AutomationProperties.SetAutomationId(_shortcut, "CaptureShortcut");
        AutomationProperties.SetAutomationId(_status, "SettingsStatus");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 14, Padding = new Thickness(24), MaxWidth = 520 };
        var shortcutRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        shortcutRow.Children.Add(_shortcut);
        shortcutRow.Children.Add(_learn);
        Grid.SetColumn(_learn, 1);
        AutomationProperties.SetAutomationId(_learn, "LearnCaptureShortcut");
        ToolTipService.SetToolTip(_learn, "Press the shortcut you want to use");
        _learn.Click += (_, _) => StartKeyCapture();
        content.Children.Add(shortcutRow);
        var apply = new Button { Content = "Apply", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        _apply = apply;
        AutomationProperties.SetAutomationId(apply, "ApplyCaptureShortcut");
        apply.Click += (_, _) => { StopKeyCapture(); ShortcutRequested?.Invoke(_shortcut.Text); };
        content.Children.Add(apply); content.Children.Add(_status);
        var actions = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        var capture = new Button { Content = "Capture", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(capture, "SettingsCapture");
        capture.Click += (_, _) => CaptureRequested?.Invoke();
        var hide = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(hide, "CloseSettings");
        hide.Click += (_, _) => HideRequested?.Invoke();
        var quit = new Button { Content = "Quit", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(quit, "SettingsQuit");
        quit.Click += (_, _) => QuitRequested?.Invoke();
        actions.Children.Add(capture); actions.Children.Add(hide); actions.Children.Add(quit);
        Grid.SetColumn(hide, 1); Grid.SetColumn(quit, 2); content.Children.Add(actions);
        var surface = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  Background="{ThemeResource ApplicationPageBackgroundThemeBrush}">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                <TitleBar x:Name="SettingsTitleBar" Title="Settings"/>
                <ScrollViewer x:Name="SettingsBody" Grid.Row="1" HorizontalScrollBarVisibility="Disabled"/>
            </Grid>
            """);
        ((ScrollViewer)surface.FindName("SettingsBody")).Content = content;
        var titleBar = (TitleBar)surface.FindName("SettingsTitleBar");
        AutomationProperties.SetAutomationId(titleBar, "SettingsTitleBar");
        Content = surface;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated || _keyCapture is null) return;
            _keyCapture.OnOwnerDeactivated();
        };
        Closed += (_, _) => StopKeyCapture();
    }

    internal void StartKeyCapture()
    {
        if (!_editingEnabled) return;
        if (_keyCapture is not null) { StopKeyCapture(); SetStatus(""); return; }
        try
        {
            _keyCapture = new ShortcutKeyCapture(_window, DispatcherQueue, shortcut =>
            {
                StopKeyCapture();
                _shortcut.Text = shortcut.Label;
                ShortcutRequested?.Invoke(shortcut.Label);
            }, message => { StopKeyCapture(); SetStatus(message ?? ""); });
            KeyCaptureChanged?.Invoke(true);
            _learn.Content = "Cancel";
            SetStatus("Press your screenshot key…");
        }
        catch (Exception error) { StopKeyCapture(); SetStatus(error.Message); }
    }

    private void StopKeyCapture()
    {
        bool wasLearning = _keyCapture is not null;
        _keyCapture?.Dispose();
        _keyCapture = null;
        _learn.Content = "Set key";
        if (_status.Text == "Press your screenshot key…") SetStatus("");
        if (wasLearning) KeyCaptureChanged?.Invoke(false);
    }

    internal void SetShortcut(string shortcut) => _shortcut.Text = shortcut;
    internal void CancelKeyCapture() => StopKeyCapture();
    internal void SetShortcutEditingEnabled(bool enabled)
    {
        _editingEnabled = enabled;
        _shortcut.IsEnabled = _learn.IsEnabled = _apply.IsEnabled = enabled;
    }
    internal void SetStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
