using System.Runtime.InteropServices;
using Crer.Windows;

internal static class InputRegressionTests
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
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Character(char expected, string message) => Require(InputBridge.TestCharacter(0x41, 0x1e) == expected, message);
    private static void Key(uint key, bool down, nint foreground = default) => InputBridge.TestKeyboardAt(key, key == 0x41 ? 0x1eu : 0, down ? 0x100u : 0x101u, foreground);

    public static void CapsLock()
    {
        // SetKeyboardState はこのテストスレッドの状態だけを変更し、物理キーや LED を操作しない。
        var original = new byte[256];
        Require(GetKeyboardState(original), "元のキーボード状態を取得できる");
        using var recorder = new WindowsInput();
        try
        {
            foreach (var enabled in new[] { false, true })
            {
                var state = new byte[256]; state[0x14] = enabled ? (byte)1 : (byte)0;
                Require(SetKeyboardState(state), "開始前の Caps Lock 状態を設定できる");
                Require((GetKeyState(0x14) & 1) == state[0x14], "スレッドの Caps Lock 設定が反映される");
                Require(recorder.Start((uint)Environment.ProcessId) == 0, "Caps Lock 検証の記録を開始できる");
                Character(enabled ? 'A' : 'a', "記録開始前からの Caps Lock 状態を文字へ反映する");
                InputBridge.TestInitialKeyboardState(enabled, true);
                Key(0x14, true);
                Character(enabled ? 'A' : 'a', "開始時から押下中の Caps Lock のリピートでは再切替しない");
                Key(0x14, false);
                Key(0xA0, true);
                Character(enabled ? 'a' : 'A', "Shift と Caps Lock の組み合わせを反映する");
                Key(0xA0, false);
                Key(0x14, true);
                Character(enabled ? 'a' : 'A', "対象外アプリでの Caps Lock 押下も次の文字へ反映する");
                Key(0x14, true);
                Character(enabled ? 'a' : 'A', "Caps Lock のリピートでは再切替しない");
                Key(0x14, false);
                Character(enabled ? 'a' : 'A', "Caps Lock の解放ではトグル状態を維持する");
                Key(0x14, true); Key(0x14, false);
                Character(enabled ? 'A' : 'a', "再度の押下で元の Caps Lock 状態へ戻る");
                Require(recorder.Read(new InputEvent[32]) == 0, "対象外で更新したキーは記録しない");
                Require(recorder.Stop() == 0, "Caps Lock 検証の記録を停止できる");
            }
        }
        finally { recorder.Stop(); SetKeyboardState(original); }
        Console.WriteLine("Caps Lock の開始状態・切替・Shift 併用の回帰テストに成功しました。");
    }

    public static void ContentWindows()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var browserName = "Chrome_WidgetWin_CrerInput_" + suffix;
        var contentName = "Chrome_RenderWidgetHostHWND_CrerInput_" + suffix;
        var intermediateName = "Intermediate D3D Window_CrerInput_" + suffix;
        var names = new[] { browserName, contentName, intermediateName };
        foreach (var name in names)
        {
            var windowClass = new WindowClass { Proc = Proc, Name = name };
            Require(RegisterClassW(ref windowClass) != 0, "入力回帰テストのクラスを登録できる");
        }
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        var foreground = GetForegroundWindow();
        nint browser = 0, content = 0, tools = 0, child = 0, intermediate = 0, detached = 0, toolsD3d = 0;
        using var recorder = new WindowsInput();
        try
        {
            browser = CreateWindowExW(0x08000000, browserName, "CRER 入力回帰テスト", 0x10cf0000, 40, 40, 440, 300, 0, 0, 0, 0);
            content = CreateWindowExW(0, contentName, "ページ", 0x50000000, 0, 0, 210, 220, browser, 0, 0, 0);
            child = CreateWindowExW(0, "STATIC", "子コントロール", 0x50000000, 20, 20, 60, 40, content, 0, 0, 0);
            Require(browser != 0 && content != 0 && child != 0, "ページと子 HWND を作成できる");
            Require(recorder.Start((uint)Environment.ProcessId) == 0, "入力回帰テストの記録を開始できる");
            Require(recorder.GetContentRect().Status == 0, "ページの矩形を初期取得できる");
            // DevTools は記録開始後に追加されても、最初に選んだページを置き換えない。
            tools = CreateWindowExW(0, contentName, "DevTools", 0x50000000, 220, 0, 200, 220, browser, 0, 0, 0);
            Require(tools != 0 && GetWindowRect(content, out _), "同じクラスの DevTools HWND を作成できる");
            SetWindowPos(browser, new nint(-1), 0, 0, 0, 0, 0x13);
            var events = new InputEvent[32];
            void Empty(string label) => Require(recorder.Read(events) == 0, label);
            (int X, int Y) Center(nint window)
            {
                Require(GetWindowRect(window, out var rect), "物理矩形を取得できる");
                return ((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            }
            void Click((int X, int Y) point) { InputBridge.TestMouseAt(1, point.X, point.Y); InputBridge.TestMouseAt(2, point.X, point.Y); }
            void Pair(string label)
            {
                Require(recorder.Read(events) == 2 && events[0].Kind == 2 && events[1].Kind == 3, label);
            }
            Require(GetWindowRect(browser, out var browserRect), "ブラウザーの外枠を取得できる");
            var title = (X: browserRect.Left + 100, Y: browserRect.Top + 10);
            Click(title); Empty("タイトルバーのクリックを記録しない");
            InputBridge.TestMouseAt(1, title.X, title.Y);
            InputBridge.TestMouseAt(0, title.X + 10, title.Y, 10);
            InputBridge.TestMouseAt(2, title.X + 10, title.Y);
            Empty("タイトルバーのドラッグを記録しない");
            Click(Center(tools)); Empty("DevTools のクリックを記録しない");
            Click(Center(content)); Pair("ページのクリックは down/up を一組だけ記録する");
            Require(GetWindowRect(content, out var originalContentRect) && events[0].HasContentBounds
                && events[0].ContentX == originalContentRect.Left && events[0].ContentY == originalContentRect.Top
                && events[0].ContentWidth == originalContentRect.Right - originalContentRect.Left
                && events[0].ContentHeight == originalContentRect.Bottom - originalContentRect.Top,
                "入力イベントに採取時点のページ原点と寸法を保存する");
            var beforeMove = events[0];
            Click(Center(child)); Pair("ページ内の子 HWND でもクリックを記録する");
            var page = Center(content);
            InputBridge.TestMouseAt(1, title.X, title.Y); InputBridge.TestMouseAt(2, page.X, page.Y);
            Empty("タイトルバーからページへ戻った解放をクリックへ変換しない");

            SetFocus(content); Require(GetFocus() == content, "ページへスレッド内フォーカスを設定できる");
            Key(0x41, true, browser); Key(0x41, false, browser);
            Require(recorder.Read(events) == 2 && events[0].Kind == 9 && events[1].Kind == 8, "ページの文字とキー解放を記録する");

            foreach (var modifier in new uint[] { 0x10, 0x11, 0x12, 0x5B, 0xA0, 0xA2, 0xA4 })
            {
                Key(modifier, true, browser);
                Key(modifier, false);
                Key(0x41, true, browser); Key(0x41, false, browser);
                Require(recorder.Read(events) == 4 && events[0].Kind == 7 && events[1].Kind == 8
                    && events[1].Data >> 16 == modifier && events[2].Kind == 9 && events[3].Kind == 8,
                    "対象内で受理した修飾キーの対象外解放を保持し、次の文字に押下状態を残さない");
                Key(modifier, true); Key(modifier, false); Key(modifier, false);
                Empty("対象外で始まった修飾キーや重複解放は記録しない");
            }
            SetFocus(tools); Require(GetFocus() == tools, "DevTools へスレッド内フォーカスを設定できる");
            Key(0x41, true, browser); Key(0x41, false, browser);
            Empty("同じプロセスの DevTools へフォーカスしたキーを記録しない");

            // 独立した root と owner を持つ D3D HWND でも、物理ページ領域の入力は採用する。
            Require(GetWindowRect(content, out var rect), "D3D 用の物理ページ矩形を取得できる");
            intermediate = CreateWindowExW(0x08000000, intermediateName, "D3D", 0x90000000, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, browser, 0, 0, 0);
            Require(intermediate != 0, "D3D 中間 HWND を作成できる");
            SetWindowPos(intermediate, new nint(-1), 0, 0, 0, 0, 0x13);
            Click(page); Pair("owner を持つ D3D 中間 HWND でページ入力を保持する");
            DestroyWindow(intermediate); intermediate = 0;
            detached = CreateWindowExW(0x08000000, contentName, "独立 DevTools", 0x90000000, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, browser, 0, 0, 0);
            Require(detached != 0, "ページに重なる独立 DevTools を作成できる");
            SetWindowPos(detached, new nint(-1), 0, 0, 0, 0, 0x13);
            Click(page); Empty("ページに重なる独立 DevTools のクリックを除外する");
            SetFocus(detached);
            Key(0x41, true, detached); Key(0x41, false, detached);
            Empty("owner が一致しても独立 DevTools のキーを除外する");
            toolsD3d = CreateWindowExW(0, intermediateName, "DevTools 内 D3D", 0x50000000, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top, detached, 0, 0, 0);
            Require(toolsD3d != 0, "独立 DevTools 内の D3D 子 HWND を作成できる");
            Click(page); Empty("独立 DevTools 内の D3D 子 HWND のクリックも除外する");
            SetFocus(toolsD3d); Key(0x41, true, detached); Key(0x41, false, detached);
            Empty("独立 DevTools 内の D3D 子 HWND のキーも除外する");
            DestroyWindow(toolsD3d); toolsD3d = 0;
            DestroyWindow(detached); detached = 0;

            // 矩形の変更を追跡した後、ページの差替え中に DevTools だけ残っても採用しない。
            SetWindowPos(content, 0, 0, 0, 200, 180, 0x14);
            Require(recorder.GetContentRect().Rect.Width == 200, "ページのリサイズを追跡する");
            DestroyWindow(child); child = 0;
            DestroyWindow(content); content = 0;
            Require(recorder.GetContentRect().Status == 1169, "ページ消失中に DevTools を代わりに採用しない");
            Click(Center(tools)); Empty("ページ消失中も DevTools のクリックを除外する");
            SetFocus(tools); Key(0x41, true, browser); Key(0x41, false, browser);
            Empty("ページ消失中も DevTools のキーを除外する");
            SetWindowPos(browser, 0, 70, 70, 0, 0, 0x15);
            content = CreateWindowExW(0, contentName, "再生成ページ", 0x50000000, 0, 0, 200, 180, browser, 0, 0, 0);
            Require(content != 0 && recorder.GetContentRect().Status == 0, "移動後の同じ領域で再生成されたページを再取得する");
            Click(Center(content)); Pair("再生成されたページのクリックを採用する");
            Require(GetWindowRect(content, out var movedContentRect) && events[0].HasContentBounds
                && events[0].ContentX == movedContentRect.Left && events[0].ContentY == movedContentRect.Top
                && events[0].ContentWidth == 200 && events[0].ContentHeight == 180
                && events[0].ContentX - beforeMove.ContentX == 30 && events[0].ContentY - beforeMove.ContentY == 30
                && beforeMove.ContentX == originalContentRect.Left && beforeMove.ContentY == originalContentRect.Top,
                "ウィンドウの移動・ページの再生成後も各イベントの取得時原点を保持する");
            SetFocus(content); Key(0x41, true, browser); Key(0x41, false, browser);
            Require(recorder.Read(events) == 2 && events[0].Kind == 9 && events[1].Kind == 8, "再生成されたページのキーを採用する");
            DestroyWindow(browser); browser = content = tools = 0;
            browser = CreateWindowExW(0x08000000, browserName, "再生成ブラウザー", 0x10cf0000, 40, 40, 440, 300, 0, 0, 0, 0);
            content = CreateWindowExW(0, contentName, "再生成ページ", 0x50000000, 0, 0, 200, 180, browser, 0, 0, 0);
            Require(browser != 0 && content != 0, "トップレベルを含むページ HWND を再生成できる");
            SetWindowPos(browser, new nint(-1), 0, 0, 0, 0, 0x13);
            Click(Center(content)); Pair("トップレベル HWND 再生成後もページ入力を復旧する");
            Require(GetForegroundWindow() == foreground, "入力テストで利用者の前景ウィンドウを変更しない");
        }
        finally
        {
            recorder.Stop();
            foreach (var window in new[] { toolsD3d, detached, intermediate, tools, child, content, browser }) if (window != 0) DestroyWindow(window);
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
            foreach (var name in names) UnregisterClassW(name, 0);
        }
        Console.WriteLine("ページ・タイトルバー・DevTools・D3D の実 HWND 入力回帰テストに成功しました。");
    }
}
