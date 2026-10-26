using Microsoft.UI.Xaml;
using Ovi.Screenshot.Interop;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Services;
using Ovi.Screenshot.Testing;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot;

public sealed partial class ScreenshotApp : Application
{
    private readonly string[] _arguments;
    private readonly AppPaths _paths = new();
    private readonly AppLog _log;
    private readonly PreviewStack _previews = new();
    private SettingsWindow? _settings;
    private DesktopIntegration? _desktop;
    private CaptureCoordinator? _captures;
    private RecordingController? _recordings;
    private ShortcutController? _shortcuts;
    private RecordingSettingsController? _recordingSettings;
    private bool _quitting;

    internal ScreenshotApp(string[] arguments)
    {
        _arguments = arguments;
        _log = AppLog.Shared;
        UnhandledException += (_, args) => _log.Write(args.Exception);
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _settings = new SettingsWindow();
        _settings.QuitRequested += Quit;
        _settings.HideRequested += () => _settings.AppWindow.Hide();
        _settings.AppWindow.Closing += (_, closing) =>
        {
            if (_quitting) return;
            closing.Cancel = true;
            _settings.AppWindow.Hide();
        };

        int smoke = Array.IndexOf(_arguments, "--ui-smoke");
        if (smoke >= 0)
        {
            await RunSmokeAsync(smoke);
            return;
        }

        try
        {
            CoreNative.VerifyAbi();
            var store = new SettingsStore(_paths.Settings);
            var loaded = store.Load();
            _settings.SetShortcut(loaded.Shortcut);
            _settings.SetRecordingOptions(loaded.Recording);
            _recordingSettings = new RecordingSettingsController(store, _settings, loaded.Recording, _log.Write);
            _settings.RecordingRequested += _recordingSettings.Apply;
            var storage = new CaptureStorage();
            _recordings = new RecordingController(_settings.DispatcherQueue, new RecordingLibrary(_paths, _log), _log, Report,
                new UserDialogs(_settings).ConfirmDiscardRecordingAsync, storage: storage);
            _captures = new CaptureCoordinator(_settings, _previews, _recordings, Report,
                _recordingSettings.ForRecordingAsync, _recordingSettings.Apply, storage);
            _recordings.CaptureRequested += _captures.RecordArea;
            _recordings.RecordingSaved = _captures.ShowSavedRecordingAsync;
            _previews.Failed += _log.Write;
            _previews.Deleted += _captures.Forget;
            _previews.VideoDeleted += _captures.ForgetVideo;
            Task recovery = _recordings.RecoverAsync();
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(_settings);
            _desktop = new DesktopIntegration(handle, _captures.Screenshot, ShowSettings, Quit, _captures.ReopenLast, Report, _recordings.Toggle);
            _recordings.StateChanged += recording => _desktop.SetRecording(recording);
            _shortcuts = new ShortcutController(_desktop, _settings, store, loaded.Shortcut, Report);
            _settings.CaptureRequested += _captures.Screenshot;
            _settings.ShortcutRequested += _shortcuts.Apply;
            _settings.KeyCaptureChanged += _shortcuts.SetLearning;
            try { _desktop.Register(CaptureShortcut.Parse(loaded.Shortcut)); }
            catch (Exception error) { Report(error); }
            await recovery;
            if (_quitting) return;
            if (loaded.Warning is not null) Report(loaded.Warning);
            if (_arguments.Contains("--settings") || _arguments.Contains("--bind-key")) ShowSettings();
            if (_arguments.Contains("--bind-key")) _settings.DispatcherQueue.TryEnqueue(_settings.StartKeyCapture);
        }
        catch (Exception error) { Report(error); }
    }

    private async Task RunSmokeAsync(int argument)
    {
        try
        {
            if (argument + 1 >= _arguments.Length) throw new ArgumentException("Supply a smoke-test marker path.");
            CoreNative.VerifyAbi();
            _settings!.SetShortcut(SettingsStore.DefaultShortcut);
            _settings.Activate();
            string marker = Path.GetFullPath(_arguments[argument + 1]);
            string result = await new NativeUiSmoke(_settings).RunAsync(marker);
            await File.WriteAllTextAsync(marker, result);
        }
        catch (Exception error)
        {
            _log.Write(error);
            Environment.ExitCode = 1;
        }
        finally
        {
            _quitting = true;
            _settings?.Close();
            Exit();
        }
    }

    private void ShowSettings()
    {
        if (!_quitting)
        {
            _settings?.Activate();
            _recordingSettings?.RefreshDevices();
        }
    }

    private void Report(Exception error)
    {
        _log.Write(error);
        if (_quitting) return;
        _settings?.SetStatus(error.Message);
        ShowSettings();
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        await StopSafelyAsync(() => _captures?.StopAsync() ?? Task.CompletedTask);
        await Task.WhenAll(
            StopSafelyAsync(() => _recordings?.ShutdownAsync() ?? Task.CompletedTask),
            StopSafelyAsync(_previews.ShutdownAsync),
            StopSafelyAsync(() => _shortcuts?.StopAsync() ?? Task.CompletedTask),
            StopSafelyAsync(() => _recordingSettings?.StopAsync() ?? Task.CompletedTask));
        try { _desktop?.Dispose(); }
        catch (Exception error) { _log.Write(error); }
        finally { _settings?.Close(); Exit(); }
    }

    private async Task StopSafelyAsync(Func<Task> stop)
    {
        try { await stop(); }
        catch (Exception error) { _log.Write(error); }
    }
}
