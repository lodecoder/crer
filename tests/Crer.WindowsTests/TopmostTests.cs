using System.Runtime.InteropServices;
using Crer.Windows;

internal static class TopmostTests
{
    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    private static readonly WindowProc Proc = HandleWindowMessage;
    private static nint blockedTopmostWindow;
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
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
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

    private static nint HandleWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (window == blockedTopmostWindow && message == 0x0046) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            // OS API は成功するが、対象側が最前面属性の変更を採用しない状況を再現する。
            position.Flags |= 0x0004; // SWP_NOZORDER
            Marshal.StructureToPtr(position, lParam, false);
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    public static void Run()
    {
        using var input = new WindowsInput();
        var name = "Chrome_WidgetWin_CrerTest_" + Guid.NewGuid().ToString("N");
        var windowClass = new WindowClass { Proc = Proc, Name = name };
        Require(RegisterClassW(ref windowClass) != 0, "register test window class");
        nint browser = 0, popup = 0, hidden = 0;
        try
        {
            // メニューやツールチップと同様に、所有者付きの補助ウィンドウを Z 順の先頭に置く。
            browser = CreateWindowExW(0, name, "CRER topmost regression test", 0x00cf0000, 30, 30, 240, 120, 0, 0, 0, 0);
            popup = CreateWindowExW(0x08000080, name, "CRER helper", 0x80000000, 30, 30, 40, 40, browser, 0, 0, 0);
            hidden = CreateWindowExW(0x08000080, name, "CRER hidden helper", 0x80000000, 30, 30, 40, 40, 0, 0, 0, 0);
            Require(browser != 0 && popup != 0 && hidden != 0, "create test windows");
            ShowWindow(browser, 4); // SW_SHOWNOACTIVATE
            ShowWindow(popup, 4);
            SetWindowPos(popup, new nint(-1), 0, 0, 0, 0, 0x13);
            var foreground = GetForegroundWindow();
            Require(!IsTopmost(browser), "browser initially not topmost");
            var topmost = input.SetProcessTopmostDetailed((uint)Environment.ProcessId, true);
            Require(topmost.Status == 0, $"enable topmost (Win32 status {topmost.Status}, stage {topmost.FailureStage})");
            Require(topmost.FailureStage == "none" && topmost.Window == browser && topmost.SetWindowPosSucceeded == true,
                "successful diagnostics must identify the browser window and successful API call");
            Require(topmost.SetWindowPosError == 0 && topmost.StyleReadError == 0 && (topmost.ExtendedStyle & 8) == 8,
                "successful diagnostics must include the verified topmost style");
            Require(IsTopmost(browser), "topmost must apply to the visible browser owner, not its popup");
            Require(!IsWindowVisible(hidden), "hidden helpers must remain hidden");
            Require(GetForegroundWindow() == foreground, "topmost-only must not steal focus");
            var originalStyle = GetWindowLongPtrW(browser, -20).ToInt64();
            foreach (uint alpha in new uint[] { 128, 0, 255 })
            {
                Require(input.SetProcessOpacity((uint)Environment.ProcessId, alpha) == 0, "set opacity");
                Require(GetLayeredWindowAttributes(browser, out _, out var actual, out var flags)
                    && actual == alpha && flags == 2, "browser opacity must match");
                Require((GetWindowLongPtrW(browser, -20).ToInt64() & ~0x80000L) == originalStyle, "opacity must preserve other window styles");
                Require(!GetLayeredWindowAttributes(popup, out _, out _, out _), "opacity must not target owned popup");
                Require(GetForegroundWindow() == foreground, "opacity must not steal focus");
                Require(!IsWindowVisible(hidden), "opacity must not show hidden helpers");
            }
            Require(input.SetProcessTopmostOnly((uint)Environment.ProcessId, false) == 0, "disable topmost");
            Require(!IsTopmost(browser) && !IsTopmost(popup), "owner and owned popup must leave topmost together");
            blockedTopmostWindow = browser;
            var mismatch = input.SetProcessTopmostDetailed((uint)Environment.ProcessId, true);
            Require(mismatch.Status == 5 && mismatch.FailureStage == "style_mismatch" && mismatch.Window == browser,
                "rejected window-position change must report a style mismatch");
            Require(mismatch.SetWindowPosSucceeded == true && mismatch.SetWindowPosError == 0 && mismatch.StyleReadError == 0,
                "style mismatch must preserve successful OS API results instead of reporting access denied");
            Require(mismatch.ExtendedStyle is { } style && (style & 8) == 0, "style mismatch must include the actual non-topmost style");
            Require(GetForegroundWindow() == foreground, "diagnostic topmost request must not steal focus");
            blockedTopmostWindow = 0;
            DestroyWindow(popup); popup = 0;
            DestroyWindow(browser); browser = 0;
            var missing = input.SetProcessTopmostDetailed((uint)Environment.ProcessId, true);
            Require(missing.Status == 1168 && missing.FailureStage == "window_not_found" && missing.Window == 0
                && missing.SetWindowPosSucceeded is null && missing.ExtendedStyle is null, "hidden widget is not a browser target");
            Require(input.SetProcessOpacity((uint)Environment.ProcessId, 128) == 1168, "opacity must ignore hidden widgets");
            Require(!IsWindowVisible(hidden), "failed lookup must not show hidden helpers");
            Console.WriteLine("Windows topmost window selection and focus tests passed.");
        }
        finally
        {
            blockedTopmostWindow = 0;
            if (popup != 0) DestroyWindow(popup);
            if (browser != 0) DestroyWindow(browser);
            if (hidden != 0) DestroyWindow(hidden);
            UnregisterClassW(name, 0);
        }
    }
}
