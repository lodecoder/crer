using System.Runtime.InteropServices;
using Crer.Windows;

internal static class TopmostTests
{
    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    private static readonly WindowProc Proc = HandleWindowMessage;
    private static nint blockedTopmostWindow;
    private static nint resetRequiredWindow;
    private static int rejectedPositionChanges, recoveryPositionChanges;
    private static bool recoveryActivatedWindow;
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
            rejectedPositionChanges++;
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            // OS API は成功するが、対象側が最前面属性の変更を採用しない状況を再現する。
            position.Flags |= 0x0004; // SWP_NOZORDER
            Marshal.StructureToPtr(position, lParam, false);
        }
        if (window == resetRequiredWindow && message == 0x0046)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            recoveryPositionChanges++;
            recoveryActivatedWindow |= (position.Flags & 0x0010) == 0; // SWP_NOACTIVATE
            // 初回の変更だけを拒否し、解除と再設定の組では反映できる状態を再現する。
            if (recoveryPositionChanges == 1)
            {
                position.Flags |= 0x0004; // SWP_NOZORDER
                Marshal.StructureToPtr(position, lParam, false);
            }
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
            Require(topmost.Status == 0, $"最前面属性を有効化できる（Win32 status {topmost.Status}, stage {topmost.FailureStage}, foreground permission {topmost.ForegroundPermissionGranted}, permission error {topmost.ForegroundPermissionError}）");
            Require(topmost.ForegroundPermissionGranted.HasValue, "有効化時の前景権限移譲結果を保持する");
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
            var disabled = input.SetProcessTopmostDetailed((uint)Environment.ProcessId, false);
            Require(disabled.Status == 0, "disable topmost");
            Require(disabled.ForegroundPermissionGranted is null && disabled.ForegroundPermissionError == 0,
                "最前面属性の解除時には前景権限を移譲しない");
            Require(!disabled.RecoveryAttempted && disabled.RecoveryInitialStyle is null,
                "最前面属性の解除時には回復処理を行わない");
            Require(!IsTopmost(browser) && !IsTopmost(popup), "owner and owned popup must leave topmost together");
            resetRequiredWindow = browser;
            recoveryPositionChanges = 0;
            recoveryActivatedWindow = false;
            var recovered = input.SetProcessTopmostDetailed((uint)Environment.ProcessId, true);
            Require(recovered.Status == 0 && recovered.FailureStage == "none" && IsTopmost(browser),
                "初回の属性不一致は解除後の再設定で回復できる");
            Require(recovered.RecoveryAttempted && recovered.RecoveryInitialStyle is { } initialStyle && (initialStyle & 8) == 0,
                "回復した場合も初回の最前面属性不一致を保持する");
            Require(recovered.RecoveryResetSucceeded == true && recovered.RecoveryResetError == 0
                && recovered.RecoveryReapplySucceeded == true && recovered.RecoveryReapplyError == 0,
                "回復時の解除と再設定の実 API 結果を保持する");
            Require(recoveryPositionChanges == 3 && !recoveryActivatedWindow,
                "回復処理は初回設定・一度の解除・一度の再設定に限定する");
            Require(GetForegroundWindow() == foreground, "回復処理も利用者の前景を変更しない");
            resetRequiredWindow = 0;
            Require(input.SetProcessTopmostDetailed((uint)Environment.ProcessId, false).Status == 0,
                "回復試験後に最前面属性を解除できる");
            blockedTopmostWindow = browser;
            rejectedPositionChanges = 0;
            var mismatch = input.SetProcessTopmostDetailed((uint)Environment.ProcessId, true);
            Require(mismatch.Status == 5 && mismatch.FailureStage == "style_mismatch" && mismatch.Window == browser,
                "rejected window-position change must report a style mismatch");
            Require(mismatch.SetWindowPosSucceeded == true && mismatch.SetWindowPosError == 0 && mismatch.StyleReadError == 0,
                "style mismatch must preserve successful OS API results instead of reporting access denied");
            Require(mismatch.ExtendedStyle is { } style && (style & 8) == 0, "style mismatch must include the actual non-topmost style");
            Require(mismatch.RecoveryAttempted && mismatch.RecoveryResetSucceeded == true && mismatch.RecoveryReapplySucceeded == true,
                "回復後も属性が反映されない場合は API 成功と属性不一致を保持する");
            Require(rejectedPositionChanges == 3, "回復後も拒否された場合に設定を無限再試行しない");
            Require(GetForegroundWindow() == foreground, "diagnostic topmost request must not steal focus");
            blockedTopmostWindow = 0;
            resetRequiredWindow = 0;
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
            resetRequiredWindow = 0;
            if (popup != 0) DestroyWindow(popup);
            if (browser != 0) DestroyWindow(browser);
            if (hidden != 0) DestroyWindow(hidden);
            UnregisterClassW(name, 0);
        }
    }
}
