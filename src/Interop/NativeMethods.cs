using System;
using System.Runtime.InteropServices;

namespace TvRemote.Interop
{
    // Chamadas à API do Windows, todas num lugar só (regra CA1060 da Microsoft).
    internal static class NativeMethods
    {
        internal const int KeyboardLowLevelHook = 13;
        internal const int KeyDownMessage = 0x0100;
        internal const int SystemKeyDownMessage = 0x0104;
        internal const uint KeyUpFlag = 0x0002;
        internal const byte WindowsKey = 0x5B;
        internal const byte KKey = 0x4B;
        internal const int HalftoneStretchMode = 4;
        internal const int SourceCopy = 0x00CC0020;

        internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int type, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        internal static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StretchBlt(
            IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr gdiObject);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);

        [DllImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr process);
    }
}
