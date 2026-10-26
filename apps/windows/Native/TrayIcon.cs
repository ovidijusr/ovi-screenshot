using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ovi.Screenshot.Native;

/// <summary>Owns a small transparent capture-frame icon; no shared system HICON is borrowed.</summary>
internal sealed class TrayIcon : IDisposable
{
    internal nint Handle { get; private set; }

    internal TrayIcon()
    {
        const int size = 32;
        var shape = new bool[size, size];
        // Four open corners read as a screenshot selection at notification-area sizes.
        Fill(5, 5, 9, 3); Fill(5, 5, 3, 9);
        Fill(18, 5, 9, 3); Fill(24, 5, 3, 9);
        Fill(5, 24, 9, 3); Fill(5, 18, 3, 9);
        Fill(18, 24, 9, 3); Fill(24, 18, 3, 9);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40); writer.Write(size); writer.Write(size * 2);
        writer.Write((short)1); writer.Write((short)32);
        writer.Write(0); writer.Write(size * size * 4);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (int y = size - 1; y >= 0; y--)
        for (int x = 0; x < size; x++)
        {
            // A one-pixel dark keyline keeps the monochrome outline legible on either taskbar.
            uint pixel = shape[x, y] ? 0xffffffff : NearShape(x, y) ? 0xff171717 : 0;
            writer.Write(pixel);
        }
        for (int y = size - 1; y >= 0; y--)
        for (int group = 0; group < size / 8; group++)
        {
            byte mask = 0;
            for (int bit = 0; bit < 8; bit++)
                if (!NearShape(group * 8 + bit, y)) mask |= (byte)(0x80 >> bit);
            writer.Write(mask);
        }
        byte[] resource = stream.ToArray();
        Handle = CreateIconFromResourceEx(resource, (uint)resource.Length, true, 0x00030000, size, size, 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the capture icon.");

        void Fill(int left, int top, int width, int height)
        {
            for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++) shape[x, y] = true;
        }
        bool NearShape(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (x + dx >= 0 && x + dx < size && y + dy >= 0 && y + dy < size && shape[x + dx, y + dy]) return true;
            return false;
        }
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        DestroyIcon(Handle);
        Handle = 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconFromResourceEx(byte[] resource, uint length, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
