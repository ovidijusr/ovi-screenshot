using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Native;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace Ovi.Screenshot.Testing;

internal static class WindowRecordingSmoke
{
    // The caller supplies its own generated four-color HWND, never a desktop source.
    internal static async Task VerifyAsync(Window fixture, nint handle, string marker)
    {
        string path = Path.ChangeExtension(marker, ".window-recording.mp4");
        int width, height;
        await using (var recorder = await ScreenRecorder.StartWindowAsync(Target(), path, new(24), CancellationToken.None))
        {
            width = recorder.Width; height = recorder.Height;
            await Task.Delay(200);
            var position = fixture.AppWindow.Position;
            fixture.AppWindow.Move(new PointInt32(position.X + 20, position.Y + 20));
            await Task.Delay(150);
            Require(!recorder.Completion.IsCompleted, "Moving the source ended window recording.");
            recorder.Pause();
            TimeSpan paused = recorder.Elapsed;
            await Task.Delay(100);
            Require(recorder.Elapsed == paused, "Window recording clock advanced while paused.");
            recorder.Resume();
            await Task.Delay(150);
            await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await recorder.Completion;
        }
        await VerifyFileAsync(path, width, height, 24, decode: true);

        string resizedPath = Path.ChangeExtension(marker, ".window-resize.mp4");
        await using (var recorder = await ScreenRecorder.StartWindowAsync(Target(), resizedPath, new(15), CancellationToken.None))
        {
            width = recorder.Width; height = recorder.Height;
            await Task.Delay(250);
            var size = fixture.AppWindow.Size;
            fixture.AppWindow.Resize(new SizeInt32(size.Width + 16, size.Height + 8));
            bool ended = false;
            try { await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(5)); ended = true; }
            catch (InvalidOperationException) { ended = true; }
            Require(ended, "A resized source did not end its fixed-size recording.");
            try { await recorder.StopAsync(); }
            catch (InvalidOperationException) { /* A playable clip is still required below. */ }
        }
        await VerifyFileAsync(resizedPath, width, height, 15, decode: false);

        WindowCaptureTarget Target()
        {
            if (!WindowTargets.TryGetBounds(handle, out var bounds))
                throw new InvalidOperationException("The generated recording window is unavailable.");
            return new(handle, bounds, "Generated recording fixture") { ProcessId = (uint)Environment.ProcessId };
        }
    }

    private static async Task VerifyFileAsync(string path, int width, int height, uint fps, bool decode)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        var video = clip.GetVideoEncodingProperties();
        double actualRate = video.FrameRate.Numerator / (double)Math.Max(1u, video.FrameRate.Denominator);
        Require(video.Width == width && video.Height == height && video.FrameRate.Denominator > 0
            && actualRate > 0 && actualRate <= fps * 1.25 && clip.OriginalDuration > TimeSpan.Zero,
            $"Window MP4 is invalid: {video.Width}x{video.Height}, {actualRate:F2} fps, {clip.OriginalDuration}.");
        if (!decode) return;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromMilliseconds(100), width, height, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        Require(Color(width / 4, height / 4, 240, 20, 20) && Color(width * 3 / 4, height / 4, 20, 240, 20)
            && Color(width / 4, height * 3 / 4, 20, 20, 240) && Color(width * 3 / 4, height * 3 / 4, 240, 240, 20),
            "Window recording changed generated pixels or orientation.");
        bool Color(int x, int y, int red, int green, int blue)
        {
            int offset = (y * bitmap.PixelWidth + x) * 4;
            return Math.Abs(pixels[offset] - blue) < 55 && Math.Abs(pixels[offset + 1] - green) < 55
                && Math.Abs(pixels[offset + 2] - red) < 55;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
