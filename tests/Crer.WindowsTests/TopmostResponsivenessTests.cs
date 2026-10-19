using System.Diagnostics;
using System.Runtime.InteropServices;
using Crer.Windows;

internal static class TopmostResponsivenessTests
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run()
    {
        using (var window = new PausedWindow())
        {
            var watch = Stopwatch.StartNew();
            var request = Task.Run(() => InputBridge.ApplyTopmost(window.Handle, true, default, TimeSpan.FromMilliseconds(150)));
            var unrelated = Task.Run(InputBridge.GetForegroundWindowHandle);
            await unrelated.WaitAsync(TimeSpan.FromSeconds(1));
            var result = await request.WaitAsync(TimeSpan.FromSeconds(1));
            Require(result.Status == 1460 && result.FailureStage == "confirmation_timeout", "非応答 UI の未反映を成功や即時不一致へ変換しない");
            Require(watch.Elapsed < TimeSpan.FromSeconds(1), "非応答 UI の復帰を無期限に待たない");
        }
        using (var window = new PausedWindow())
        using (var cancelled = new CancellationTokenSource())
        {
            var request = Task.Run(() => InputBridge.ApplyTopmost(window.Handle, true, cancelled.Token, TimeSpan.FromSeconds(5)));
            cancelled.CancelAfter(50);
            try { await request.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("属性反映待ちの取消しを無視した"); }
            catch (OperationCanceledException error) { Require(error.CancellationToken == cancelled.Token, "元の取消し token を維持する"); }
        }
        using (var window = new PausedWindow())
        {
            var request = Task.Run(() => InputBridge.ApplyTopmost(window.Handle, true, default, TimeSpan.FromSeconds(2)));
            await Task.Delay(50);
            Require(!request.IsCompleted, "非同期要求を送っただけで属性反映済みと扱わない");
            window.Resume();
            var result = await request.WaitAsync(TimeSpan.FromSeconds(2));
            Require(result.Status == 0 && (result.ExtendedStyle & 8) == 8, "別 UI スレッドへの要求の反映を確認してから成功を返す");
            Require(InputBridge.ApplyTopmost(window.Handle, false).Status == 0, "非同期の最前面解除も反映を確認する");
        }
        Console.WriteLine("非応答 UI の最前面化・取消し・他ウィンドウ操作・遅延反映の回帰テストに成功しました。");
    }

    // 自分で作成する非表示ウィンドウだけを使用し、利用者の前景や通常のアプリを変更しない。
    private sealed class PausedWindow : IDisposable
    {
        private readonly ManualResetEventSlim resume = new();
        private readonly ManualResetEventSlim finish = new();
        private readonly Thread owner;
        public nint Handle { get; }

        public PausedWindow()
        {
            var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner = new Thread(() =>
            {
                var window = Native.CreateWindowExW(0, "STATIC", "CRER 非応答 UI 回帰テスト", 0, 0, 0, 100, 100, 0, 0, 0, 0);
                if (window == 0) { ready.SetException(new Exception("非表示の調査用ウィンドウを作成できない")); return; }
                ready.SetResult(window);
                resume.Wait();
                while (!finish.IsSet)
                {
                    while (Native.PeekMessageW(out var message, 0, 0, 0, 1))
                    {
                        Native.TranslateMessage(ref message);
                        Native.DispatchMessageW(ref message);
                    }
                    Thread.Sleep(1);
                }
                Native.DestroyWindow(window);
            }) { IsBackground = true };
            owner.Start();
            Handle = ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }

        public void Resume() => resume.Set();
        public void Dispose()
        {
            resume.Set(); finish.Set();
            Require(owner.Join(TimeSpan.FromSeconds(3)), "調査用 UI スレッドを終了できる");
            resume.Dispose(); finish.Dispose();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowExW(uint extended, string type, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern nint DispatchMessageW(ref Message message);
    }
}
