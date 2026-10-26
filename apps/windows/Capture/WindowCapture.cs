using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Ovi.Screenshot.Native;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Ovi.Screenshot.Capture;

internal static class WindowCapture
{
    internal static async Task<CapturedDisplay> AcquireAsync(WindowCaptureTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Window capture is unavailable in this Windows session.");
        if (!WindowTargets.TryGetBounds(target.Handle, out var initialBounds))
            throw new InvalidOperationException("That window is no longer available. Select it again.");
        uint process = WindowTargets.OwnerProcessId(target.Handle);
        if (process == 0 || (target.ProcessId != 0 && target.ProcessId != process))
            throw new InvalidOperationException("That window has changed. Select it again.");
        if (initialBounds.Width != target.Bounds.Width || initialBounds.Height != target.Bounds.Height)
            throw new InvalidOperationException("That window changed size. Select it again.");
        if (WindowTargets.ExcludesCapture(target.Handle))
            throw new NotSupportedException("That window does not allow screen capture.");

        CapturedDisplay source;
        using (var device = new CanvasDevice())
        {
            GraphicsCaptureItem item = CreateForWindow(target.Handle);
            var expected = item.Size;
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.As<IDirect3DDevice>(),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, expected);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            var completion = new TaskCompletionSource<CapturedDisplay>(TaskCreationOptions.RunContinuationsAsynchronously);
            object callbackGate = new();
            bool stopping = false, acquired = false;
            pool.FrameArrived += OnFrame;
            item.Closed += OnClosed;
            Exception? captureFailure = null;
            try
            {
                session.StartCapture();
                source = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (Exception error) { captureFailure = error; throw; }
            finally
            {
                Exception? cleanupFailure = DrainAndDetachCallbacks(callbackGate, () => stopping = true,
                    () => pool.FrameArrived -= OnFrame, () => item.Closed -= OnClosed);
                if (cleanupFailure is not null)
                    throw captureFailure is null ? cleanupFailure :
                        new AggregateException("Window capture and callback cleanup failed.", captureFailure, cleanupFailure);
            }

            void OnClosed(GraphicsCaptureItem sender, object args) => completion.TrySetException(new InvalidOperationException("That window closed before capture finished."));

            void OnFrame(Direct3D11CaptureFramePool sender, object args)
            {
                lock (callbackGate)
                {
                    if (stopping || acquired) return;
                    try
                    {
                        using var frame = sender.TryGetNextFrame();
                        if (frame is null) return;
                        acquired = true;
                        if (frame.ContentSize.Width != expected.Width || frame.ContentSize.Height != expected.Height ||
                            !WindowTargets.TryGetBounds(target.Handle, out var current) ||
                            current.Width != initialBounds.Width || current.Height != initialBounds.Height ||
                            WindowTargets.OwnerProcessId(target.Handle) != process)
                            throw new InvalidOperationException("That window changed while capturing. Select it again.");
                        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                        if (frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0 ||
                            frame.ContentSize.Width > bitmap.SizeInPixels.Width || frame.ContentSize.Height > bitmap.SizeInPixels.Height)
                            throw new InvalidOperationException("The window supplied an invalid capture frame.");
                        byte[] bgra = bitmap.GetPixelBytes(0, 0, frame.ContentSize.Width, frame.ContentSize.Height);
                        byte[] rgba = StraightRgbaFromPremultipliedBgra(bgra);
                        bool visible = false;
                        for (int offset = 3; offset < rgba.Length && !visible; offset += 4) visible = rgba[offset] != 0;
                        if (!visible) throw new InvalidOperationException("The window did not provide a visible image. It may be unavailable for capture.");
                        completion.TrySetResult(new CapturedDisplay(frame.ContentSize.Width, frame.ContentSize.Height, rgba));
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                }
            }
        }
        return source;
    }

    private static Exception? DrainAndDetachCallbacks(object callbackGate, Action stopCallbacks, Action detachFrames, Action detachClosed)
    {
        // Wait for the active frame readback before any native cleanup can fail.
        // Queued callbacks then see stopping and leave capture resources untouched.
        lock (callbackGate) stopCallbacks();
        var failures = new List<Exception>();
        try { detachFrames(); } catch (Exception error) { failures.Add(error); }
        try { detachClosed(); } catch (Exception error) { failures.Add(error); }
        return failures.Count == 0 ? null : new AggregateException("Window capture event cleanup failed.", failures);
    }

    internal static async Task VerifySyntheticCleanupAsync()
    {
        object gate = new();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool callbackExited = false, stopping = false, detachedFrames = false, detachedClosed = false;
        var frameFailure = new InvalidOperationException("Synthetic FrameArrived removal failure.");
        var closedFailure = new InvalidOperationException("Synthetic Closed removal failure.");
        Task callback = Task.Run(() =>
        {
            lock (gate)
            {
                callbackEntered.TrySetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic callback was not released.");
                callbackExited = true;
            }
        });
        Task<Exception?>? cleanup = null;
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cleanup = Task.Run(() =>
            {
                cleanupStarted.TrySetResult();
                return DrainAndDetachCallbacks(gate, () => stopping = true,
                    () =>
                    {
                        Require(callbackExited && stopping, "native event removal ran before the frame callback drained");
                        detachedFrames = true;
                        throw frameFailure;
                    },
                    () => { detachedClosed = true; throw closedFailure; });
            });
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!cleanup.IsCompleted, "cleanup did not wait for the active frame callback");
            releaseCallback.Set();
            Exception? error = await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Require(detachedFrames && detachedClosed, "an event removal failure skipped the other removal");
            Require(error is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                ReferenceEquals(aggregate.InnerExceptions[0], frameFailure) && ReferenceEquals(aggregate.InnerExceptions[1], closedFailure),
                "native cleanup failures lost their original diagnostic information");
            bool accessedResources = false;
            lock (gate) { if (!stopping) accessedResources = true; }
            Require(!accessedResources, "a queued callback could access resources after cleanup");
        }
        finally
        {
            releaseCallback.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Window capture cleanup regression: " + message + ".");
        }
    }

    internal static byte[] StraightRgbaFromPremultipliedBgra(byte[] bgra)
    {
        if ((bgra.Length & 3) != 0) throw new ArgumentException("A window frame has invalid pixel data.", nameof(bgra));
        byte[] rgba = new byte[bgra.Length];
        for (int offset = 0; offset < bgra.Length; offset += 4)
        {
            int alpha = bgra[offset + 3];
            if (alpha == 0) continue;
            rgba[offset] = (byte)Math.Min(255, (bgra[offset + 2] * 255 + alpha / 2) / alpha);
            rgba[offset + 1] = (byte)Math.Min(255, (bgra[offset + 1] * 255 + alpha / 2) / alpha);
            rgba[offset + 2] = (byte)Math.Min(255, (bgra[offset] * 255 + alpha / 2) / alpha);
            rgba[offset + 3] = (byte)alpha;
        }
        return rgba;
    }

    private static GraphicsCaptureItem CreateForWindow(nint window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out nint text));
        nint factory = 0, item = 0;
        try
        {
            Guid factoryId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in factoryId, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            Guid itemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, in itemId, out item));
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(text);
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, in Guid iid, out nint result);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
}
