using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Crer;

var passed = 0;
var failed = 0;
var evidence = Path.GetFullPath(Path.Combine(".crer", "playback-executor-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(evidence);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> body, int timeoutSeconds = 5)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
async Task Cancelled(Task<int> task, CancellationToken expected)
{
    try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (OperationCanceledException error)
    {
        Check(error.CancellationToken == expected, "利用者・worker の取消し token が置き換わりました");
        return;
    }
    throw new Exception("取消した実行が正常終了しました");
}
JsonObject Log(string message) => new() { ["do"] = "log", ["message"] = message };
JsonObject Wait() => new() { ["do"] = "wait_for", ["state"] = "visible", ["locator_hint"] = new JsonObject { ["role"] = "status", ["text"] = "ready" } };
Scenario ScenarioFor(JsonArray steps, double actionMs = 10000, bool continueTimeout = false) => Scenario.Validate(new JsonObject
{
    ["version"] = 1, ["name"] = "executor-regression",
    ["browser"] = new JsonObject { ["initial_url"] = "http://127.0.0.1/" },
    ["playback"] = new JsonObject { ["timeouts"] = new JsonObject { ["action_ms"] = actionMs },
        ["on_failure"] = new JsonObject { ["timeout"] = continueTimeout ? "continue" : "abort" } },
    ["steps"] = steps,
});

await Test("空の巨大 repeat を途中取消しでき、後続 step を実行しない", async () =>
{
    await using var run = await ExecutorRun.Create(evidence, new ControlledSocket());
    using var cancellation = new CancellationTokenSource();
    var scenario = ScenarioFor(new JsonArray(new JsonObject { ["do"] = "repeat", ["count"] = 1e18, ["steps"] = new JsonArray() }, Log("unreachable")));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    run.BeforeStep(_ => { entered.TrySetResult(); return Task.CompletedTask; });
    // 空の反復が同期完了しても、テスト側の取消し処理まで止めない。
    var execution = Task.Run(() => run.Execute(scenario, cancellation.Token));
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await Task.Delay(50);
    cancellation.Cancel();
    await Cancelled(execution, cancellation.Token);
    Check(!run.Records.Any(record => Json.Text(record["message"]) == "unreachable"), "取消し後に兄弟 step を実行しました");
    Check(run.Records.Any(record => Json.Text(record["kind"]) == "interrupted"), "中断の診断がありません");
    Check(run.Cdp.PendingCount == 0, "中断した反復が CDP 応答待ちを残しました");
});

await Test("repeat の count 0 と有限の空反復は後続 step へ進む", async () =>
{
    await using var run = await ExecutorRun.Create(evidence, new ControlledSocket());
    var code = await run.Execute(ScenarioFor(new JsonArray(
        new JsonObject { ["do"] = "repeat", ["count"] = 0, ["steps"] = new JsonArray(new JsonObject { ["do"] = "fail", ["message"] = "unreachable" }) },
        new JsonObject { ["do"] = "repeat", ["count"] = 3, ["steps"] = new JsonArray() }, Log("finished"))), default);
    Check(code == 0 && run.Records.Any(record => Json.Text(record["message"]) == "finished"), "通常の反復を変更しました");
});

await Test("条件が期限後に成立しても timeout になり続行方針を適用する", async () =>
{
    var socket = new ControlledSocket { ConditionDelay = TimeSpan.FromSeconds(3) };
    await using var run = await ExecutorRun.Create(evidence, socket);
    var watch = Stopwatch.StartNew();
    var code = await run.Execute(ScenarioFor(new JsonArray(Wait(), Log("after-timeout")), 50, true), default).WaitAsync(TimeSpan.FromSeconds(2));
    Check(code == 4 && run.Failures.Any(value => Json.Text(value).Contains(":timeout:", StringComparison.Ordinal)), "期限後の成立を成功としました");
    Check(run.Records.Any(record => Json.Text(record["message"]) == "after-timeout"), "timeout の continue を適用しませんでした");
    Check(watch.Elapsed < socket.ConditionDelay, "期限超過後も条件応答を待ちました");
    Check(run.Cdp.PendingCount == 0 && !run.Cdp.Disconnected.IsCompleted, "待機期限で CDP 接続を破棄またはリークしました");
});

await Test("遅れて届いた条件応答は後続の CDP 応答へ混入しない", async () =>
{
    var socket = new ControlledSocket { ConditionDelay = TimeSpan.FromMilliseconds(200) };
    await using var run = await ExecutorRun.Create(evidence, socket);
    Check(await run.Execute(ScenarioFor(new JsonArray(Wait()), 30), default) == 4, "短い期限が成功しました");
    await socket.ConditionReplied.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var next = await run.Cdp.Call("Probe.after-timeout");
    Check(Json.Text(next["method"]) == "Probe.after-timeout" && run.Cdp.PendingCount == 0, "遅延応答を別の要求へ割り当てました");
});

await Test("期限内の成立は成功し、大きな有限期限もタイマー上限で拒否しない", async () =>
{
    foreach (var limit in new[] { 1000d, 1e20 })
    {
        await using var run = await ExecutorRun.Create(evidence, new ControlledSocket());
        Check(await run.Execute(ScenarioFor(new JsonArray(Wait()), limit), default) == 0, "期限内の成立を拒否しました");
        Check(run.Failures.Count == 0, "成功した待機に失敗を記録しました");
    }
});

await Test("action_ms 0 は条件照会せず timeout として続行する", async () =>
{
    var socket = new ControlledSocket();
    await using var run = await ExecutorRun.Create(evidence, socket);
    var code = await run.Execute(ScenarioFor(new JsonArray(Wait(), Log("zero-timeout")), 0, true), default);
    Check(code == 4 && socket.ConditionCalls == 0 && run.Records.Any(record => Json.Text(record["message"]) == "zero-timeout"), "期限 0 の挙動を変更しました");
});

await Test("条件応答の待機中も利用者・worker の取消し理由を保持する", async () =>
{
    var socket = new ControlledSocket { ConditionDelay = TimeSpan.FromSeconds(3) };
    await using var run = await ExecutorRun.Create(evidence, socket);
    using var cancellation = new CancellationTokenSource();
    var execution = run.Execute(ScenarioFor(new JsonArray(Wait(), Log("unreachable")), 10000), cancellation.Token);
    await socket.ConditionRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
    cancellation.Cancel();
    await Cancelled(execution, cancellation.Token);
    Check(run.Records.Any(record => Json.Text(record["kind"]) == "interrupted"), "取消しをステップ timeout に変換しました");
    Check(!run.Records.Any(record => Json.Text(record["message"]) == "unreachable") && run.Cdp.PendingCount == 0, "取消し後の実行や CDP 待機が残りました");
});

await Test("条件照会中の CDP 接続断は環境エラーのまま伝播する", async () =>
{
    await using var run = await ExecutorRun.Create(evidence, new ControlledSocket { DisconnectCondition = true });
    try { await run.Execute(ScenarioFor(new JsonArray(Wait()), 1000), default); throw new Exception("接続断が成功しました"); }
    catch (CrerException error) { Check(error.Code == 3, "接続断を続行可能なステップ失敗に変換しました"); }
    Check(run.Records.Any(record => Json.Text(record["kind"]) == "environment") && run.Cdp.PendingCount == 0, "接続断の診断または後始末がありません");
});

await Test("文書切替中の一時的な評価エラーを待機期限内で再試行する", async () =>
{
    foreach (var message in new[] { "Execution context was destroyed.", "Cannot find context with specified id", "Cannot find default execution context" })
    {
        var socket = new ControlledSocket { ContextError = message, ContextFailures = 1 };
        await using var run = await ExecutorRun.Create(evidence, socket);
        Check(await run.Execute(ScenarioFor(new JsonArray(Wait()), 1000), default) == 0, "文書切替中の待機を失敗にした");
        Check(socket.ConditionCalls == 2 && run.Failures.Count == 0, "遷移後に条件を確認しなかった");
    }
});

await Test("文書切替が続いても元の待機期限で失敗し続行方針を保つ", async () =>
{
    var socket = new ControlledSocket { ContextError = "Execution context was destroyed.", ContextFailures = int.MaxValue };
    await using var run = await ExecutorRun.Create(evidence, socket);
    Check(await run.Execute(ScenarioFor(new JsonArray(Wait(), Log("after-changing-document")), 80, true), default) == 4,
        "一時エラーの再試行で期限を延長した");
    Check(run.Failures.Any(item => Json.Text(item).Contains(":timeout:")) && run.Records.Any(record => Json.Text(record["message"]) == "after-changing-document"),
        "一時エラーを timeout と区別してしまった");
});

await Test("十秒を超える条件応答も指定した action_ms の期限内なら成立を受け入れる", async () =>
{
    var socket = new ControlledSocket { ConditionDelay = TimeSpan.FromMilliseconds(10300) };
    await using var run = await ExecutorRun.Create(evidence, socket);
    Check(await run.Execute(ScenarioFor(new JsonArray(Wait()), 15000), default) == 0, "CDP の既定十秒で待機を中止した");
    Check(socket.ConditionCalls == 1 && run.Failures.Count == 0, "期限内の条件成立を失敗として扱った");
}, 18);

await Test("深い制御ステップの中断でも診断は一度だけ短い予算で終える", async () =>
{
    var socket = new ControlledSocket { ConditionDelay = TimeSpan.FromSeconds(3), ScreenshotDelay = TimeSpan.FromSeconds(3), UrlDelay = TimeSpan.FromSeconds(3) };
    await using var run = await ExecutorRun.Create(evidence, socket);
    using var cancellation = new CancellationTokenSource();
    JsonObject Repeat(JsonObject child) => new() { ["do"] = "repeat", ["count"] = 1, ["steps"] = new JsonArray(child) };
    var task = run.Execute(ScenarioFor(new JsonArray(Repeat(Repeat(Wait())), Log("unreachable"))), cancellation.Token);
    await socket.ConditionRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var watch = Stopwatch.StartNew(); cancellation.Cancel();
    await Cancelled(task, cancellation.Token);
    Check(watch.Elapsed < TimeSpan.FromSeconds(1), "無応答の renderer の診断で中断を待たせた");
    Check(socket.ScreenshotCalls == 1 && socket.UrlCalls == 0, "親ごとに画像取得や URL 照会を繰り返した");
    Check(run.Records.Count(record => Json.Text(record["kind"]) == "interrupted") == 3, "親子の中断記録が欠落した");
});

await Test("ステップの URL はトップ文書の最後の遷移を使い診断の CDP 照会を行わない", async () =>
{
    var socket = new ControlledSocket();
    await using var run = await ExecutorRun.Create(evidence, socket);
    void Navigation(string id, string url, string? parent = null)
    {
        var frame = new JsonObject { ["id"] = id, ["url"] = url };
        if (parent != null) frame["parentId"] = parent;
        run.Observe("Page.frameNavigated", new JsonObject { ["frame"] = frame });
    }
    Navigation("main", "http://127.0.0.1/first");
    Navigation("child", "http://127.0.0.1/child", "main");
    // 遷移前の文書を見た状態応答が遅れて到着しても、遷移イベントの URL を優先する。
    await run.Execute(ScenarioFor(new JsonArray(Wait(), Log("first"))), default);
    run.Observe("Page.navigatedWithinDocument", new JsonObject { ["frameId"] = "child", ["url"] = "http://127.0.0.1/wrong" });
    run.Observe("Page.navigatedWithinDocument", new JsonObject { ["frameId"] = "main", ["url"] = "http://127.0.0.1/first#next" });
    await run.Execute(ScenarioFor(new JsonArray(Log("second"))), default);
    Check(Json.Text(run.Records.Single(record => Json.Text(record["message"]) == "first")["url"]) == "http://127.0.0.1/first"
        && Json.Text(run.Records.Single(record => Json.Text(record["message"]) == "second")["url"]) == "http://127.0.0.1/first#next",
        "子 frame や古い状態応答と主文書の URL を混同した");
    Check(socket.UrlCalls == 0, "診断ごとに renderer の応答を待った");
});

Console.WriteLine($"{passed} passed, {failed} failed; evidence: {evidence}");
return failed == 0 ? 0 : 1;

sealed class ExecutorRun : IAsyncDisposable
{
    private readonly Playback playback = new();
    private readonly string directory;
    public Cdp Cdp { get; }
    public JsonArray Failures { get; } = new();
    public JsonObject[] Records => File.ReadLines(Path.Combine(directory, "steps.ndjson")).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    private ExecutorRun(Cdp cdp, string directory)
    {
        Cdp = cdp; this.directory = directory;
        Set("cdp", cdp); Set("session", "page"); Set("absoluteRunDir", directory);
        Set("metadata", new JsonObject { ["seed"] = "42" });
    }
    public static async Task<ExecutorRun> Create(string evidence, ControlledSocket socket)
    {
        // 通信境界だけを制御し、製品の step 実行・CDP 要求管理・成果物生成をそのまま通す。
        var cdp = (Cdp)Activator.CreateInstance(typeof(Cdp), BindingFlags.Instance | BindingFlags.NonPublic, null, [socket], null)!;
        await cdp.Open("ws://127.0.0.1/executor-test", default);
        var directory = Path.Combine(evidence, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new(cdp, directory);
    }
    private void Set(string field, object value) => typeof(Playback).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playback, value);
    public void BeforeStep(Func<string, Task> callback) => Set("beforeStep", callback);
    public void Observe(string method, JsonObject parameters) => typeof(Playback).GetMethod("OnNetworkEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(playback, [method, parameters, "page"]);
    public Task<int> Execute(Scenario scenario, CancellationToken token)
    {
        Set("token", token);
        return (Task<int>)typeof(Playback).GetMethod("ExecuteSteps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(playback, [scenario, CliOptions.Parse(["play", "unused.yaml"]), Failures])!;
    }
    public ValueTask DisposeAsync() => Cdp.DisposeAsync();
}

sealed class ControlledSocket : WebSocket
{
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> replies = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private readonly CancellationTokenSource lifetime = new();
    private WebSocketState state = WebSocketState.Open;
    private int conditionCalls;
    public TimeSpan ConditionDelay { get; init; }
    public bool DisconnectCondition { get; init; }
    public string? ContextError { get; init; }
    public int ContextFailures { get; init; }
    public TimeSpan ScreenshotDelay { get; init; }
    public TimeSpan UrlDelay { get; init; }
    public int ScreenshotCalls, UrlCalls;
    public int ConditionCalls => Volatile.Read(ref conditionCalls);
    public TaskCompletionSource ConditionRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ConditionReplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => state;
    public override void Abort() { state = WebSocketState.Aborted; }
    public override void Dispose() { state = WebSocketState.Closed; lifetime.Cancel(); lifetime.Dispose(); }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
    {
        var reply = await replies.Reader.ReadAsync(token);
        reply.Data.CopyTo(buffer.AsSpan());
        return new(reply.Data.Length, reply.Type, true);
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
    {
        var request = JsonNode.Parse(buffer.AsSpan())!.AsObject();
        var condition = Json.Text(request["params"]?["expression"]).Contains("const hint", StringComparison.Ordinal);
        if (condition) { Interlocked.Increment(ref conditionCalls); ConditionRequested.TrySetResult(); }
        _ = Respond(request, condition, lifetime.Token);
        return Task.CompletedTask;
    }
    private async Task Respond(JsonObject request, bool condition, CancellationToken token)
    {
        try
        {
            if (condition && DisconnectCondition)
            {
                await replies.Writer.WriteAsync(([], WebSocketMessageType.Close), token);
                return;
            }
            if (condition) await Task.Delay(ConditionDelay, token);
            if (condition && ConditionCalls <= ContextFailures)
            {
                await replies.Writer.WriteAsync((Encoding.UTF8.GetBytes(new JsonObject { ["id"] = request["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["message"] = ContextError } }.ToJsonString()), WebSocketMessageType.Text), token);
                return;
            }
            var method = Json.Text(request["method"]);
            JsonObject result;
            if (method == "Runtime.evaluate")
            {
                if (!condition) { Interlocked.Increment(ref UrlCalls); await Task.Delay(UrlDelay, token); }
                JsonNode value = condition ? new JsonObject { ["url"] = "http://127.0.0.1/", ["state"] = "complete", ["found"] = true, ["urlMatches"] = true }
                    : JsonValue.Create("http://127.0.0.1/")!;
                result = new JsonObject { ["result"] = new JsonObject { ["value"] = value } };
            }
            else if (method == "Page.captureScreenshot")
            {
                Interlocked.Increment(ref ScreenshotCalls); await Task.Delay(ScreenshotDelay, token);
                result = new JsonObject { ["data"] = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==" };
            }
            else result = new JsonObject { ["method"] = method };
            var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
            await replies.Writer.WriteAsync((bytes, WebSocketMessageType.Text), token);
            if (condition) ConditionReplied.TrySetResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
