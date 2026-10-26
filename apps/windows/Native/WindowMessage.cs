namespace Ovi.Screenshot.Native;

internal static class WindowMessage
{
    internal const uint Activate = 0x0006;
    internal const uint Paint = 0x000F;
    internal const uint Close = 0x0010;
    internal const uint Quit = 0x0012;
    internal const uint EraseBackground = 0x0014;
    internal const uint SystemColorsChanged = 0x0015;
    internal const uint SettingsChanged = 0x001A;
    internal const uint CancelMode = 0x001F;
    internal const uint DrawItem = 0x002B;
    internal const uint SetFont = 0x0030;
    internal const uint DisplayChanged = 0x007E;
    internal const uint NonClientDestroyed = 0x0082;
    internal const uint NonClientHitTest = 0x0084;
    internal const uint KeyDown = 0x0100;
    internal const uint Command = 0x0111;
    internal const uint Timer = 0x0113;
    internal const uint MouseMove = 0x0200;
    internal const uint LeftButtonDown = 0x0201;
    internal const uint LeftButtonUp = 0x0202;
    internal const uint CaptureChanged = 0x0215;
    internal const uint PowerBroadcast = 0x0218;
    internal const uint MouseLeave = 0x02A3;
    internal const uint DpiChanged = 0x02E0;
    internal const uint ThemeChanged = 0x031A;
}
