using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Crer.Windows;

internal static class BrowserInputRegressionTests
{
    private delegate bool EnumProc(nint window, nint argument);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint argument);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint argument);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, char[] name, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static async Task Run(string chrome)
    {
        var root = Path.GetFullPath(Path.Combine(".crer", "windows-browser-input", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var profile = Path.Combine(root, "profile");
        var start = new ProcessStartInfo(Path.GetFullPath(chrome)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "--user-data-dir=" + profile,
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--force-device-scale-factor=1", "--app=data:text/html,<input autofocus id=test>" }) start.ArgumentList.Add(argument);
        using var browser = BackgroundProcessLauncher.Start(start);
        var stdout = browser.StandardOutput.ReadToEndAsync(); var stderr = browser.StandardError.ReadToEndAsync();
        using var recorder = new WindowsInput();
        using var socket = new ClientWebSocket();
        var id = 0;
        var foreground = GetForegroundWindow();
        async Task<JsonObject> Call(string method, JsonObject? parameters = null)
        {
            var current = ++id;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var request = new JsonObject { ["id"] = current, ["method"] = method, ["params"] = parameters ?? new() };
            await socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, deadline.Token);
            var bytes = new byte[65536];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do { part = await socket.ReceiveAsync(bytes, deadline.Token); message.Write(bytes, 0, part.Count); } while (!part.EndOfMessage);
                var response = JsonNode.Parse(message.ToArray())!.AsObject();
                if (response["id"]?.GetValue<int>() != current) continue;
                if (response["error"] != null) throw new Exception(response["error"]!.ToJsonString());
                return response["result"]!.AsObject();
            }
        }
        void Require(bool condition, string message) { if (!condition) throw new Exception(message + "; 証跡: " + root); }
        try
        {
            var activePort = Path.Combine(profile, "DevToolsActivePort");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(activePort) && !browser.Process.HasExited && watch.ElapsedMilliseconds < 10000) await Task.Delay(50);
            Require(File.Exists(activePort), "CfT の CDP ポートを取得できる (終了コード " + (browser.Process.HasExited ? browser.Process.ExitCode : null) + ")");
            var lines = await File.ReadAllLinesAsync(activePort);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var targets = JsonNode.Parse(await http.GetStringAsync("http://127.0.0.1:" + lines[0] + "/json/list"))!.AsArray();
            var target = targets.OfType<JsonObject>().First(item => item["type"]?.GetValue<string>() == "page");
            await socket.ConnectAsync(new Uri(target["webSocketDebuggerUrl"]!.GetValue<string>()), CancellationToken.None);
            await Call("Runtime.evaluate", new() { ["expression"] = "document.getElementById('test').focus()" });
            nint window = 0;
            EnumWindows((candidate, _) =>
            {
                GetWindowThreadProcessId(candidate, out var pid);
                var name = new char[256]; GetClassNameW(candidate, name, name.Length);
                if (pid != browser.Process.Id || !IsWindowVisible(candidate) || !new string(name).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return true;
                window = candidate; return false;
            }, 0);
            Require(window != 0, "実 CfT のウィンドウを取得できる");
            Require(recorder.SetProcessTopmostOnly((uint)browser.Process.Id, true) == 0, "前景を変えずに CfT を観測できる");
            Require(recorder.Start((uint)browser.Process.Id) == 0, "実 CfT の記録を開始できる");
            await Task.Delay(200);
            var bounds = recorder.GetContentRect();
            Require(bounds.Status == 0, "実 CfT のページ HWND を取得できる");
            var events = new InputEvent[64];
            var x = bounds.Rect.X + 100; var y = bounds.Rect.Y + 100;
            InputBridge.TestMouseAt(1, x, y); InputBridge.TestMouseAt(2, x, y);
            var count = recorder.Read(events);
            Require(count == 2 && events[0].Kind == 2 && events[1].Kind == 3, "実 CfT のページクリックを一組だけ採用する");
            Require(GetWindowRect(window, out var frame), "実 CfT の外枠を取得できる");
            InputBridge.TestMouseAt(1, frame.Left + 100, frame.Top + 10); InputBridge.TestMouseAt(2, frame.Left + 100, frame.Top + 10);
            Require(recorder.Read(events) == 0, "実 CfT のタイトルバークリックを除外する");

            // 所有する CfT のキューに接続し、物理キーと前景を変えずページへフォーカスを与える。
            nint content = 0;
            EnumChildWindows(window, (candidate, _) =>
            {
                var name = new char[256]; GetClassNameW(candidate, name, name.Length);
                if (!new string(name).StartsWith("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal) || !IsWindowVisible(candidate)) return true;
                content = candidate; return false;
            }, 0);
            Require(content != 0, "実 CfT のページ HWND を特定できる");
            var currentThread = GetCurrentThreadId();
            var browserThread = GetWindowThreadProcessId(window, out _);
            Require(AttachThreadInput(currentThread, browserThread, true), "所有する CfT のキューへ接続できる");
            try { SetFocus(content); }
            finally { AttachThreadInput(currentThread, browserThread, false); }
            var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
            Require(GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info), "実 CfT のフォーカスを取得できる");
            var focusClass = new char[256]; GetClassNameW(info.Focus, focusClass, focusClass.Length);
            await File.WriteAllTextAsync(Path.Combine(root, "focus.txt"), $"focus={info.Focus}; class={new string(focusClass).TrimEnd('\0')}; content={bounds.Rect}\n");
            InputBridge.TestKeyboardAt(0x41, 0x1e, 0x100, window); InputBridge.TestKeyboardAt(0x41, 0x1e, 0x101, window);
            count = recorder.Read(events);
            Require(count == 2 && events[0].Kind == 9 && events[1].Kind == 8, "実 CfT のページ文字入力を採用する");

            await Call("Page.navigate", new() { ["url"] = "data:text/html,<input autofocus id=next>" });
            await Task.Delay(300);
            bounds = recorder.GetContentRect();
            Require(bounds.Status == 0, "ページ遷移後もコンテンツ HWND を取得できる");
            x = bounds.Rect.X + 100; y = bounds.Rect.Y + 100;
            InputBridge.TestMouseAt(1, x, y); InputBridge.TestMouseAt(2, x, y);
            count = recorder.Read(events);
            Require(count == 2 && events[0].Kind == 2 && events[1].Kind == 3, "ページ遷移後もページクリックを採用する");
            Require(GetForegroundWindow() == foreground, "実 CfT 検証で利用者の前景を変えない");
            Console.WriteLine("実 CfT のページ入力・タイトルバー除外・ページ遷移の回帰テストに成功しました。証跡: " + root);
        }
        finally
        {
            recorder.Stop();
            recorder.SetProcessTopmostOnly((uint)browser.Process.Id, false);
            if (socket.State == WebSocketState.Open) try { await Call("Browser.close"); } catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
            if (!browser.Process.HasExited) { browser.Process.Kill(true); await browser.Process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(root, "chrome.stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(root, "chrome.stderr.log"), await stderr);
        }
    }
}
