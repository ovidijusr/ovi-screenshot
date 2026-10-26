using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Views;

internal sealed class RecordingControlWindow : Window
{
    private readonly TextBlock _elapsed;
    private readonly TextBlock _status;
    private readonly Button _pause;
    private readonly Button _action;
    private readonly Button _dismiss;
    private readonly Button _discard;
    private readonly FrameworkElement _recordingDot;
    private readonly FrameworkElement _pausedDot;
    private TimeSpan _elapsedValue;
    private bool _paused, _finalizing, _ready, _allowClose, _closed;

    internal event Action? StopRequested;
    internal event Action? SaveRequested;
    internal event Action? PauseRequested;
    internal event Action? DiscardRequested;
    internal nint Handle { get; }
    internal bool IsPaused => _paused;
    internal bool IsFinalizing => _finalizing;
    internal bool IsReady => _ready;
    internal string ElapsedText => _elapsed.Text;

    internal RecordingControlWindow(Win32.Rect workArea)
    {
        Title = "Recording";
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        long extended = Win32.GetWindowLongPtr(Handle, Win32.GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(Handle, Win32.GwlExStyle, new nint(extended | Win32.WsExToolWindow));
        Win32.SetWindowDisplayAffinity(Handle, 0x11);

        var content = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                    CornerRadius="{ThemeResource OverlayCornerRadius}">
                <Grid Padding="10,8" ColumnSpacing="4">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <Grid x:Name="ClockStrip" Background="Transparent" Margin="2,0,8,0">
                        <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                        <Grid VerticalAlignment="Center" Margin="0,0,9,0">
                            <Ellipse x:Name="RecordingDot" Width="8" Height="8" Fill="{ThemeResource SystemFillColorCriticalBrush}"/>
                            <Ellipse x:Name="PausedDot" Width="8" Height="8" Fill="{ThemeResource SystemFillColorCautionBrush}" Visibility="Collapsed"/>
                        </Grid>
                        <StackPanel Grid.Column="1" VerticalAlignment="Center">
                            <TextBlock x:Name="Elapsed" Text="00:00" FontSize="16" FontWeight="SemiBold" Foreground="{ThemeResource TextFillColorPrimaryBrush}"/>
                            <TextBlock x:Name="Status" Visibility="Collapsed" FontSize="12" TextTrimming="CharacterEllipsis" MaxWidth="100" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                        </StackPanel>
                    </Grid>
                    <Button x:Name="Pause" Grid.Column="1" Style="{StaticResource SubtleButtonStyle}" Width="36" Height="36" Padding="0" CornerRadius="8" VerticalAlignment="Center"><FontIcon Glyph="&#xE769;" FontSize="15"/></Button>
                    <Button x:Name="Action" Grid.Column="2" Style="{StaticResource SubtleButtonStyle}" Width="36" Height="36" Padding="0" CornerRadius="8" VerticalAlignment="Center"><FontIcon Glyph="&#xE71A;" FontSize="15"/></Button>
                    <Button x:Name="Discard" Grid.Column="3" Style="{StaticResource SubtleButtonStyle}" Width="36" Height="36" Padding="0" CornerRadius="8" VerticalAlignment="Center" Visibility="Collapsed"><FontIcon Glyph="&#xE74D;" FontSize="15"/></Button>
                    <Button x:Name="Dismiss" Grid.Column="4" Style="{StaticResource SubtleButtonStyle}" Width="36" Height="36" Padding="0" CornerRadius="8" VerticalAlignment="Center" Visibility="Collapsed"><FontIcon Glyph="&#xE8BB;" FontSize="13"/></Button>
                </Grid>
            </Border>
            """);
        _elapsed = (TextBlock)content.FindName("Elapsed");
        _status = (TextBlock)content.FindName("Status");
        _pause = (Button)content.FindName("Pause");
        _action = (Button)content.FindName("Action");
        _dismiss = (Button)content.FindName("Dismiss");
        _discard = (Button)content.FindName("Discard");
        _recordingDot = (FrameworkElement)content.FindName("RecordingDot");
        _pausedDot = (FrameworkElement)content.FindName("PausedDot");
        AutomationProperties.SetAutomationId(_elapsed, "RecordingElapsed");
        AutomationProperties.SetAutomationId(_pause, "PauseRecording");
        AutomationProperties.SetAutomationId(_dismiss, "DismissRecording");
        AutomationProperties.SetName(_dismiss, "Dismiss recording");
        AutomationProperties.SetAutomationId(_status, "RecordingStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        ToolTipService.SetToolTip(_dismiss, "Dismiss");
        AutomationProperties.SetAutomationId(_discard, "DiscardRecording");
        AutomationProperties.SetName(_discard, "Discard recording");
        ToolTipService.SetToolTip(_discard, "Discard recording");
        _discard.Click += (_, _) => DiscardRequested?.Invoke();
        _pause.Click += (_, _) => { if (!_ready && !_finalizing) PauseRequested?.Invoke(); };
        _action.Click += (_, _) => { if (_ready) SaveRequested?.Invoke(); else if (!_finalizing) StopRequested?.Invoke(); };
        _dismiss.Click += (_, _) => CloseSafely();
        Content = content;
        // Only the noninteractive clock strip is draggable; icon buttons remain ordinary controls.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar((FrameworkElement)content.FindName("ClockStrip"));
        presenter.SetBorderAndTitleBar(true, false);
        Win32.TryRoundWindow(Handle);
        SetPaused(false);
        SetAction(false);

        AppWindow.Move(new Windows.Graphics.PointInt32(workArea.Left, workArea.Top));
        double scale = Win32.GetDpiForWindow(Handle) / 96d;
        int margin = (int)Math.Round(20 * scale);
        int width = Math.Min((int)Math.Round(220 * scale), Math.Max(1, workArea.Width - margin * 2));
        int height = Math.Min((int)Math.Round(64 * scale), Math.Max(1, workArea.Height - margin * 2));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            Math.Max(workArea.Left, workArea.Right - width - margin),
            Math.Max(workArea.Top, workArea.Bottom - height - margin), width, height));
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose || _ready) return;
            args.Cancel = true;
            if (!_finalizing) StopRequested?.Invoke();
        };
        Closed += (_, _) => _closed = true;
    }

    internal void ShowWithoutActivation() { if (!_closed) AppWindow.Show(false); }

    internal void SetElapsed(TimeSpan elapsed)
    {
        if (_closed) return;
        _elapsedValue = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        _elapsed.Text = _elapsedValue.TotalHours >= 1
            ? $"{(long)_elapsedValue.TotalHours}:{_elapsedValue.Minutes:00}:{_elapsedValue.Seconds:00}"
            : $"{_elapsedValue.Minutes:00}:{_elapsedValue.Seconds:00}";
    }

    internal void SetPaused(bool paused)
    {
        if (_closed || _ready || _finalizing) return;
        _paused = paused;
        _pause.Content = new FontIcon { Glyph = paused ? "\uE768" : "\uE769", FontSize = 15 };
        AutomationProperties.SetName(_pause, paused ? "Resume recording" : "Pause recording");
        ToolTipService.SetToolTip(_pause, paused ? "Resume" : "Pause");
        _recordingDot.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        _pausedDot.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(paused ? "Paused" : null);
    }

    internal void MarkFinalizing()
    {
        if (_closed) return;
        _finalizing = true;
        _paused = false;
        _recordingDot.Visibility = _pausedDot.Visibility = Visibility.Collapsed;
        _pause.IsEnabled = _action.IsEnabled = false;
        SetStatus("Finishing…");
    }

    internal void MarkReady(bool recovered = false)
    {
        if (_closed) return;
        _ready = true; _paused = _finalizing = false;
        _pause.Visibility = Visibility.Collapsed;
        _recordingDot.Visibility = _pausedDot.Visibility = Visibility.Collapsed;
        _dismiss.Visibility = Visibility.Visible;
        _discard.Visibility = Visibility.Visible;
        _discard.IsEnabled = true;
        _action.IsEnabled = true;
        SetAction(true);
        SetStatus(recovered ? "Recovered" : null);
    }

    internal void ShowFailure(string message)
    {
        if (_closed) return;
        MarkReady();
        _action.IsEnabled = false;
        _discard.IsEnabled = false;
        SetStatus(message);
    }

    internal void ShowSaveFailure()
        => ShowFileActionFailure("Save failed");

    internal void ShowFileActionFailure(string message)
    {
        if (_closed) return;
        MarkReady();
        SetStatus(message);
    }

    private void SetAction(bool save)
    {
        _action.Content = new FontIcon { Glyph = save ? "\uE74E" : "\uE71A", FontSize = 15 };
        AutomationProperties.SetAutomationId(_action, save ? "SaveRecording" : "StopRecording");
        AutomationProperties.SetName(_action, save ? "Save recording" : "Stop recording");
        ToolTipService.SetToolTip(_action, save ? "Save" : "Stop");
    }

    private void SetStatus(string? message)
    {
        _status.Text = message ?? "";
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(_status, message);
        FrameworkElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal void VerifySyntheticStates()
    {
        bool paused = _paused, ready = _ready, finalizing = _finalizing, enabled = _action.IsEnabled;
        string previousStatus = _status.Text;
        TimeSpan elapsed = _elapsedValue;
        try
        {
            _ready = _finalizing = false;
            _pause.Visibility = Visibility.Visible;
            _pause.IsEnabled = true;
            SetElapsed(TimeSpan.FromSeconds(65)); SetPaused(true);
            Require(IsPaused && ElapsedText == "01:05" && AutomationProperties.GetName(_pause) == "Resume recording", "paused clock and resume action");
            SetPaused(false);
            Require(!IsPaused && ElapsedText == "01:05", "resume does not advance the backend clock");
            MarkFinalizing();
            Require(IsFinalizing && !_pause.IsEnabled && !_action.IsEnabled, "finalizing disables actions");
            MarkReady();
            Require(IsReady && !IsFinalizing && _action.IsEnabled && _pause.Visibility == Visibility.Collapsed, "ready exposes save");
            ShowSaveFailure();
            Require(_action.IsEnabled && _status.Text == "Save failed", "save failure remains retryable");
            ShowFailure("Recording failed");
            Require(!_action.IsEnabled && _dismiss.Visibility == Visibility.Visible, "recording failure remains dismissible");
        }
        finally
        {
            _ready = _finalizing = _paused = false;
            _pause.Visibility = Visibility.Visible; _pause.IsEnabled = true;
            _dismiss.Visibility = Visibility.Collapsed; _action.IsEnabled = true;
            _discard.Visibility = Visibility.Collapsed;
            SetAction(false); SetElapsed(elapsed); SetPaused(paused);
            if (finalizing) MarkFinalizing();
            if (ready) MarkReady();
            _action.IsEnabled = enabled;
            SetStatus(previousStatus);
        }
        static void Require(bool condition, string state)
        {
            if (!condition) throw new InvalidOperationException($"Recording control regression: {state}.");
        }
    }

    internal void CloseSafely()
    {
        if (_closed) return;
        _allowClose = true;
        Close();
    }
}
