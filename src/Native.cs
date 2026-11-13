using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrayView
{
    // Win32, DWM and shell calls used by the app.
    static class Native
    {
        public const int WM_SETCURSOR = 0x0020, WM_GETMINMAXINFO = 0x0024, WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_LBUTTONDOWN = 0x0201, WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0;
        public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, LWA_ALPHA = 2;
        const int SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_ROUND = 2, IMAGE_ICON = 1, APP_ICON_ID = 32512, APPMODE_ALLOW_DARK = 1;
        const int MONITOR_DEFAULTTONEAREST = 2, MDT_EFFECTIVE_DPI = 0, SM_CXSMICON = 49;
        const int SPI_GETCLIENTAREAANIMATION = 0x1042, ABM_GETTASKBARPOS = 5;

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage, uEdge;
            public int Left, Top, Right, Bottom;
            public IntPtr lParam;
        }

        [DllImport("user32", SetLastError = true)]
        static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, int type, int cx, int cy, int flags);

        [DllImport("user32")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32")]
        static extern IntPtr GetModuleHandleW(IntPtr name);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string path, IntPtr file, int flags);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        public static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("user32")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32")]
        static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32")]
        static extern int GetSystemMetricsForDpi(int index, int dpi);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowW(string className, string title);

        [DllImport("user32")]
        static extern IntPtr MonitorFromPoint(Point point, int flags);

        [DllImport("shcore")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out int dpiX, out int dpiY);

        [DllImport("dwmapi")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi")]
        static extern int DwmFlush();

        [DllImport("uxtheme", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme", EntryPoint = "#136")]
        static extern void FlushMenuThemes();

        [DllImport("user32")]
        static extern int GetWindowLongW(IntPtr hwnd, int index);

        [DllImport("user32")]
        static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

        [DllImport("user32")]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int key, byte alpha, int flags);

        [DllImport("user32")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        [DllImport("user32")]
        static extern bool SystemParametersInfoW(int action, int param, out bool value, int winIni);

        [DllImport("shell32")]
        static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);

        [DllImport("user32")]
        public static extern bool ReleaseCapture();

        [DllImport("user32")]
        public static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32")]
        public static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32")]
        public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

        [DllImport("user32")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32")]
        public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

        // The scale of the monitor at a point or under a window, as the user set it now. WinForms'
        // DeviceDpi and GDI's point sizes use the system DPI instead, which keeps its sign-in value
        // after the display scale changes.
        public static int DpiAt(Point point)
        {
            int x, y;
            return GetDpiForMonitor(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, out x, out y) == 0 ? x : 96;
        }

        public static int DpiOf(IntPtr hwnd)
        {
            int dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi : 96;
        }

        // The size of a notification-area icon at the taskbar's current scale.
        public static int TrayIconSize()
        {
            IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
            return GetSystemMetricsForDpi(SM_CXSMICON, taskbar == IntPtr.Zero ? DpiAt(Point.Empty) : DpiOf(taskbar));
        }

        // Which side of the screen the taskbar is on: Left, Top, Right or Bottom. Windows 11 lets the
        // taskbar move to any side. The work area tells for the monitor at hand; when it fills the whole
        // screen (auto-hide), ask the shell where the main taskbar is.
        public static AnchorStyles TaskbarEdge(Screen screen)
        {
            Rectangle b = screen.Bounds, w = screen.WorkingArea;
            if (w.Bottom < b.Bottom) return AnchorStyles.Bottom;
            if (w.Top > b.Top) return AnchorStyles.Top;
            if (w.Left > b.Left) return AnchorStyles.Left;
            if (w.Right < b.Right) return AnchorStyles.Right;
            APPBARDATA data = new APPBARDATA();
            data.cbSize = Marshal.SizeOf(data);
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero)
            {
                switch (data.uEdge)
                {
                    case 0: return AnchorStyles.Left;
                    case 1: return AnchorStyles.Top;
                    case 2: return AnchorStyles.Right;
                }
            }
            return AnchorStyles.Bottom;
        }

        // Lets native (Win32) menus follow the Windows dark mode, like Explorer's.
        public static void AllowDarkMenus()
        {
            try { SetPreferredAppMode(APPMODE_ALLOW_DARK); FlushMenuThemes(); }
            catch (EntryPointNotFoundException) { }
        }

        public static void StyleFrame(IntPtr hwnd, bool dark, Color border)
        {
            int on = dark ? 1 : 0, round = DWMWCP_ROUND, color = border.R | border.G << 8 | border.B << 16;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, 4);
        }

        // The icon embedded by csc /win32icon, rendered by Windows at the exact size requested.
        public static Icon AppIcon(int size)
        {
            IntPtr icon = LoadImageW(GetModuleHandleW(IntPtr.Zero), new IntPtr(APP_ICON_ID), IMAGE_ICON, size, size, 0);
            return icon == IntPtr.Zero ? null : Icon.FromHandle(icon);
        }

        // Hands unused pages back to Windows; a tray app spends nearly all its time idle.
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        }

        // "Show animations in Windows" (Settings > Accessibility > Visual effects).
        public static bool AnimationsEnabled()
        {
            bool on;
            return !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, out on, 0) || on;
        }

        // Turns window-wide transparency on (for fades) or off (the normal, fastest path).
        public static void SetLayered(IntPtr hwnd, bool on)
        {
            int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, on ? style | WS_EX_LAYERED : style & ~WS_EX_LAYERED);
        }

        public static void SetAlpha(IntPtr hwnd, byte alpha)
        {
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }

        public static void MoveWindow(IntPtr hwnd, int x, int y)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public static void PlaceOnTop(IntPtr hwnd, Rectangle r)
        {
            SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Waits for the next frame DWM composes, so animations step once per refresh.
        public static void WaitForFrame()
        {
            if (DwmFlush() != 0)
                System.Threading.Thread.Sleep(8);
        }
    }
}
