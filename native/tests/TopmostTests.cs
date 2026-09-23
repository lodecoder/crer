using System.Runtime.InteropServices;

internal static class TopmostTests
{
    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    private static readonly WindowProc Proc = DefWindowProcW;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProc Proc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);

    private static bool IsTopmost(nint window) => (GetWindowLongPtrW(window, -20).ToInt64() & 8) != 0;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run()
    {
        var name = "Chrome_WidgetWin_CrerTest_" + Guid.NewGuid().ToString("N");
        var windowClass = new WindowClass { Proc = Proc, Name = name };
        Require(RegisterClassW(ref windowClass) != 0, "register test window class");
        nint browser = 0, popup = 0, hidden = 0;
        try
        {
            // The owned Chrome widget appears first in Z order, just like a menu/tooltip.
            browser = CreateWindowExW(0, name, "CRER topmost regression test", 0x00cf0000, 30, 30, 240, 120, 0, 0, 0, 0);
            popup = CreateWindowExW(0x08000080, name, "CRER helper", 0x80000000, 30, 30, 40, 40, browser, 0, 0, 0);
            hidden = CreateWindowExW(0x08000080, name, "CRER hidden helper", 0x80000000, 30, 30, 40, 40, 0, 0, 0, 0);
            Require(browser != 0 && popup != 0 && hidden != 0, "create test windows");
            ShowWindow(browser, 4); // SW_SHOWNOACTIVATE
            ShowWindow(popup, 4);
            SetWindowPos(popup, new nint(-1), 0, 0, 0, 0, 0x13);
            var foreground = GetForegroundWindow();
            Require(!IsTopmost(browser), "browser initially not topmost");
            Require(InputBridge.TestTopmost((uint)Environment.ProcessId, true) == 0, "enable topmost");
            Require(IsTopmost(browser), "topmost must apply to the visible browser owner, not its popup");
            Require(!IsWindowVisible(hidden), "hidden helpers must remain hidden");
            Require(GetForegroundWindow() == foreground, "topmost-only must not steal focus");
            var originalStyle = GetWindowLongPtrW(browser, -20).ToInt64();
            foreach (uint alpha in new uint[] { 128, 0, 255 })
            {
                Require(InputBridge.TestOpacity((uint)Environment.ProcessId, alpha) == 0, "set opacity");
                Require(GetLayeredWindowAttributes(browser, out _, out var actual, out var flags)
                    && actual == alpha && flags == 2, "browser opacity must match");
                Require((GetWindowLongPtrW(browser, -20).ToInt64() & ~0x80000L) == originalStyle, "opacity must preserve other window styles");
                Require(!GetLayeredWindowAttributes(popup, out _, out _, out _), "opacity must not target owned popup");
                Require(GetForegroundWindow() == foreground, "opacity must not steal focus");
                Require(!IsWindowVisible(hidden), "opacity must not show hidden helpers");
            }
            Require(InputBridge.TestTopmost((uint)Environment.ProcessId, false) == 0, "disable topmost");
            Require(!IsTopmost(browser) && !IsTopmost(popup), "owner and owned popup must leave topmost together");
            DestroyWindow(popup); popup = 0;
            DestroyWindow(browser); browser = 0;
            Require(InputBridge.TestTopmost((uint)Environment.ProcessId, true) == 1168, "hidden widget is not a browser target");
            Require(InputBridge.TestOpacity((uint)Environment.ProcessId, 128) == 1168, "opacity must ignore hidden widgets");
            Require(!IsWindowVisible(hidden), "failed lookup must not show hidden helpers");
            Console.WriteLine("Native topmost window selection and focus tests passed.");
        }
        finally
        {
            if (popup != 0) DestroyWindow(popup);
            if (browser != 0) DestroyWindow(browser);
            if (hidden != 0) DestroyWindow(hidden);
            UnregisterClassW(name, 0);
        }
    }
}
