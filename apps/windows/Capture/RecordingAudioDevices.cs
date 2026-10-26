using NAudio.CoreAudioApi;

namespace Ovi.Screenshot.Capture;

internal static class RecordingAudioDevices
{
    // Endpoint metadata only: enumeration does not create an audio client.
    internal static AudioDeviceCatalog Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        return new(Read(enumerator, DataFlow.Render), Read(enumerator, DataFlow.Capture));
    }

    private static IReadOnlyList<AudioDeviceChoice> Read(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var choices = new List<AudioDeviceChoice> { new(null, "System default") };
        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (MMDevice device in devices)
        {
            using (device) choices.Add(new(device.ID, device.FriendlyName));
        }
        return choices;
    }
}
