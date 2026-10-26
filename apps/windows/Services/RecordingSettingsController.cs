using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Services;

internal sealed class RecordingSettingsController(SettingsStore store, SettingsWindow window,
    RecordingOptions initial, Action<Exception> log)
{
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _pending = Task.CompletedTask;
    private Task _devices = Task.CompletedTask;
    private bool _stopping;
    internal RecordingOptions Current { get; private set; } = initial;

    internal void Apply(RecordingOptions options)
    {
        if (!_stopping) _pending = SaveAsync(options);
    }

    internal async Task<RecordingOptions> ForRecordingAsync(CancellationToken cancellation)
    {
        await _pending.WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return Current;
    }

    private async Task SaveAsync(RecordingOptions options)
    {
        await _updates.WaitAsync();
        try
        {
            if (_stopping) return;
            window.SetRecordingUpdatePending(true);
            await store.SaveRecordingAsync(options);
            Current = options;
            window.SetRecordingStatus("Saved for the next recording.");
        }
        catch (Exception error)
        {
            log(error);
            window.SetRecordingStatus("Could not save recording settings. Try again.", isError: true);
        }
        finally { window.SetRecordingUpdatePending(_stopping); _updates.Release(); }
    }

    internal void RefreshDevices()
    {
        if (!_stopping && _devices.IsCompleted) _devices = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await Task.Run(RecordingAudioDevices.Enumerate, _lifetime.Token);
            if (!_stopping) window.SetAudioDevices(devices);
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception error)
        {
            log(error);
            if (!_stopping) window.SetRecordingStatus("Audio devices are unavailable. Reopen Settings to retry.", isError: true);
        }
    }

    internal async Task StopAsync()
    {
        _stopping = true;
        _lifetime.Cancel();
        window.SetRecordingUpdatePending(true);
        await Task.WhenAll(_pending, _devices);
        _lifetime.Dispose();
    }
}
