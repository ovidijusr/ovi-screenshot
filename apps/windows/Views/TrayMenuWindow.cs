using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Ovi.Screenshot.Native;
using Windows.Foundation;
using Windows.Graphics;

namespace Ovi.Screenshot.Views;

/// <summary>A dedicated XAML root for the tray flyout; the ordinary settings window stays hidden.</summary>
internal sealed class TrayMenuWindow : Window, IDisposable
{
    private readonly Grid _anchor = new() { RequestedTheme = ElementTheme.Default, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly MenuFlyout _menu = new() { ShouldConstrainToRootBounds = false, LightDismissOverlayMode = LightDismissOverlayMode.Off };
    private readonly MenuFlyoutItem? _record;
    private readonly nint _window;
    private nint _previousForeground;
    private Action? _pendingAction;
    private bool _disposed, _showRequested, _openQueued;

    internal bool IsOpen => _menu.IsOpen;
    internal event Action<Exception>? Failed;

    internal TrayMenuWindow(Action capture, Action? record, Action reopen, Action settings, Action quit)
    {
        Title = "Ovi Screenshot menu";
        _window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        long extended = Win32.GetWindowLongPtr(_window, Win32.GwlExStyle).ToInt64();
        Win32.SetWindowLongPtr(_window, Win32.GwlExStyle, new nint(extended | Win32.WsExToolWindow));
        AppWindow.Resize(new SizeInt32(1, 1));

        AddItem("Capture area", Symbol.Camera, "TrayCapture", capture);
        if (record is not null) _record = AddItem("Record area", Symbol.Video, "TrayRecord", record);
        AddItem("Last capture", Symbol.Pictures, "TrayLastCapture", reopen);
        _menu.Items.Add(new MenuFlyoutSeparator());
        AddItem("Settings", Symbol.Setting, "TraySettings", settings);
        AddItem("Quit", Symbol.Cancel, "TrayQuit", quit);
        _menu.Closed += (_, _) => FinishSession();
        _anchor.Loaded += (_, _) => QueueOpen();
        Content = _anchor;
        AppWindow.Closing += (_, args) =>
        {
            if (_disposed) return;
            args.Cancel = true;
            Dismiss();
        };
        Closed += (_, _) => _disposed = true;
    }

    internal void ShowAt(Win32.Point screenPoint, bool recording)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_menu.IsOpen) { Dismiss(); return; }
        if (!_showRequested)
        {
            _previousForeground = Win32.GetForegroundWindow();
            _pendingAction = null;
        }
        _showRequested = true;
        if (_record is not null)
        {
            _record.Text = recording ? "Stop recording" : "Record area";
            _record.Icon = new SymbolIcon(recording ? Symbol.Stop : Symbol.Video);
        }
        // This is only an anchor point; the WinUI popup draws outside it and handles display edges.
        AppWindow.MoveAndResize(new RectInt32(screenPoint.X, screenPoint.Y, 1, 1));
        Activate();
        Win32.SetForegroundWindow(_window);
        QueueOpen();
    }

    internal void Dismiss()
    {
        if (_disposed) return;
        _pendingAction = null;
        if (_menu.IsOpen) _menu.Hide();
        else FinishSession();
    }

    private MenuFlyoutItem AddItem(string text, Symbol symbol, string id, Action callback)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(symbol) };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) =>
        {
            _pendingAction = callback;
            // Execute only after Closed has removed the popup and its host from capture output.
            if (_menu.IsOpen) _menu.Hide();
            else FinishSession();
        };
        _menu.Items.Add(item);
        return item;
    }

    private void QueueOpen()
    {
        if (_disposed || !_showRequested || _openQueued || !_anchor.IsLoaded || _anchor.XamlRoot is null) return;
        _openQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _openQueued = false;
            if (_disposed || !_showRequested || _menu.IsOpen) return;
            try
            {
                // Popup presenters do not always inherit the placement target theme across HWNDs.
                // Keep the platform template/materials and pass the resolved app/system theme through.
                var style = new Style(typeof(MenuFlyoutPresenter))
                {
                    BasedOn = (Style)Application.Current.Resources["DefaultMenuFlyoutPresenterStyle"]
                };
                style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, _anchor.ActualTheme));
                _menu.MenuFlyoutPresenterStyle = style;
                _menu.XamlRoot = _anchor.XamlRoot;
                _menu.ShowAt(_anchor, new FlyoutShowOptions
                {
                    Position = new Point(0, 0), Placement = FlyoutPlacementMode.Auto, ShowMode = FlyoutShowMode.Standard
                });
            }
            catch (Exception error)
            {
                FinishSession();
                if (Failed is not null) Failed(error);
                else System.Diagnostics.Trace.WriteLine(error);
            }
        });
    }

    private void FinishSession()
    {
        if (_disposed || !_showRequested) return;
        _showRequested = false;
        Action? callback = _pendingAction;
        _pendingAction = null;
        bool restoreFocus = callback is not null || Win32.GetForegroundWindow() == _window;
        AppWindow.Hide();
        if (restoreFocus && _previousForeground != 0 && _previousForeground != _window)
            Win32.SetForegroundWindow(_previousForeground);
        // Leave the flyout's Closed callback before a command creates another window or quits the app.
        if (callback is not null) DispatcherQueue.TryEnqueue(() => { if (!_disposed) callback(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _showRequested = false;
        _pendingAction = null;
        if (_menu.IsOpen) _menu.Hide();
        Close();
    }
}
