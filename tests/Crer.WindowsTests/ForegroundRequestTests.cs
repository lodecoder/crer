using System.Diagnostics;
using System.Runtime.InteropServices;
using Crer.Windows;

internal static class ForegroundRequestTests
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
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run()
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var name = "Chrome_WidgetWin_CrerForeground_" + Guid.NewGuid().ToString("N");
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            var windowClass = new WindowClass { Proc = Proc, Name = name };
            nint browser = 0;
            var registered = false;
            try
            {
                Require(RegisterClassW(ref windowClass) != 0, "非応答ウィンドウのクラスを登録できる");
                registered = true;
                browser = CreateWindowExW(0x08000000, name, "CRER foreground regression test", 0x10cf0000, 30, 30, 240, 120, 0, 0, 0, 0);
                Require(browser != 0, "別スレッドに前景要求の対象ウィンドウを作成できる");
                ready.Set();
                // UI スレッドがメッセージを処理しない間にも、前景要求は応答待ちにならないことを確認する。
                release.Wait();
            }
            catch (Exception error) { ownerFailure = error; ready.Set(); }
            finally
            {
                if (browser != 0) DestroyWindow(browser);
                if (registered) UnregisterClassW(name, 0);
            }
        }) { IsBackground = true };
        var foreground = GetForegroundWindow();
        Task<int>? request = null;
        var returnedWhileUnresponsive = false;
        var elapsed = Stopwatch.StartNew();
        owner.Start();
        try
        {
            Require(ready.Wait(TimeSpan.FromSeconds(5)), "非応答ウィンドウの作成を待機できる");
            if (ownerFailure is not null) throw ownerFailure;
            elapsed.Restart();
            request = Task.Run(() => InputBridge.ForegroundProcessWindow((uint)Environment.ProcessId));
            returnedWhileUnresponsive = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(2))) == request;
            elapsed.Stop();
        }
        finally
        {
            // 失敗時にも UI スレッドを解放し、同期待ちを発生させた実装でも検証プロセスを残さない。
            release.Set();
            Require(owner.Join(TimeSpan.FromSeconds(5)), "非応答ウィンドウのスレッドを終了できる");
        }
        if (request is not null) await request.WaitAsync(TimeSpan.FromSeconds(5));
        InputBridge.RestoreForegroundWindow(foreground);
        Require(returnedWhileUnresponsive, "前景要求は対象 UI の非応答に巻き込まれず 2 秒以内に戻る");
        Console.WriteLine($"非応答ウィンドウへの前景要求テストに成功しました（{elapsed.ElapsedMilliseconds} ms）。");
    }
}
