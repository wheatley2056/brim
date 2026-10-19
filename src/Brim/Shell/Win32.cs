using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Brim.Shell;

/// <summary>Die wenigen Win32-Aufrufe, die WinUI und WinUIEx nicht abdecken (über CsWin32 erzeugt).</summary>
internal static class Win32
{
    public const uint WmHotkey = PInvoke.WM_HOTKEY;
    public const uint WmNcCalcSize = PInvoke.WM_NCCALCSIZE;

    /// <summary>Kein Taskleisteneintrag, nie aktivieren beim Klicken, nicht in Alt+Tab.</summary>
    public static void MakeOverlay(nint hwnd)
    {
        var h = new HWND(hwnd);

        // Reines Popup: ohne Rahmen zeichnet DWM weder Schatten noch die helle Kante oben.
        var frame = (WINDOW_STYLE)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        frame &= ~(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_THICKFRAME | WINDOW_STYLE.WS_SYSMENU
            | WINDOW_STYLE.WS_MINIMIZEBOX | WINDOW_STYLE.WS_MAXIMIZEBOX | WINDOW_STYLE.WS_BORDER | WINDOW_STYLE.WS_DLGFRAME);
        frame |= WINDOW_STYLE.WS_POPUP;
        _ = PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE, unchecked((int)frame));

        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        style |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST;
        style &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        _ = PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)style);
        PInvoke.SetWindowPos(h, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        // Windows 11 würde rahmenlose Fenster sonst abrunden und die Form der Notch beschneiden.
        unsafe
        {
            var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
            _ = PInvoke.DwmSetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(DWM_WINDOW_CORNER_PREFERENCE));

            // Ebenso die 1-px-Fensterkante von Windows 11 (DWMWA_COLOR_NONE).
            var none = 0xFFFFFFFEu;
            _ = PInvoke.DwmSetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &none, sizeof(uint));
        }
    }

    /// <summary>Nur dieses Rechteck (physische Pixel, Fensterkoordinaten) nimmt Maus und Zeichnen an.</summary>
    public static void SetRegion(nint hwnd, int x, int y, int width, int height)
    {
        // Das System übernimmt die Region; sie darf danach nicht freigegeben werden.
        var region = PInvoke.CreateRectRgn(x, y, x + width, y + height);
        _ = PInvoke.SetWindowRgn(new HWND(hwnd), region, true);
    }

    public static uint GetDpi(nint hwnd) => PInvoke.GetDpiForWindow(new HWND(hwnd));

    /// <summary>Mausposition in physischen Bildschirmpixeln.</summary>
    public static bool TryGetCursor(out int x, out int y)
    {
        var ok = PInvoke.GetCursorPos(out var point);
        (x, y) = (point.X, point.Y);
        return ok;
    }

    /// <summary>Bildwiederholrate (Hz) des Monitors, auf dem das Fenster liegt; 0, wenn unbekannt.</summary>
    public static unsafe int GetRefreshRate(nint hwnd)
    {
        var monitor = PInvoke.MonitorFromWindow(new HWND(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            return 0;
        }
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        return PInvoke.EnumDisplaySettings(info.szDevice.ToString(), ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
            ? (int)mode.dmDisplayFrequency
            : 0;
    }

    public static bool RegisterHotkey(nint hwnd, int id, uint virtualKey) =>
        PInvoke.RegisterHotKey(new HWND(hwnd), id, HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, virtualKey);

    public static void UnregisterHotkey(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);

    public static void Activate(nint hwnd) => PInvoke.SetForegroundWindow(new HWND(hwnd));

    /// <summary>Position der Taskleiste (Plan 4.1: Taskleiste oben erkennen).</summary>
    public static (int Edge, RECT Rect)? TaskbarPosition()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (PInvoke.SHAppBarMessage(PInvoke.ABM_GETTASKBARPOS, ref data) == 0)
        {
            return null;
        }
        return ((int)data.uEdge, data.rc);
    }

    /// <summary>ABE_TOP</summary>
    public const int EdgeTop = 1;
}
