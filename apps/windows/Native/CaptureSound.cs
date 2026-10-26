using System.Runtime.InteropServices;
using Ovi.Screenshot.Services;

namespace Ovi.Screenshot.Native;

internal static partial class CaptureSound
{
    private const uint Async = 0x0001, NoDefault = 0x0002, NoStop = 0x0010, FileName = 0x00020000;
    internal static string AssetPath => Path.Combine(AppContext.BaseDirectory, "Assets", "capture.wav");

    internal static void Play() => _ = Task.Run(() =>
    {
        try
        {
            if (!PlaySound(AssetPath, 0, Async | NoDefault | NoStop | FileName))
                AppLog.Shared.Write("Capture sound could not be played.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Shared.Write(error);
        }
    });

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string fileName, nint module, uint flags);
}
