using System.Diagnostics;
using System.Text.Json.Nodes;
using Crer;
using Crer.Windows;

var passed = 0;
var failed = 0;
var root = Path.GetFullPath(Path.Combine(".crer", "window-controller-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
string DirectoryFor(string name)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    return directory;
}
JsonObject Foreground(string directory) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "foreground.json")))!.AsObject();
JsonObject Startup(string directory) => Foreground(directory)["topmostStartup"]!.AsObject();
JsonArray Attempts(string directory) => Startup(directory)["attempts"]!.AsArray();
JsonObject Window(double opacity = 1) => new() { ["foreground"] = true, ["foreground_mode"] = "once", ["opacity"] = opacity };
Task Configure(WindowController controller, string directory, JsonObject? settings = null, bool shared = false, CancellationToken cancellation = default) =>
    controller.Configure(42, settings ?? Window(), CliOptions.Parse(["play", "unused.yaml"]), new ScenarioLogger("window-test"), directory, shared, "once", cancellation);
async Task Cancelled(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new Exception("キャンセルされた処理が正常終了しました");
}
async Task EnvironmentFailure(Func<Task> action)
{
    try { await action(); }
    catch (CrerException error) when (error.Code == 3 && error.Message.Contains("topmost", StringComparison.Ordinal)) { return; }
    throw new Exception("最前面化の失敗が環境エラーになりませんでした");
}

await Test("一時的なスタイル不一致から回復しフォーカス要求を繰り返さない", async () =>
{
    var directory = DirectoryFor("style-recovery");
    var fake = new FakeWindowInput(Outcomes.Mismatch(0x111), Outcomes.Success(0x222));
    using var controller = new WindowController(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));
    await Configure(controller, directory);
    var attempts = Attempts(directory);
    Check(attempts.Count == 2, "一時的な失敗を再試行しませんでした");
    Check(Json.Text(Startup(directory)["status"]) == "succeeded", "回復後の成功が記録されていません");
    Check(Json.Text(attempts[0]!["failureStage"]) == "style_mismatch", "最初の失敗原因が記録されていません");
    Check(attempts[0]!["setWindowPosSucceeded"]!.GetValue<bool>(), "API成功後のスタイル不一致を区別できません");
    Check(Json.Number(attempts[0]!["setWindowPosError"]) == 0, "API成功をエラー5と誤記録しました");
    Check(attempts[0]!["window"]!.ToJsonString() != attempts[1]!["window"]!.ToJsonString(), "再試行で選び直したHWNDが診断に残りません");
    Check(fake.FocusCalls == 1, "最前面化の再試行ごとにフォーカスを要求しました");
});

await Test("初期ウィンドウ未検出から別のHWNDで回復する", async () =>
{
    var directory = DirectoryFor("window-discovery");
    var fake = new FakeWindowInput(Outcomes.Missing(), Outcomes.Success(0x333));
    using var controller = new WindowController(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));
    await Configure(controller, directory);
    var attempts = Attempts(directory);
    Check(attempts.Count == 2 && fake.EnableCalls == 2, "ウィンドウの探索をやり直しませんでした");
    Check(Json.Text(attempts[0]!["failureStage"]) == "window_not_found", "ウィンドウ未検出を別の失敗と混同しました");
    Check(attempts[0]!["setWindowPosSucceeded"] is null, "未実行のSetWindowPosを実行済みと記録しました");
    Check(Json.Number(attempts[1]!["status"]) == 0, "次のHWNDで回復しませんでした");
    Check(fake.FocusCalls == 1, "ウィンドウ探索の再試行でフォーカス要求が増えました");
});

await Test("APIのアクセス拒否とAPI成功後のスタイル不一致を別々に記録する", async () =>
{
    var directory = DirectoryFor("failure-diagnostics");
    var fake = new FakeWindowInput(Outcomes.ApiFailure(0x444), Outcomes.Mismatch(0x555), Outcomes.Success(0x666));
    using var controller = new WindowController(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));
    await Configure(controller, directory);
    var attempts = Attempts(directory);
    Check(attempts.Count == 3, "失敗ごとの診断が保存されていません");
    var api = attempts[0]!; var style = attempts[1]!;
    Check(Json.Number(api["status"]) == 5 && Json.Number(style["status"]) == 5, "同じ終了状態の比較条件が崩れました");
    Check(Json.Text(api["failureStage"]) == "set_window_pos" && !api["setWindowPosSucceeded"]!.GetValue<bool>() && Json.Number(api["setWindowPosError"]) == 5,
        "API自体のアクセス拒否を診断できません");
    Check(Json.Text(style["failureStage"]) == "style_mismatch" && style["setWindowPosSucceeded"]!.GetValue<bool>() && Json.Number(style["setWindowPosError"]) == 0,
        "API成功後の検証失敗を診断できません");
    Check(fake.FocusCalls == 1, "診断用の再試行でフォーカスを繰り返しました");
});

await Test("永続的な失敗は複数回試行した後に環境エラーになり後始末でも診断を保持する", async () =>
{
    var directory = DirectoryFor("timeout");
    var fake = new FakeWindowInput { DefaultResult = Outcomes.Mismatch(0x777) };
    var controller = new WindowController(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(25));
    int startupAttempts;
    try
    {
        // 診断保存の負荷に余裕を持たせつつ、実装の期限処理が壊れた場合も検証を終了させます。
        await EnvironmentFailure(() => Configure(controller, directory, Window(.5)).WaitAsync(TimeSpan.FromSeconds(3)));
        startupAttempts = Attempts(directory).Count;
        Check(startupAttempts > 1, "最初の失敗で即座に終了しました");
        Check(Json.Text(Startup(directory)["status"]) == "timeout", "期限切れが記録されていません");
        Check(Json.Number(Startup(directory)["timeoutMs"]) == 1000, "設定した期限が記録されていません");
        Check(fake.FocusCalls == 1, "期限までの再試行でフォーカスを繰り返しました");
    }
    finally { controller.Dispose(); }
    Check(Attempts(directory).Count == startupAttempts && Json.Text(Startup(directory)["status"]) == "timeout", "後始末で起動時の失敗診断を失いました");
    Check(fake.DisableCalls == 1 && fake.RestoredWindows.SequenceEqual(new nint[] { FakeWindowInput.OriginalWindow }), "失敗後の最前面解除またはフォーカス復元が欠けています");
    Check(fake.Opacities.Last() == 255 && fake.DisposeCalls == 1, "失敗後の不透明度復元またはリソース解放が欠けています");
});

await Test("再試行の待機中にキャンセルすると次の待機満了を待たず終了する", async () =>
{
    var directory = DirectoryFor("cancel-wait");
    var fake = new FakeWindowInput { DefaultResult = Outcomes.Mismatch(0x888) };
    using var controller = new WindowController(fake, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
    using var cancellation = new CancellationTokenSource();
    var configuring = Configure(controller, directory, cancellation: cancellation.Token);
    await fake.FirstTopmost.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var elapsed = Stopwatch.StartNew();
    cancellation.Cancel();
    await Cancelled(() => configuring.WaitAsync(TimeSpan.FromMilliseconds(700)));
    Check(elapsed.ElapsedMilliseconds < 700, "キャンセル後も再試行間隔の満了を待ちました");
    Check(fake.EnableCalls == 1, "キャンセル後に最前面化を再要求しました");
    Check(Json.Text(Startup(directory)["status"]) == "cancelled", "キャンセルを起動診断に記録しませんでした");
});

await Test("事前キャンセルではウィンドウ設定APIを呼ばない", async () =>
{
    var directory = DirectoryFor("cancel-before-start");
    var fake = new FakeWindowInput();
    using var controller = new WindowController(fake);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Cancelled(() => Configure(controller, directory, Window(.5), cancellation: cancellation.Token));
    Check(fake.EnableCalls == 0 && fake.DisableCalls == 0 && fake.FocusCalls == 0 && fake.Opacities.Count == 0,
        "キャンセル済みの起動からウィンドウ設定APIを呼びました");
});

await Test("同じ共有セッションの再構成ではfocus onceを保持する", async () =>
{
    var firstDirectory = DirectoryFor("shared-first"); var nextDirectory = DirectoryFor("shared-next");
    var fake = new FakeWindowInput(Outcomes.Missing(), Outcomes.Success(), Outcomes.Mismatch(0x999), Outcomes.Success(0xaaa));
    using var controller = new WindowController(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));
    await Configure(controller, firstDirectory, shared: true);
    await Configure(controller, nextDirectory, shared: true);
    Check(fake.EnableCalls == 4 && fake.FocusCalls == 1, "共有セッションの再利用でフォーカスを取り直しました");
    Check(Startup(firstDirectory)["focusRequested"]!.GetValue<bool>(), "初回フォーカス要求を記録しませんでした");
    Check(!Startup(nextDirectory)["focusRequested"]!.GetValue<bool>(), "再利用時にもフォーカスを要求したと記録しました");
    Check(Attempts(firstDirectory).Count == 2 && Attempts(nextDirectory).Count == 2, "前のシナリオの試行履歴と混在しました");
});

await Test("once成功後はstep開始と不透明度監視で最前面化やフォーカスを再要求しない", async () =>
{
    var directory = DirectoryFor("once-after-success");
    var fake = new FakeWindowInput();
    var controller = new WindowController(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5));
    try
    {
        await Configure(controller, directory, Window(.5), shared: true);
        controller.BeforeStep(); controller.BeforeStep();
        // 不透明度の監視が実際に動いたことを通知で確認し、固定時間の待機を避けます。
        await fake.OpacityMaintenance.Task.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Pause();
        Check(fake.Opacities.Count >= 2, "不透明度監視が実行されませんでした");
        Check(fake.EnableCalls == 1 && fake.FocusCalls == 1, "once成功後に最前面化またはフォーカスを再要求しました");
    }
    finally { controller.Dispose(); }
    Check(fake.DisableCalls == 1 && fake.RestoredWindows.SequenceEqual(new nint[] { FakeWindowInput.OriginalWindow }), "終了時の最前面解除または元ウィンドウへの復元が欠けています");
    Check(fake.Opacities.Last() == 255 && fake.DisposeCalls == 1, "不透明度の復元またはリソース解放が欠けています");
    Check(Json.Text(Startup(directory)["status"]) == "succeeded" && Attempts(directory).Count == 1, "後始末で起動成功の診断が変わりました");
});

Console.WriteLine($"{passed} passed, {failed} failed; evidence: {root}");
return failed == 0 ? 0 : 1;

static class Outcomes
{
    internal static TopmostResult Success(nint window = 0x222) => new(0, "none", window, true, 0, 8, 0);
    internal static TopmostResult Mismatch(nint window) => new(5, "style_mismatch", window, true, 0, 0, 0);
    internal static TopmostResult Missing() => new(1168, "window_not_found", 0, null, 0, null, 0);
    internal static TopmostResult ApiFailure(nint window) => new(5, "set_window_pos", window, false, 5, null, 0);
}

sealed class FakeWindowInput(params TopmostResult[] results) : IWindowInput
{
    internal static readonly nint OriginalWindow = 0x123;
    private readonly object gate = new();
    private readonly Queue<TopmostResult> pending = new(results);
    private readonly List<(uint Pid, bool Enabled)> topmost = [];
    private readonly List<uint> opacity = [];
    private readonly List<nint> restored = [];
    private int focusCalls, disposeCalls;
    public TopmostResult DefaultResult { get; init; } = Outcomes.Success();
    public TaskCompletionSource FirstTopmost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OpacityMaintenance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int EnableCalls { get { lock (gate) return topmost.Count(call => call.Enabled); } }
    public int DisableCalls { get { lock (gate) return topmost.Count(call => !call.Enabled); } }
    public int FocusCalls { get { lock (gate) return focusCalls; } }
    public int DisposeCalls { get { lock (gate) return disposeCalls; } }
    public IReadOnlyList<uint> Opacities { get { lock (gate) return opacity.ToArray(); } }
    public IReadOnlyList<nint> RestoredWindows { get { lock (gate) return restored.ToArray(); } }
    public nint GetForegroundWindow() => OriginalWindow;
    public bool IsProcessForeground(uint pid) => true;
    public int RestoreForegroundWindow(nint window) { lock (gate) restored.Add(window); return 0; }
    public int ForegroundProcessWindow(uint pid) { lock (gate) focusCalls++; return 0; }
    public TopmostResult SetProcessTopmostDetailed(uint pid, bool enabled)
    {
        lock (gate)
        {
            topmost.Add((pid, enabled));
            if (!enabled) return Outcomes.Success();
            var result = pending.TryDequeue(out var next) ? next : DefaultResult;
            FirstTopmost.TrySetResult();
            return result;
        }
    }
    public int SetProcessOpacity(uint pid, uint alpha)
    {
        lock (gate)
        {
            opacity.Add(alpha);
            if (opacity.Count >= 2) OpacityMaintenance.TrySetResult();
        }
        return 0;
    }
    public void Dispose() { lock (gate) disposeCalls++; }
}
