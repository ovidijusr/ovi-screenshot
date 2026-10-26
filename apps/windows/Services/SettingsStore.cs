using System.Text.Json;
using Ovi.Screenshot.Native;
using Ovi.Screenshot.Capture;

namespace Ovi.Screenshot.Services;

internal sealed class SettingsStore(string path)
{
    internal const string DefaultShortcut = "Ctrl+Shift+4";
    private readonly SemaphoreSlim _writes = new(1, 1);

    internal SettingsLoadResult Load()
    {
        if (!File.Exists(path)) return new(DefaultShortcut, null);
        try
        {
            var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path));
            string shortcut = CaptureShortcut.Parse(settings?.Shortcut ?? DefaultShortcut).Label;
            var recording = settings?.Recording ?? new RecordingOptions();
            try { recording.Validate(); }
            catch (ArgumentException error)
            {
                return new(shortcut, new InvalidOperationException("Recording settings were invalid. Recording defaults are active.", error));
            }
            return new(shortcut, null) { Recording = recording };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(DefaultShortcut, new InvalidOperationException("Settings could not be read. Defaults are active; check your settings.", error));
        }
    }

    internal Task SaveAsync(string shortcut)
    {
        string label = CaptureShortcut.Parse(shortcut).Label;
        return UpdateAsync(current => current with { Shortcut = label });
    }

    internal Task SaveRecordingAsync(RecordingOptions options)
    {
        options.Validate();
        return UpdateAsync(current => current with { Recording = options });
    }

    private async Task UpdateAsync(Func<SavedSettings, SavedSettings> update)
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await Task.Run(Load).ConfigureAwait(false);
            await AtomicJsonFile.WriteAsync(path, update(new SavedSettings(current.Shortcut, current.Recording))).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private sealed record SavedSettings(string Shortcut, RecordingOptions? Recording = null);
}

internal sealed record SettingsLoadResult(string Shortcut, Exception? Warning)
{
    internal RecordingOptions Recording { get; init; } = new();
}
