namespace Ovi.Screenshot.Capture;

internal sealed record RecordingOptions(int FramesPerSecond = 30, bool SystemAudio = false,
    bool Microphone = false, string? OutputDeviceId = null, string? MicrophoneDeviceId = null)
{
    internal bool HasAudio => SystemAudio || Microphone;
    internal TimeSpan FrameInterval => TimeSpan.FromTicks(TimeSpan.TicksPerSecond / FramesPerSecond);

    internal void Validate()
    {
        if (FramesPerSecond is not (15 or 24 or 30 or 60))
            throw new ArgumentException("Choose 15, 24, 30 or 60 fps.");
        if (OutputDeviceId?.Length > 2048 || MicrophoneDeviceId?.Length > 2048)
            throw new ArgumentException("The selected audio device is invalid.");
    }
}

internal readonly record struct RecordingClockState(TimeSpan Elapsed, bool IsRunning, bool IsPaused, bool IsStopped,
    long PauseGeneration = 0, TimeSpan LastPauseElapsed = default);

internal sealed record PcmAudioFrame(byte[] Pcm, TimeSpan Timestamp, TimeSpan Duration)
{
    internal const uint SampleRate = 48_000;
    internal const uint BitsPerSample = 16;
    internal const uint Channels = 2;
    internal const int FramesPerBlock = 960;
    internal const int BytesPerFrame = 4;
    internal const int BytesPerBlock = FramesPerBlock * BytesPerFrame;
    internal static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(20);
}

internal sealed record AudioDeviceChoice(string? Id, string Name);
internal sealed record AudioDeviceCatalog(IReadOnlyList<AudioDeviceChoice> Outputs, IReadOnlyList<AudioDeviceChoice> Microphones);
