using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Ovi.Screenshot.Native;
using Windows.Graphics;
using Windows.System;

namespace Ovi.Screenshot.Views;

internal sealed class RecordingCountdownWindow : Window
{
    private readonly TextBlock _number;
    private readonly Button _cancel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started, _closed, _finished;

    internal int CurrentNumber { get; private set; } = 3;
    internal bool IsClosed => _closed;

    internal RecordingCountdownWindow(Win32.Rect selectionBounds, Win32.Rect workArea)
    {
        Title = "Recording countdown";
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
        var content = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                  CornerRadius="{ThemeResource OverlayCornerRadius}">
                <TextBlock x:Name="Number" Text="3" FontSize="56" FontWeight="SemiBold" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{ThemeResource TextFillColorPrimaryBrush}"/>
                <Button x:Name="Cancel" Style="{StaticResource SubtleButtonStyle}" Width="28" Height="28" Padding="0" Margin="5" CornerRadius="6" HorizontalAlignment="Right" VerticalAlignment="Top"><FontIcon Glyph="&#xE8BB;" FontSize="12"/></Button>
            </Grid>
            """);
        _number = (TextBlock)content.FindName("Number");
        _cancel = (Button)content.FindName("Cancel");
        AutomationProperties.SetAutomationId(_number, "RecordingCountdownNumber");
        AutomationProperties.SetName(_number, "Recording starts in 3");
        AutomationProperties.SetLiveSetting(_number, AutomationLiveSetting.Assertive);
        AutomationProperties.SetAutomationId(_cancel, "CancelRecordingCountdown");
        AutomationProperties.SetName(_cancel, "Cancel recording countdown");
        ToolTipService.SetToolTip(_cancel, "Cancel (Esc)");
        _cancel.Click += (_, _) => Cancel();
        content.PreviewKeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { args.Handled = true; Cancel(); } };
        content.Loaded += (_, _) => { _loaded.TrySetResult(); _cancel.Focus(FocusState.Programmatic); };
        Content = content;

        int centerX = (int)(((long)selectionBounds.Left + selectionBounds.Right) / 2);
        int centerY = (int)(((long)selectionBounds.Top + selectionBounds.Bottom) / 2);
        AppWindow.Move(new PointInt32(centerX, centerY));
        double scale = Win32.GetDpiForWindow(window) / 96d;
        int margin = (int)Math.Round(12 * scale);
        int width = Math.Min((int)Math.Round(112 * scale), Math.Max(1, workArea.Width - margin * 2));
        int height = Math.Min((int)Math.Round(112 * scale), Math.Max(1, workArea.Height - margin * 2));
        int x = Math.Clamp(centerX - width / 2, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - width - margin));
        int y = Math.Clamp(centerY - height / 2, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - height - margin));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        Closed += (_, _) => { _closed = true; if (!_finished) _cancellation.Cancel(); };
    }

    internal async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("The countdown can only run once.");
        _started = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            Activate();
            await _loaded.Task.WaitAsync(linked.Token);
            var clock = Stopwatch.StartNew();
            for (int remaining = 3; remaining >= 1; remaining--)
            {
                CurrentNumber = remaining;
                _number.Text = remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AutomationProperties.SetName(_number, $"Recording starts in {remaining}");
                FrameworkElementAutomationPeer.FromElement(_number)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                TimeSpan delay = TimeSpan.FromSeconds(4 - remaining) - clock.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            _finished = true;
            if (!_closed) Close();
            _cancellation.Dispose();
        }
    }

    internal void Cancel()
    {
        if (!_closed && !_finished) _cancellation.Cancel();
    }
}
