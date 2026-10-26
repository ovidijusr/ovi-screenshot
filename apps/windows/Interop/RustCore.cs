using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ovi.Screenshot.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct ImageDescriptor
{
    public uint Width;
    public uint Height;
    public ulong StrideBytes;
    public uint PixelFormat;
    public uint AlphaMode;
    public uint ColorSpace;
    public uint Reserved;

    public static ImageDescriptor Rgba(uint width, uint height, ulong stride) => new()
    {
        Width = width, Height = height, StrideBytes = stride,
        PixelFormat = 1, AlphaMode = 1, ColorSpace = 1
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct DocumentInfo
{
    public ImageDescriptor Image;
    public ulong Revision;
}

internal static partial class CoreNative
{
    private const string Library = "ovi_core";

    [LibraryImport(Library, EntryPoint = "ovi_core_api_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ApiVersion();
    [LibraryImport(Library, EntryPoint = "ovi_status_message")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint StatusMessage(int status);
    [LibraryImport(Library, EntryPoint = "ovi_document_create_from_pixels")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int Create(in ImageDescriptor descriptor, byte* data, ulong length, out nint document);
    [LibraryImport(Library, EntryPoint = "ovi_document_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInfo(DocumentHandle document, out DocumentInfo info);
    [LibraryImport(Library, EntryPoint = "ovi_document_add_window_shadow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddWindowShadow(DocumentHandle document, out nint styledDocument);
    [LibraryImport(Library, EntryPoint = "ovi_document_render_preview")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RenderPreview(DocumentHandle document, uint maxWidth, uint maxHeight, out nint buffer, out ImageDescriptor image);
    [LibraryImport(Library, EntryPoint = "ovi_document_export_png")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ExportPng(DocumentHandle document, out nint buffer);
    [LibraryImport(Library, EntryPoint = "ovi_buffer_get_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int BufferView(BufferHandle buffer, out nint data, out ulong length);
    [LibraryImport(Library, EntryPoint = "ovi_document_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReleaseDocument(nint document);
    [LibraryImport(Library, EntryPoint = "ovi_buffer_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReleaseBuffer(nint buffer);

    internal static void Check(int status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Image engine: {Marshal.PtrToStringUTF8(StatusMessage(status))} (code {status}).");
    }

    internal static void VerifyAbi()
    {
        if (Marshal.SizeOf<ImageDescriptor>() != 32 || Marshal.SizeOf<DocumentInfo>() != 40 || ApiVersion() != 2)
            throw new InvalidOperationException("The image engine and Windows app use different API versions. Rebuild both components.");
    }
}

internal sealed class DocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal DocumentHandle(nint handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() => CoreNative.ReleaseDocument(handle) == 0;
}

internal sealed class BufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal BufferHandle(nint handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() => CoreNative.ReleaseBuffer(handle) == 0;

    internal byte[] CopyBytes()
    {
        CoreNative.Check(CoreNative.BufferView(this, out nint data, out ulong length));
        byte[] bytes = new byte[checked((int)length)];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }
}

// All operations, including release, share one lock. No borrowed Rust buffer escapes this wrapper.
internal sealed class ImageDocument : IDisposable
{
    internal const int WindowShadowPadding = 24;
    private readonly DocumentHandle _handle;
    private readonly object _gate = new();
    private bool _disposed;

    private ImageDocument(DocumentHandle handle) => _handle = handle;

    internal static unsafe ImageDocument Import(ImageDescriptor descriptor, byte[] rgba)
    {
        CoreNative.VerifyAbi();
        fixed (byte* data = rgba)
        {
            CoreNative.Check(CoreNative.Create(in descriptor, data, (ulong)rgba.LongLength, out nint handle));
            return new ImageDocument(new DocumentHandle(handle));
        }
    }

    // Opaque monitor BGRA needs channel conversion only inside the selection.
    internal static ImageDocument ImportBgraRegion(int sourceWidth, int sourceHeight, byte[] sourceBgra,
        int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sourceBgra);
        if (sourceWidth <= 0 || sourceHeight <= 0 || (sourceBgra.Length & 3) != 0
            || (long)sourceWidth * sourceHeight != sourceBgra.LongLength / 4)
            throw new ArgumentException("Source image dimensions do not match its pixel buffer.", nameof(sourceBgra));
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || width > sourceWidth - x || height > sourceHeight - y)
            throw new ArgumentOutOfRangeException(nameof(width), "The selected region must fit inside its source image.");
        if (width > 32_768 || height > 32_768 || (long)width * height > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width), "The selected image exceeds the image engine's resource limit.");
        byte[] rgba = new byte[checked(width * height * 4)];
        int output = 0;
        for (int row = 0; row < height; row++)
        {
            int input = checked(((y + row) * sourceWidth + x) * 4);
            for (int column = 0; column < width; column++, input += 4, output += 4)
            {
                rgba[output] = sourceBgra[input + 2];
                rgba[output + 1] = sourceBgra[input + 1];
                rgba[output + 2] = sourceBgra[input];
                rgba[output + 3] = 255;
            }
        }
        return Import(ImageDescriptor.Rgba((uint)width, (uint)height, (ulong)width * 4), rgba);
    }

    internal ImageDocument AddWindowShadow()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CoreNative.Check(CoreNative.AddWindowShadow(_handle, out nint pointer));
            return new ImageDocument(new DocumentHandle(pointer));
        }
    }

    internal DocumentInfo Info
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                CoreNative.Check(CoreNative.GetInfo(_handle, out DocumentInfo info));
                return info;
            }
        }
    }

    internal byte[] ExportPng()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CoreNative.Check(CoreNative.ExportPng(_handle, out nint pointer));
            using var buffer = new BufferHandle(pointer);
            return buffer.CopyBytes();
        }
    }

    internal (ImageDescriptor Image, byte[] Rgba) RenderPreview(uint maxWidth, uint maxHeight)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CoreNative.Check(CoreNative.RenderPreview(_handle, maxWidth, maxHeight, out nint pointer, out ImageDescriptor image));
            using var buffer = new BufferHandle(pointer);
            return (image, buffer.CopyBytes());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }
}
