using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Ovi.Screenshot.Capture;

internal sealed class VideoFrameBufferPool
{
    private readonly int _frameBytes, _capacity;
    private readonly object _gate = new();
    private readonly Stack<BufferEntry> _available = new();
    private TaskCompletionSource _availableChanged = NewSignal();
    private int _allocated;

    internal VideoFrameBufferPool(int frameBytes, int capacity = 4)
    {
        if (frameBytes <= 0) throw new ArgumentOutOfRangeException(nameof(frameBytes));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _frameBytes = frameBytes; _capacity = capacity;
    }

    internal int AllocatedCount { get { lock (_gate) return _allocated; } }
    internal int LeasedBufferCount { get { lock (_gate) return _allocated - _available.Count; } }

    internal VideoFrameLease? TryRent()
    {
        lock (_gate)
        {
            BufferEntry entry;
            if (_available.TryPop(out var available)) entry = available;
            else
            {
                if (_allocated >= _capacity) return null;
                entry = new BufferEntry(new byte[_frameBytes]);
                _allocated++;
            }
            entry.References = 1;
            return new VideoFrameLease(this, entry);
        }
    }

    internal Task WaitForAvailableAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_available.Count != 0 || _allocated < _capacity) return Task.CompletedTask;
            return _availableChanged.Task.WaitAsync(cancellationToken);
        }
    }

    internal static VideoFrameLease WrapImmutable(byte[] pixels)
    {
        var pool = new VideoFrameBufferPool(pixels.Length, 1);
        var entry = new BufferEntry(pixels) { References = 1 };
        pool._allocated = 1;
        return new VideoFrameLease(pool, entry);
    }

    internal VideoFrameLease Retain(BufferEntry entry)
    {
        lock (_gate)
        {
            entry.References++;
            return new VideoFrameLease(this, entry);
        }
    }

    internal void Release(BufferEntry entry)
    {
        lock (_gate)
        {
            if (--entry.References != 0) return;
            _available.Push(entry);
            var changed = _availableChanged;
            _availableChanged = NewSignal();
            changed.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class BufferEntry(byte[] pixels)
    {
        internal readonly byte[] Pixels = pixels;
        internal readonly IBuffer Buffer = pixels.AsBuffer();
        internal int References;
    }
}

internal sealed class VideoFrameLease(VideoFrameBufferPool pool, VideoFrameBufferPool.BufferEntry entry) : IDisposable
{
    private readonly object _gate = new();
    private VideoFrameBufferPool.BufferEntry? _entry = entry;

    internal byte[] Pixels => GetEntry().Pixels;
    internal IBuffer Buffer => GetEntry().Buffer;

    internal VideoFrameLease Retain()
    {
        lock (_gate) return pool.Retain(GetEntry());
    }

    private VideoFrameBufferPool.BufferEntry GetEntry() => Volatile.Read(ref _entry) ?? throw new ObjectDisposedException(nameof(VideoFrameLease));

    public void Dispose()
    {
        lock (_gate)
        {
            var released = _entry;
            _entry = null;
            if (released is not null) pool.Release(released);
        }
    }

    // Win2D reads packed crop rows into this same buffer. Expand backwards so an
    // odd-width crop can gain an opaque black column without a second frame.
    internal void PadPackedCrop(int width, int height, int outputWidth, int outputHeight)
    {
        byte[] pixels = Pixels;
        int sourceStride = checked(width * 4), outputStride = checked(outputWidth * 4);
        if (width <= 0 || height <= 0 || outputWidth < width || outputHeight < height ||
            pixels.Length != checked(outputStride * outputHeight))
            throw new ArgumentException("The recording buffer dimensions do not match.");
        for (int row = height - 1; row >= 0; row--)
        {
            if (sourceStride != outputStride)
                System.Buffer.BlockCopy(pixels, row * sourceStride, pixels, row * outputStride, sourceStride);
            FillOpaqueBlack(pixels, row * outputStride + sourceStride, outputStride - sourceStride);
        }
        FillOpaqueBlack(pixels, height * outputStride, (outputHeight - height) * outputStride);
        Buffer.Length = (uint)pixels.Length;
    }

    private static void FillOpaqueBlack(byte[] pixels, int offset, int length)
    {
        pixels.AsSpan(offset, length).Clear();
        for (int alpha = offset + 3; alpha < offset + length; alpha += 4) pixels[alpha] = 255;
    }
}

internal sealed class BgraVideoFrame : IDisposable
{
    private readonly VideoFrameLease _lease;
    internal byte[] Pixels => _lease.Pixels;
    internal IBuffer Buffer => _lease.Buffer;
    internal TimeSpan Timestamp { get; }
    internal TimeSpan Duration { get; }

    internal BgraVideoFrame(byte[] pixels, TimeSpan timestamp, TimeSpan duration)
        : this(VideoFrameBufferPool.WrapImmutable(pixels), timestamp, duration) { }

    internal BgraVideoFrame(VideoFrameLease lease, TimeSpan timestamp, TimeSpan duration)
    {
        _lease = lease; Timestamp = timestamp; Duration = duration;
    }

    public void Dispose() => _lease.Dispose();
}
