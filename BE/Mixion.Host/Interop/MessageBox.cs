using System.Runtime.InteropServices;

namespace Mixion.Host.Interop;

/// <summary>
/// Thin wrapper over user32!MessageBoxW. Used for the pre-server driver-miss
/// dialog so the user gets a real Windows error window instead of a console
/// message they may never see (the .exe is GUI-launched in production).
/// </summary>
public static class MessageBox
{
    private const uint MB_OK         = 0x00000000;
    private const uint MB_ICONERROR  = 0x00000010;
    private const uint MB_TOPMOST    = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void ShowError(string caption, string text)
    {
        MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
    }
}
