using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// Keeps the standard Windows title bar, and with it snap layouts, Aero Snap and the system
/// menu, but makes it fit the screen and the theme.
/// </summary>
internal static partial class WindowFrame
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Shrinks the window to the work area of the monitor it is about to open on. Call before
    /// <see cref="Window.Show"/>. CenterScreen places the window on the monitor under the cursor,
    /// and a window taller than that work area gets centred with its title bar above the screen.
    /// </summary>
    public static void FitToWorkArea(Window window, double margin = 24)
    {
        if (!GetCursorPos(out var cursor)) return;

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        // The process is per-monitor DPI aware, so the work area arrives in physical pixels.
        double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;

        double workWidth = (info.rcWork.Right - info.rcWork.Left) / scale;
        double workHeight = (info.rcWork.Bottom - info.rcWork.Top) / scale;

        double width = Math.Max(0, workWidth - margin * 2);
        double height = Math.Max(0, workHeight - margin * 2);

        if (window.Width > width) window.Width = width;
        if (window.Height > height) window.Height = height;
        if (window.MinWidth > window.Width) window.MinWidth = window.Width;
        if (window.MinHeight > window.Height) window.MinHeight = window.Height;
    }

    /// <summary>
    /// Light or dark title bar on Windows 10 20H1 and later; on Windows 11 the caption, its text
    /// and the window border also take the theme colours. Call from SourceInitialized, before the
    /// window is first painted, and again whenever the theme changes. Older builds reject the
    /// attributes they do not know, which is harmless.
    /// </summary>
    public static void ApplyTheme(Window window, bool dark, Color caption, Color text, Color border)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int useDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

        SetColor(hwnd, DWMWA_CAPTION_COLOR, caption);
        SetColor(hwnd, DWMWA_TEXT_COLOR, text);
        SetColor(hwnd, DWMWA_BORDER_COLOR, border);
    }

    private static void SetColor(IntPtr hwnd, int attribute, Color color)
    {
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }
}
