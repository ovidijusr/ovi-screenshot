using System.Text.Json;
using Ovi.Screenshot.Native;

namespace Ovi.Screenshot.Services;

internal sealed class SettingsStore(string path)
{
    internal const string DefaultShortcut = "Ctrl+Shift+4";

    internal SettingsLoadResult Load()
    {
        if (!File.Exists(path)) return new(DefaultShortcut, null);
        try
        {
            var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path));
            string shortcut = CaptureShortcut.Parse(settings?.Shortcut ?? DefaultShortcut).Label;
            return new(shortcut, null);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(DefaultShortcut, new InvalidOperationException("Settings could not be read. The default shortcut is active; set your key again.", error));
        }
    }

    internal Task SaveAsync(string shortcut) => AtomicJsonFile.WriteAsync(path, new SavedSettings(shortcut));
    private sealed record SavedSettings(string Shortcut);
}

internal sealed record SettingsLoadResult(string Shortcut, Exception? Warning);
