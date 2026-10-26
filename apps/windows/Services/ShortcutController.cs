using Ovi.Screenshot.Native;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Services;

internal sealed class ShortcutController(DesktopIntegration desktop, SettingsWindow window, SettingsStore store, string initialShortcut, Action<Exception> report)
{
    private readonly SemaphoreSlim _updates = new(1, 1);
    private Task _pending = Task.CompletedTask;
    private string _current = initialShortcut;
    private bool _stopping;

    internal void Apply(string shortcut)
    {
        if (!_stopping) _pending = ApplyAsync(shortcut);
    }

    private async Task ApplyAsync(string text)
    {
        await _updates.WaitAsync();
        string previous = _current;
        bool installed = false;
        try
        {
            if (_stopping) return;
            window.CancelKeyCapture();
            window.SetShortcutEditingEnabled(false);
            var shortcut = CaptureShortcut.Parse(text);
            desktop.Register(shortcut);
            installed = true;
            await store.SaveAsync(shortcut.Label);
            _current = shortcut.Label;
            window.SetShortcut(_current);
            window.SetStatus("Shortcut saved.");
        }
        catch (Exception error)
        {
            if (installed)
            {
                try { desktop.Register(CaptureShortcut.Parse(previous)); }
                catch (Exception rollback) { report(rollback); }
            }
            window.SetShortcut(_current);
            report(error);
        }
        finally { window.SetShortcutEditingEnabled(!_stopping); _updates.Release(); }
    }

    internal void SetLearning(bool learning)
    {
        if (_stopping) return;
        try { desktop.PauseShortcut(learning); }
        catch (Exception error) { report(error); }
    }

    internal async Task StopAsync()
    {
        _stopping = true;
        window.SetShortcutEditingEnabled(false);
        window.CancelKeyCapture();
        await _pending;
    }
}
