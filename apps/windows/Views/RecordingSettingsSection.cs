using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Ovi.Screenshot.Capture;

namespace Ovi.Screenshot.Views;

internal sealed class RecordingSettingsSection : StackPanel
{
    private readonly ComboBox _frameRate = new() { Header = "Frame rate", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _systemAudio = new() { Header = "System sound" };
    private readonly ToggleSwitch _microphone = new() { Header = "Microphone" };
    private readonly ComboBox _outputDevice = new() { Header = "Output device", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _microphoneDevice = new() { Header = "Microphone device", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _apply = new() { Content = "Apply recording" };
    private readonly InfoBar _status = new() { IsOpen = false, IsClosable = true };
    private AudioDeviceCatalog _devices = new([], []);
    private string? _outputId, _microphoneId;
    private bool _updating, _pending;

    internal event Action<RecordingOptions>? ApplyRequested;

    internal RecordingSettingsSection()
    {
        Spacing = 12;
        Children.Add(new TextBlock { Text = "Recording", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        Children.Add((TextBlock)XamlReader.Load("""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Text="Applies to the next recording." TextWrapping="Wrap" Style="{StaticResource CaptionTextBlockStyle}"
                Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
            """));
        Identify(_frameRate, "RecordingFrameRate", "Recording frame rate");
        Identify(_systemAudio, "RecordSystemAudio", "Record system sound");
        Identify(_microphone, "RecordMicrophone", "Record microphone");
        Identify(_outputDevice, "RecordingOutputDevice", "System sound output device");
        Identify(_microphoneDevice, "RecordingMicrophoneDevice", "Recording microphone device");
        Identify(_apply, "ApplyRecordingSettings", "Apply recording settings");
        Identify(_status, "RecordingSettingsStatus", "Recording settings status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        foreach (int fps in new[] { 15, 24, 30, 60 })
            _frameRate.Items.Add(new ComboBoxItem { Content = $"{fps} fps", Tag = fps });
        _systemAudio.Toggled += (_, _) => UpdateAvailability();
        _microphone.Toggled += (_, _) => UpdateAvailability();
        _outputDevice.SelectionChanged += (_, _) =>
        {
            if (!_updating && _outputDevice.SelectedItem is ComboBoxItem { Tag: AudioDeviceChoice choice }) _outputId = choice.Id;
        };
        _microphoneDevice.SelectionChanged += (_, _) =>
        {
            if (!_updating && _microphoneDevice.SelectedItem is ComboBoxItem { Tag: AudioDeviceChoice choice }) _microphoneId = choice.Id;
        };
        _apply.Click += (_, _) => RequestApply();
        Children.Add(_frameRate);
        Children.Add(_systemAudio); Children.Add(_outputDevice);
        Children.Add(_microphone); Children.Add(_microphoneDevice);
        Children.Add(_apply); Children.Add(_status);
        SetOptions(new RecordingOptions());
    }

    internal void SetOptions(RecordingOptions options)
    {
        options.Validate();
        _frameRate.SelectedItem = _frameRate.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == options.FramesPerSecond);
        _systemAudio.IsOn = options.SystemAudio;
        _microphone.IsOn = options.Microphone;
        _outputId = options.OutputDeviceId; _microphoneId = options.MicrophoneDeviceId;
        RefreshDevices();
        UpdateAvailability();
    }

    internal void SetDevices(AudioDeviceCatalog devices)
    {
        _devices = devices;
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        _updating = true;
        try
        {
            PopulateDevices(_outputDevice, _devices.Outputs, _outputId);
            PopulateDevices(_microphoneDevice, _devices.Microphones, _microphoneId);
        }
        finally { _updating = false; }
    }

    private static void PopulateDevices(ComboBox combo, IReadOnlyList<AudioDeviceChoice> devices, string? selectedId)
    {
        combo.Items.Clear();
        var choices = new List<AudioDeviceChoice> { devices.FirstOrDefault(device => device.Id is null) ?? new(null, "Default device") };
        choices.AddRange(devices.Where(device => device.Id is not null).DistinctBy(device => device.Id!, StringComparer.Ordinal));
        // Keep the saved route explicit; never silently switch a disconnected device to the default.
        if (selectedId is not null && choices.All(device => device.Id != selectedId))
            choices.Add(new(selectedId, "Unavailable device"));
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice.Name, Tag = choice };
            combo.Items.Add(item);
            if (choice.Id == selectedId) combo.SelectedItem = item;
        }
    }

    internal void SetPending(bool pending)
    {
        _pending = pending;
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        _frameRate.IsEnabled = _systemAudio.IsEnabled = _microphone.IsEnabled = _apply.IsEnabled = !_pending;
        _outputDevice.Visibility = _systemAudio.IsOn ? Visibility.Visible : Visibility.Collapsed;
        _microphoneDevice.Visibility = _microphone.IsOn ? Visibility.Visible : Visibility.Collapsed;
        _outputDevice.IsEnabled = !_pending && _systemAudio.IsOn;
        _microphoneDevice.IsEnabled = !_pending && _microphone.IsOn;
    }

    internal void SetStatus(string message, bool isError)
    {
        _status.Message = message;
        _status.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        _status.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private RecordingOptions ReadOptions() => new((int)((ComboBoxItem)_frameRate.SelectedItem).Tag,
        _systemAudio.IsOn, _microphone.IsOn, _outputId, _microphoneId);

    private void RequestApply()
    {
        if (_pending) return;
        SetStatus("", false);
        ApplyRequested?.Invoke(ReadOptions());
    }

    private static void Identify(DependencyObject control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }

    // Exercise the real draft controls and Apply route without input injection or hardware metadata.
    internal static void VerifySynthetic()
    {
        var section = new RecordingSettingsSection();
        int requests = 0;
        RecordingOptions? requested = null;
        section.ApplyRequested += value => { requests++; requested = value; };
        Require(section.ReadOptions() == new RecordingOptions(), "defaults are 30 fps with audio off");
        Require(section._outputDevice.Visibility == Visibility.Collapsed && section._microphoneDevice.Visibility == Visibility.Collapsed,
            "audio device fields stay hidden while disabled");
        section.SetDevices(new([new(null, "Default device"), new("fixture-output", "Demo speakers")],
            [new(null, "Default device"), new("fixture-mic", "Demo microphone")]));
        var saved = new RecordingOptions(24, true, true, "fixture-output", "fixture-mic");
        section.SetOptions(saved);
        Require(section.ReadOptions() == saved && section._outputDevice.IsEnabled && section._microphoneDevice.IsEnabled,
            "saved options select both audio routes");
        section._frameRate.SelectedIndex = 3;
        section._microphone.IsOn = false;
        Require(requests == 0 && section.ReadOptions().FramesPerSecond == 60 && section.ReadOptions().MicrophoneDeviceId == "fixture-mic",
            "draft changes do not save or erase the disabled microphone route");
        section.SetPending(true); section.RequestApply();
        Require(requests == 0 && !section._frameRate.IsEnabled && !section._apply.IsEnabled && !section._outputDevice.IsEnabled,
            "pending update blocks duplicate Apply and draft edits");
        section.SetPending(false); section.RequestApply();
        Require(requests == 1 && requested == (saved with { FramesPerSecond = 60, Microphone = false }),
            "explicit Apply dispatches exactly the edited options");
        section.SetStatus("Could not save recording settings.", true);
        section.SetOptions(saved);
        Require(section.ReadOptions() == saved && section._status.IsOpen && section._status.Severity == InfoBarSeverity.Error,
            "saved rollback restores controls without hiding the failure");
        section.SetDevices(new([], []));
        Require(section.ReadOptions() == saved && section._outputDevice.SelectedItem is ComboBoxItem { Content: "Unavailable device" },
            "device refresh preserves an unavailable saved route");
        section._outputDevice.SelectedIndex = 0;
        section.RequestApply();
        Require(requests == 2 && requested?.OutputDeviceId is null && requested?.MicrophoneDeviceId == "fixture-mic",
            "only an explicit device choice switches back to the default");
        static void Require(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException($"Recording settings regression: {scenario}.");
        }
    }
}
