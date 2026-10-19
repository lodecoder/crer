using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Crer;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(15)); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task ErrorCode(Task request, int expected, string diagnostic)
{
    try { await request.WaitAsync(TimeSpan.FromSeconds(3)); }
    catch (CrerException e)
    {
        Check(e.Code == expected, $"expected code {expected}, got {e.Code}");
        Check(e.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase), $"missing '{diagnostic}' in '{e.Message}'");
        return;
    }
    throw new Exception("CDP request unexpectedly succeeded");
}

await Test("concurrent responses are matched by ID in reverse order and retain sessions", async () =>
{
    await using var fixture = await Connection.Create();
    var calls = Enumerable.Range(1, 4).Select(i => fixture.Client.Call($"Test.{i}", new() { ["value"] = i }, $"session-{i}")).ToArray();
    var requests = new List<JsonObject>();
    for (var i = 0; i < calls.Length; i++) requests.Add(await fixture.Read());
    Check(fixture.Client.PendingCount == 4, "not all concurrent requests were pending");
    foreach (var request in requests.AsEnumerable().Reverse())
    {
        var number = request["params"]!["value"]!.GetValue<int>();
        Check(request["sessionId"]!.GetValue<string>() == $"session-{number}", "session ID changed");
        await fixture.Reply(request, new() { ["echo"] = number });
    }
    var results = await Task.WhenAll(calls);
    for (var i = 0; i < results.Length; i++) Check(results[i]["echo"]!.GetValue<int>() == i + 1, "response routed to wrong request");
    Check(fixture.Client.PendingCount == 0, "completed requests leaked");
});

await Test("a close frame rejects every pending request with environment code 3", async () =>
{
    await using var fixture = await Connection.Create();
    var calls = Enumerable.Range(0, 4).Select(i => fixture.Client.Call($"Wait.{i}")).ToArray();
    for (var i = 0; i < calls.Length; i++) await fixture.Read();
    await fixture.Server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", CancellationToken.None);
    foreach (var call in calls) await ErrorCode(call, 3, "closed");
    Check(fixture.Client.PendingCount == 0, "disconnected requests leaked");
    await ErrorCode(fixture.Client.Call("After.close"), 3, "closed");
});

await Test("idle connection loss completes Disconnected without a pending request", async () =>
{
    await using var fixture = await Connection.Create();
    Check(fixture.Client.PendingCount == 0, "idle connection unexpectedly has requests");
    Check(!fixture.Client.Disconnected.IsCompleted, "new connection is already disconnected");
    await fixture.Server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "idle disconnect", CancellationToken.None);
    var failure = await fixture.Client.Disconnected.WaitAsync(TimeSpan.FromSeconds(3));
    Check(failure is CrerException { Code: 3 }, "idle disconnect did not report environment code 3");
    Check(failure.Message.Contains("closed", StringComparison.OrdinalIgnoreCase), "idle disconnect diagnostic missing");
    Check(fixture.Client.PendingCount == 0, "idle disconnect created pending requests");
    await fixture.DisposeClient();
    Check(ReferenceEquals(failure, await fixture.Client.Disconnected), "dispose replaced the original disconnect failure");
});

await Test("returned result has no parent and can be attached to artifact JSON", async () =>
{
    await using var fixture = await Connection.Create();
    var call = fixture.Client.Call("Browser.getWindowBounds");
    await fixture.Reply(await fixture.Read(), new() { ["bounds"] = new JsonObject { ["width"] = 876, ["height"] = 599 } });
    var result = await call;
    Check(result.Parent == null, "CDP response result still belongs to its wire message");
    var artifact = new JsonObject { ["window"] = result };
    Check(ReferenceEquals(result.Parent, artifact), "response could not be attached to artifact");
    var roundTrip = JsonNode.Parse(artifact.ToJsonString())!;
    Check(roundTrip["window"]!["bounds"]!["width"]!.GetValue<int>() == 876, "attached result lost its nested data");
    Check(fixture.Client.PendingCount == 0, "completed result left a pending request");
});

await Test("protocol error is step code 4 and leaves the connection usable", async () =>
{
    await using var fixture = await Connection.Create();
    var call = fixture.Client.Call("Unsupported.command");
    var request = await fixture.Read();
    await fixture.Send(new JsonObject { ["id"] = request["id"]!.GetValue<int>(),
        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "unknown test method" } }.ToJsonString());
    await ErrorCode(call, 4, "unknown test method");
    Check(fixture.Client.PendingCount == 0 && !fixture.Client.Disconnected.IsCompleted, "protocol error leaked or closed the connection");
    var next = fixture.Client.Call("After.protocol.error");
    await fixture.Reply(await fixture.Read(), new() { ["ok"] = true });
    Check((await next)["ok"]!.GetValue<bool>() && fixture.Client.PendingCount == 0, "protocol error broke subsequent response routing");
});

await Test("cancellation removes its pending request and preserves connection usability", async () =>
{
    await using var fixture = await Connection.Create();
    using var cancellation = new CancellationTokenSource();
    var cancelled = fixture.Client.Call("Wait.cancel", token: cancellation.Token);
    var original = await fixture.Read();
    cancellation.Cancel();
    try { await cancelled; throw new Exception("request ignored cancellation"); }
    catch (OperationCanceledException) { }
    Check(fixture.Client.PendingCount == 0, "cancelled request leaked");
    // A late response for a cancelled ID must not complete another request.
    await fixture.Reply(original, new() { ["echo"] = "late" });
    var next = fixture.Client.Call("After.cancel");
    await fixture.Reply(await fixture.Read(), new() { ["echo"] = "next" });
    Check((await next)["echo"]!.GetValue<string>() == "next", "late response confused request routing");
});

await Test("request timeout reports code 3 and removes pending state", async () =>
{
    await using var fixture = await Connection.Create();
    var timed = fixture.Client.Call("Wait.timeout", timeoutMs: 100);
    await fixture.Read();
    await ErrorCode(timed, 3, "timed out: Wait.timeout");
    Check(fixture.Client.PendingCount == 0, "timed out request leaked");
});

await Test("dispose rejects every pending request and empties the registry", async () =>
{
    await using var fixture = await Connection.Create();
    var calls = Enumerable.Range(0, 4).Select(i => fixture.Client.Call($"Wait.dispose.{i}")).ToArray();
    for (var i = 0; i < calls.Length; i++) await fixture.Read();
    await fixture.DisposeClient();
    foreach (var call in calls) await ErrorCode(call, 3, "closed by client");
    Check(fixture.Client.PendingCount == 0, "dispose left pending requests");
});

await Test("invalid JSON rejects all pending requests with code 3", async () =>
{
    await using var fixture = await Connection.Create();
    var first = fixture.Client.Call("Wait.invalid.1");
    var second = fixture.Client.Call("Wait.invalid.2");
    await fixture.Read();
    await fixture.Read();
    await fixture.Send("{invalid");
    await ErrorCode(first, 3, "connection lost");
    await ErrorCode(second, 3, "connection lost");
    Check(fixture.Client.PendingCount == 0, "malformed-message requests leaked");
});

SemaphoreSlim SendSlot(Cdp client) => (SemaphoreSlim)typeof(Cdp)
    .GetField("sendLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

await Test("call timeout includes waiting for the send slot and leaves the connection usable", async () =>
{
    await using var fixture = await Connection.Create();
    var slot = SendSlot(fixture.Client);
    await slot.WaitAsync();
    try
    {
        await ErrorCode(fixture.Client.Call("Queue.timeout", timeoutMs: 50), 3, "timed out: Queue.timeout");
        Check(fixture.Client.PendingCount == 0, "send-slot timeout left a pending request");
        Check(!fixture.Client.Disconnected.IsCompleted, "a command which was never sent aborted the connection");
    }
    finally { slot.Release(); }
    var next = fixture.Client.Call("After.queue.timeout");
    var request = await fixture.Read();
    Check(request["method"]!.GetValue<string>() == "After.queue.timeout", "timed-out queued command was still sent");
    await fixture.Reply(request, new() { ["ok"] = true });
    Check((await next)["ok"]!.GetValue<bool>(), "queue timeout made the connection unusable");
});

await Test("caller cancellation while waiting for the send slot does not send or abort", async () =>
{
    await using var fixture = await Connection.Create();
    var slot = SendSlot(fixture.Client);
    await slot.WaitAsync();
    try
    {
        using var cancelled = new CancellationTokenSource();
        var call = fixture.Client.Call("Queue.cancel", token: cancelled.Token);
        cancelled.Cancel();
        try { await call; throw new Exception("queued call ignored cancellation"); }
        catch (OperationCanceledException) { }
        Check(fixture.Client.PendingCount == 0 && !fixture.Client.Disconnected.IsCompleted, "queued cancellation leaked or aborted");
    }
    finally { slot.Release(); }
    var next = fixture.Client.Call("After.queue.cancel");
    var request = await fixture.Read();
    Check(request["method"]!.GetValue<string>() == "After.queue.cancel", "cancelled queued command was sent");
    await fixture.Reply(request, new()); await next;
});

await Test("timeout interrupts a stalled WebSocket send and rejects the ambiguous transport", async () =>
{
    using var transport = new BlockedSendSocket();
    await using var client = new Cdp(transport);
    await client.Open("ws://127.0.0.1/test", default);
    var call = client.Call("Send.timeout", timeoutMs: 50);
    await transport.SendStarted.Task;
    await ErrorCode(call, 3, "timed out: Send.timeout");
    Check(client.PendingCount == 0, "stalled-send timeout left a pending request");
    Check(client.Disconnected.IsCompleted && transport.State == WebSocketState.Aborted, "partially sent command left the connection reusable");
    await ErrorCode(client.Call("After.send.timeout"), 3, "CDP");
});

await Test("response timeout leaves the socket reusable and ignores the late response", async () =>
{
    await using var fixture = await Connection.Create();
    var call = fixture.Client.Call("Response.timeout", timeoutMs: 50);
    var request = await fixture.Read();
    await ErrorCode(call, 3, "timed out: Response.timeout");
    Check(!fixture.Client.Disconnected.IsCompleted, "response timeout aborted the socket before input cleanup could run");
    await fixture.Reply(request, new() { ["late"] = true });
    var next = fixture.Client.Call("After.response.timeout");
    await fixture.Reply(await fixture.Read(), new() { ["ok"] = true });
    Check((await next)["ok"]!.GetValue<bool>() && fixture.Client.PendingCount == 0, "late timeout response changed routing");
});

await Test("dispose cancels queued sends with no caller token and waits until they drain", async () =>
{
    await using var fixture = await Connection.Create();
    var slot = SendSlot(fixture.Client);
    await slot.WaitAsync();
    var calls = Enumerable.Range(0, 8).Select(index => fixture.Client.Call($"Queue.dispose.{index}", timeoutMs: Timeout.Infinite)).ToArray();
    Check(fixture.Client.PendingCount == 8, "queued calls were not registered");
    // Dispose owns and releases the semaphore; the test must not release a disposed slot.
    await fixture.DisposeClient();
    foreach (var call in calls) await ErrorCode(call, 3, "closed by client");
    Check(fixture.Client.PendingCount == 0, "dispose returned before queued sends drained");
    await ErrorCode(fixture.Client.Call("After.dispose"), 3, "closed by client");
});

await Test("dispose interrupts a stalled send and all callers finish before resource disposal", async () =>
{
    using var transport = new BlockedSendSocket();
    var client = new Cdp(transport);
    await client.Open("ws://127.0.0.1/test", default);
    var call = client.Call("Send.dispose", timeoutMs: Timeout.Infinite);
    await transport.SendStarted.Task;
    await client.DisposeAsync();
    await ErrorCode(call, 3, "closed by client");
    Check(client.PendingCount == 0 && transport.Disposed, "dispose returned before the active send drained");
});

await Test("caller cancellation interrupts a stalled send and preserves interruption identity", async () =>
{
    using var transport = new BlockedSendSocket();
    await using var client = new Cdp(transport);
    await client.Open("ws://127.0.0.1/test", default);
    using var cancelled = new CancellationTokenSource();
    var call = client.Call("Send.cancel", token: cancelled.Token, timeoutMs: Timeout.Infinite);
    await transport.SendStarted.Task;
    cancelled.Cancel();
    try { await call; throw new Exception("send ignored caller cancellation"); }
    catch (OperationCanceledException error) { Check(error.CancellationToken == cancelled.Token, "caller cancellation token was replaced"); }
    Check(client.PendingCount == 0 && client.Disconnected.IsCompleted, "cancelled ambiguous send leaked or left the socket usable");
});

await Test("dispose cancels an incomplete opening handshake before disposing transport resources", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new Cdp();
    try
    {
        var opening = client.Open($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/pending-handshake", default);
        using var accepted = await listener.AcceptTcpClientAsync();
        // The TCP peer accepts the connection but never replies to the HTTP upgrade.
        await client.DisposeAsync();
        await ErrorCode(opening, 3, "closed by client");
        Check(client.PendingCount == 0, "opening/disposal left pending state");
    }
    finally { listener.Stop(); await client.DisposeAsync(); }
});

await Test("abort cancels queued sends and preserves the first failure", async () =>
{
    await using var fixture = await Connection.Create();
    await SendSlot(fixture.Client).WaitAsync();
    var calls = Enumerable.Range(0, 32).Select(index => fixture.Client.Call($"Queue.abort.{index}", timeoutMs: Timeout.Infinite)).ToArray();
    var original = new CrerException(3, "test abort failure");
    fixture.Client.Abort(original);
    foreach (var call in calls) await ErrorCode(call, 3, "test abort failure");
    Check(fixture.Client.PendingCount == 0 && ReferenceEquals(original, await fixture.Client.Disconnected), "abort leaked or replaced the failure");
});

await Test("concurrent repeated disposal and abort after disposal are safe", async () =>
{
    await using var fixture = await Connection.Create();
    await Task.WhenAll(fixture.Client.DisposeAsync().AsTask(), fixture.Client.DisposeAsync().AsTask());
    await fixture.Client.DisposeAsync();
    fixture.Client.Abort(new CrerException(3, "later failure"));
    await ErrorCode(fixture.Client.Call("After.repeated.dispose"), 3, "closed by client");
    Check(fixture.Client.PendingCount == 0, "repeated disposal created pending state");
});

await Test("実行保存先の切替と受信イベントが競合しても CDP 接続と通信記録を維持する", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "crer-network-rollover-" + Guid.NewGuid().ToString("N"));
    var previous = Path.Combine(root, "previous");
    var next = Path.Combine(root, "next");
    Directory.CreateDirectory(previous);
    try
    {
        await using var fixture = await Connection.Create();
        var playback = new Playback();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var directoryField = typeof(Playback).GetField("absoluteRunDir", flags)!;
        var initialize = typeof(Playback).GetMethod("InitializeRunDirectory", flags)!;
        initialize.Invoke(playback, [previous]);
        var logField = typeof(Playback).GetField("networkLog", flags)!;
        var previousLog = (AsyncDiagnosticLog)logField.GetValue(playback)!;
        typeof(Playback).GetField("session", flags)!.SetValue(playback, "network-rollover");
        var handler = typeof(Playback).GetMethod("OnNetworkEvent", flags)!
            .CreateDelegate<Action<string, JsonObject, string?>>(playback);
        fixture.Client.Event += handler;
        JsonObject Request(string id) => new() { ["requestId"] = id, ["type"] = "Document" };
        JsonObject Event(string id) => new()
        {
            ["method"] = "Network.requestWillBeSent", ["params"] = Request(id), ["sessionId"] = "network-rollover",
        };
        handler("Network.requestWillBeSent", Request("previous"), "network-rollover");

        var gate = typeof(Playback).GetField("networkLock", flags)!.GetValue(playback)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holding = Task.Run(() =>
        {
            lock (gate)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("通信ロックの解放が期限を超えた");
            }
        });
        Task? switching = null;
        try
        {
            await entered.Task;
            switching = Task.Run(() => typeof(Playback).GetMethod("InitializeRunDirectory", flags)!.Invoke(playback, [next]));
            var deadline = Stopwatch.StartNew();
            while (!Directory.Exists(next) && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Check(Directory.Exists(next), "次の実行ディレクトリが作成されなかった");
            // 受信側が旧保存先を使用している間は、保存先も集計値も切り替わってはいけない。
            Check((string)directoryField.GetValue(playback)! == previous, "通信ロックの取得前に新しい保存先を公開した");
            Check((int)typeof(Playback).GetField("networkRequests", flags)!.GetValue(playback)! == 1, "受信処理との排他前に通信集計を初期化した");

            await fixture.Send(Event("handoff").ToJsonString());
            var probe = fixture.Client.Call("Test.networkRollover");
            await fixture.Reply(await fixture.Read(), new());
            release.Set();
            await Task.WhenAll(switching, holding, probe);

            await fixture.Send(Event("next").ToJsonString());
            var after = fixture.Client.Call("Test.afterNetworkRollover");
            await fixture.Reply(await fixture.Read(), new());
            await after;
            Check(!fixture.Client.Disconnected.IsCompleted, "実行保存先の切替で CDP 接続が切断された");
            await previousLog.CompleteAsync();
            await ((AsyncDiagnosticLog)logField.GetValue(playback)!).CompleteAsync();
            var oldRecords = File.ReadAllLines(Path.Combine(previous, "network.ndjson")).Select(line => JsonNode.Parse(line)!).ToArray();
            var newRecords = File.ReadAllLines(Path.Combine(next, "network.ndjson")).Select(line => JsonNode.Parse(line)!).ToArray();
            var ids = oldRecords.Concat(newRecords).Select(record => Json.Text(record["requestId"])).Order().ToArray();
            Check(ids.SequenceEqual(new[] { "handoff", "next", "previous" }), "切替中の通信イベントが欠落または重複した");
            Check(Json.Text(newRecords[^1]["requestId"]) == "next", "切替後の通信記録が旧保存先へ書き込まれた");
            Check((int)typeof(Playback).GetField("networkRequests", flags)!.GetValue(playback)! == newRecords.Length, "通信記録と実行ごとの集計が一致しない");
        }
        finally
        {
            release.Set();
            await holding;
            if (switching != null) await switching;
            fixture.Client.Event -= handler;
        }
    }
    finally { Directory.Delete(root, recursive: true); }
});

await Test("通信ログの保存失敗でも正常な CDP 応答を受信できる", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "crer-network-log-failure-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        await using var fixture = await Connection.Create();
        var playback = new Playback();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Playback).GetMethod("InitializeRunDirectory", flags)!.Invoke(playback, [directory]);
        typeof(Playback).GetField("session", flags)!.SetValue(playback, "logging-test");
        var log = (AsyncDiagnosticLog)typeof(Playback).GetField("networkLog", flags)!.GetValue(playback)!;
        using var locked = new FileStream(Path.Combine(directory, "network.ndjson"), FileMode.Create, FileAccess.Write, FileShare.None);
        var handler = typeof(Playback).GetMethod("OnNetworkEvent", flags)!.CreateDelegate<Action<string, JsonObject, string?>>(playback);
        fixture.Client.Event += handler;
        await fixture.Send(new JsonObject { ["method"] = "Network.requestWillBeSent", ["sessionId"] = "logging-test",
            ["params"] = new JsonObject { ["requestId"] = "locked-log", ["type"] = "Document" } }.ToJsonString());
        var response = fixture.Client.Call("Test.afterLogFailure");
        await fixture.Reply(await fixture.Read(), new() { ["ok"] = true });
        Check((await response)["ok"]!.GetValue<bool>() && !fixture.Client.Disconnected.IsCompleted, "診断ログの障害で通信を切断した");
        await log.CompleteAsync();
        Check(log.Diagnostic()["writeFailed"]!.GetValue<bool>(), "保存失敗の診断を残さなかった");
        fixture.Client.Event -= handler;
    }
    finally { Directory.Delete(directory, true); }
});

await Test("遅い通信ログの保存は受信処理を待たせずキュー容量も制限する", async () =>
{
    using var opened = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var log = new AsyncDiagnosticLog("unused", () =>
    {
        opened.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("テスト用保存先の待機期限");
        return new MemoryStream();
    });
    try
    {
        log.Append("first");
        Check(opened.Wait(TimeSpan.FromSeconds(2)), "ログ保存を開始しなかった");
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 5000; index++) log.Append("queued");
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "受信側が保存待ちになった");
        Check(Json.Number(log.Diagnostic()["dropped"]) > 0, "容量上限を超えた通信記録を無制限に保持した");
    }
    finally { release.Set(); await log.CompleteAsync(); }
});

await Test("通信ログの保存先が停止していても終了待ちは一秒の予算で戻る", async () =>
{
    using var opened = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var log = new AsyncDiagnosticLog("unused", () =>
    {
        opened.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("テスト用保存先の待機期限");
        return new MemoryStream();
    });
    try
    {
        log.Append("pending");
        Check(opened.Wait(TimeSpan.FromSeconds(2)), "保存処理を開始しなかった");
        var watch = Stopwatch.StartNew();
        await log.CompleteAsync();
        Check(watch.Elapsed < TimeSpan.FromSeconds(1.5) && !log.Diagnostic()["completed"]!.GetValue<bool>() && Json.Number(log.Diagnostic()["pending"]) == 1,
            "終了時に無応答の保存先を無期限に待った");
    }
    finally { release.Set(); await log.CompleteAsync(); }
});

await Test("flush 前に保存が失敗した行も破棄件数へ含める", async () =>
{
    var log = new AsyncDiagnosticLog("unused", () => new FailingFlushStream());
    log.Append("not-confirmed");
    await log.CompleteAsync();
    var diagnostic = log.Diagnostic();
    Check(diagnostic["writeFailed"]!.GetValue<bool>() && Json.Number(diagnostic["dropped"]) == 1 && Json.Number(diagnostic["pending"]) == 0,
        "キューから取り出した未保存行を保存成功として扱った");
});

await Test("非表示照合環境の文脈待ちでは実 CDP に評価パラメーターを再送できる", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "crer-matcher-context-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var template = Path.Combine(directory, "pixel.png");
    await File.WriteAllBytesAsync(template, [0]);
    try
    {
        await using var fixture = await Connection.Create();
        var methods = new List<string>();
        var expected = new TemplateMatch(10, 20, 30, 40, .99);
        JsonObject? firstEvaluation = null;
        var evaluations = 0;
        async Task Serve()
        {
            while (true)
            {
                var request = await fixture.Read();
                var method = Json.Text(request["method"]);
                var session = Json.Text(request["sessionId"]);
                methods.Add(method);
                switch (method)
                {
                    case "Page.captureScreenshot":
                        Check(session == "page-context-test", "対象ページ以外からスクリーンショットを取得した");
                        await fixture.Reply(request, new() { ["data"] = "c2NyZWVu" });
                        break;
                    case "Target.createTarget":
                        Check(session == "" && request["params"]!["hidden"]!.GetValue<bool>() && request["params"]!["background"]!.GetValue<bool>(), "非表示の照合環境を作成しなかった");
                        await fixture.Reply(request, new() { ["targetId"] = "owned-matcher-context" });
                        break;
                    case "Target.attachToTarget":
                        Check(session == "" && Json.Text(request["params"]?["targetId"]) == "owned-matcher-context", "別の照合環境へ接続した");
                        await fixture.Reply(request, new() { ["sessionId"] = "matcher-context-test" });
                        break;
                    case "Runtime.enable":
                        Check(session == "matcher-context-test", "対象ページ側の Runtime を有効にした");
                        await fixture.Reply(request, new());
                        break;
                    case "Runtime.evaluate":
                        Check(session == "matcher-context-test", "対象ページ内でテンプレート照合を評価した");
                        evaluations++;
                        if (evaluations == 1)
                        {
                            firstEvaluation = request["params"]!.DeepClone().AsObject();
                            await fixture.Send(new JsonObject
                            {
                                ["id"] = request["id"]!.DeepClone(),
                                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Cannot find default execution context" },
                            }.ToJsonString());
                        }
                        else
                        {
                            Check(evaluations == 2 && JsonNode.DeepEquals(firstEvaluation, request["params"]), "再試行で評価内容が変わったか評価回数が増えた");
                            await fixture.Reply(request, new() { ["result"] = new JsonObject { ["value"] = expected.ToJson() } });
                        }
                        break;
                    case "Target.closeTarget":
                        Check(session == "" && Json.Text(request["params"]?["targetId"]) == "owned-matcher-context", "所有していない target を閉じた");
                        await fixture.Reply(request, new() { ["success"] = true });
                        return;
                    default: throw new Exception("非表示照合環境が想定外の操作を要求した: " + method);
                }
            }
        }
        async Task MatchAndClose()
        {
            await using var matcher = new TemplateMatcher(fixture.Client, "page-context-test");
            var result = await matcher.MatchAsync(new() { ["path"] = template }, null);
            Check(result.Match == expected && result.Screenshot == "c2NyZWVu", "文脈待ちの再試行で照合結果または取得済み画像を失った");
        }
        // 実 CDP は最初の送信時に params を所有するため、同じ JsonObject の再利用では次の送信が失敗する。
        await Task.WhenAll(MatchAndClose(), Serve());
        Check(methods.SequenceEqual(new[] { "Page.captureScreenshot", "Target.createTarget", "Target.attachToTarget", "Runtime.enable", "Runtime.evaluate", "Runtime.evaluate", "Target.closeTarget" }),
            "文脈待ちで撮影や環境作成を繰り返したか、評価または所有 target の終了を省略した");
        Check(fixture.Client.PendingCount == 0 && !fixture.Client.Disconnected.IsCompleted, "照合の再試行で CDP 要求が残るか接続を破棄した");
    }
    finally { Directory.Delete(directory, recursive: true); }
});

async Task InitialViewportFinalCheck(bool contextDestroyed)
{
    var directory = Path.Combine(Path.GetTempPath(), "crer-initial-viewport-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        await using var fixture = await Connection.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var playback = new Playback();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string field, object value) => typeof(Playback).GetField(field, flags)!.SetValue(playback, value);
        var diagnostic = new JsonObject { ["status"] = "starting", ["timeoutMs"] = 0, ["stages"] = new JsonArray() };
        Set("cdp", fixture.Client); Set("session", "viewport-test"); Set("absoluteRunDir", directory);
        Set("token", deadline.Token); Set("startupDiagnostic", diagnostic);
        ((Stopwatch)typeof(Playback).GetField("startupWatch", flags)!.GetValue(playback)!).Restart();
        typeof(Playback).GetMethod("SetStartupStage", flags)!.Invoke(playback, ["document_readiness"]);
        var document = typeof(Playback).GetMethod("WaitForInitialDocument", flags)!;
        var viewport = typeof(Playback).GetMethod("WaitForInitialViewport", flags)!;
        var documentCalls = 0; var finalChecks = 0; var injected = false;
        var displayPath = Path.Combine(directory, "display.json");
        Task Value(JsonObject request, JsonNode value) => fixture.Reply(request, new()
        {
            ["result"] = new JsonObject { ["value"] = value },
        });
        async Task Serve()
        {
            while (true)
            {
                var request = await fixture.Read();
                Check(Json.Text(request["sessionId"]) == "viewport-test", "viewport 測定の session が変わった");
                var method = Json.Text(request["method"]);
                if (method == "Page.getLayoutMetrics")
                {
                    await fixture.Reply(request, new()
                    {
                        ["cssVisualViewport"] = new JsonObject { ["clientWidth"] = documentCalls > 1 ? 900 : 800, ["clientHeight"] = documentCalls > 1 ? 500 : 400 },
                    });
                    continue;
                }
                Check(method == "Runtime.evaluate", "viewport 測定中に想定外の CDP 操作を要求した: " + method);
                var expression = Json.Text(request["params"]?["expression"]);
                if (expression == "({url:location.href,ready:document.readyState})")
                {
                    documentCalls++;
                    await Value(request, new JsonObject { ["url"] = documentCalls == 1 ? "http://fixture/before" : "http://fixture/after", ["ready"] = "complete" });
                    continue;
                }
                if (expression == "({dpr:devicePixelRatio,scale:visualViewport?.scale ?? 1})")
                {
                    await Value(request, new JsonObject { ["dpr"] = 1, ["scale"] = 1 });
                    continue;
                }
                Check(expression == "document.readyState", "想定外のページ評価を要求した: " + expression);
                // display.json の保存前には常に complete を返し、保存直後の最後の照会だけを競合させる。
                if (File.Exists(displayPath) && !injected)
                {
                    injected = true; finalChecks++;
                    if (contextDestroyed)
                        await fixture.Send(new JsonObject
                        {
                            ["id"] = request["id"]!.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Execution context was destroyed." },
                        }.ToJsonString());
                    else await Value(request, JsonValue.Create("loading")!);
                    continue;
                }
                var final = injected && documentCalls > 1 && File.Exists(displayPath)
                    && Json.Number(JsonNode.Parse(File.ReadAllText(displayPath))?["actual"]?["width"]) == 900;
                if (final) finalChecks++;
                await Value(request, JsonValue.Create("complete")!);
                if (final) return;
            }
        }
        async Task<JsonObject> Prepare()
        {
            await (Task)document.Invoke(playback, null)!;
            return await (Task<JsonObject>)viewport.Invoke(playback, [null, false, new JsonObject()])!;
        }
        var preparing = Prepare();
        await Task.WhenAll(preparing, Serve());
        var actual = await preparing;
        Check(injected && finalChecks == 2 && documentCalls == 2, "診断保存後の競合で文書準備と最終確認をやり直していない");
        Check(Json.Number(actual["width"]) == 900 && Json.Number(actual["height"]) == 500, "追加遷移前の測定値を返した");
        Check((double)typeof(Playback).GetField("width", flags)!.GetValue(playback)! == 900
            && (double)typeof(Playback).GetField("height", flags)!.GetValue(playback)! == 500, "入力用の viewport フィールドに再測定値を反映していない");
        var savedDisplay = JsonNode.Parse(File.ReadAllText(displayPath))!;
        Check(Json.Number(savedDisplay["actual"]?["width"]) == 900 && Json.Number(savedDisplay["actual"]?["height"]) == 500, "display.json に古い測定値が残った");
        var stages = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "startup.json")))!["stages"]!.AsArray()
            .Select(stage => Json.Text(stage!["stage"])).ToArray();
        Check(stages.SequenceEqual(new[] { "document_readiness", "viewport_stability", "document_readiness", "viewport_stability" }),
            "診断保存後の競合で起動段階の再往復を記録していない");
        Check(fixture.Client.PendingCount == 0 && !fixture.Client.Disconnected.IsCompleted, "viewport 再待機で CDP 要求が残るか接続を破棄した");
    }
    finally { Directory.Delete(directory, true); }
}

await Test("viewport診断保存後にloadingへ戻っても文書準備とサイズ測定をやり直す", () => InitialViewportFinalCheck(false));
await Test("viewport診断保存後に実行コンテキストが破棄されても文書準備とサイズ測定をやり直す", () => InitialViewportFinalCheck(true));

async Task RecordingContentSize(bool scrollbar, bool neverResizes)
{
    await using var fixture = await Connection.Create();
    var browser = new RecordingBrowser("unused", "unused");
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    typeof(RecordingBrowser).GetField("cdp", flags)!.SetValue(browser, fixture.Client);
    typeof(RecordingBrowser).GetField("session", flags)!.SetValue(browser, "recording-size");
    typeof(RecordingBrowser).GetField("windowId", flags)!.SetValue(browser, 7);
    var captures = 0; var measurements = 0;
    var sizing = browser.SynchronizeContent(new(600, 400), default, TimeSpan.FromSeconds(neverResizes ? .6 : 5));
    async Task Serve()
    {
        try
        {
            while (true)
            {
                var request = await fixture.Read();
                var method = Json.Text(request["method"]);
                var resized = !neverResizes && captures > 0;
                var width = resized ? scrollbar ? 585 : 600 : 1889;
                var height = resized ? 400 : 2005;
                if (method == "Browser.setContentsSize")
                {
                    Check(Json.Number(request["params"]?["width"]) == 600 && Json.Number(request["params"]?["height"]) == 400,
                        "記録用ウィンドウへ要求寸法を適用しなかった");
                    await fixture.Reply(request, new());
                }
                else if (method == "Page.captureScreenshot") { captures++; await fixture.Reply(request, new()); }
                else if (method == "Page.getLayoutMetrics")
                {
                    measurements++;
                    await fixture.Reply(request, new() { ["cssVisualViewport"] = new JsonObject { ["clientWidth"] = width, ["clientHeight"] = height } });
                }
                else
                {
                    Check(method == "Runtime.evaluate", "記録の寸法待機中に想定外の操作を行った: " + method);
                    await fixture.Reply(request, new() { ["result"] = new JsonObject { ["value"] = new JsonObject
                    {
                        ["dpr"] = 1, ["scale"] = 1, ["innerWidth"] = resized ? 600 : 1889, ["innerHeight"] = resized ? 400 : 2005,
                        ["width"] = width, ["height"] = height,
                    } } });
                }
            }
        }
        catch (Exception) when (sizing.IsCompleted) { }
    }
    var serving = Serve();
    try
    {
        if (neverResizes) await ErrorCode(sizing, 3, "did not synchronize");
        else Check(await sizing == new NormalizePoint(scrollbar ? 585 : 600, 400), "実際の viewport を取得しなかった");
        Check(captures > 0 && measurements > 1, "古い巨大 viewport を同期せずスクロールバーとして受理した");
        Check(fixture.Client.PendingCount == 0, "記録寸法の待機後に未完了 CDP 要求が残った");
    }
    finally { fixture.Server.Abort(); await serving; }
}

await Test("記録開始前に古い巨大viewportをcaptureで同期し要求contentの反映を待つ", () => RecordingContentSize(false, false));
await Test("記録開始前の真のスクロールバー縮小はDOMと一致すれば許可する", () => RecordingContentSize(true, false));
await Test("記録のviewportが要求contentへ反映されなければ期限内に明示的に失敗する", () => RecordingContentSize(false, true));

JsonObject Attached(string id, string type) => new()
{
    ["sessionId"] = id, ["waitingForDebugger"] = false,
    ["targetInfo"] = new JsonObject { ["type"] = type, ["targetId"] = "target-" + id, ["url"] = "https://private.test/do-not-log" },
};
async Task ObserverSettled(Func<bool> ready)
{
    var watch = Stopwatch.StartNew();
    while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(5);
    Check(ready(), "関連 target の非同期初期化または detach が完了しない");
}
void AutoAttachRequest(JsonObject request, string session)
{
    Check(Json.Text(request["method"]) == "Target.setAutoAttach" && Json.Text(request["sessionId"]) == session, "autoAttach の設定先が違う");
    var settings = request["params"]!;
    Check(settings["autoAttach"]!.GetValue<bool>() && settings["flatten"]!.GetValue<bool>()
        && !settings["waitForDebuggerOnStart"]!.GetValue<bool>(), "関連 target を debugger 停止させたか平坦化を省いた");
    var filter = settings["filter"]!.AsArray();
    Check(filter.Count == 5 && filter.Last()!["exclude"]!.GetValue<bool>()
        && filter.Take(4).Select(item => Json.Text(item!["type"])).SequenceEqual(new[] { "iframe", "worker", "shared_worker", "service_worker" }),
        "無関係な page を含む autoAttach filter を使用した");
}

await Test("関連iframeとworkerを再帰観測し子の初期化待ちでCDP受信を止めない", async () =>
{
    await using var fixture = await Connection.Create();
    var removed = new ConcurrentQueue<string>();
    await using var observer = new RelatedTargetObserver(fixture.Client, "observed-page", removed.Enqueue);
    fixture.Client.Event += observer.OnEvent;
    Check(observer.TryGetScope("observed-page", out var mainType) && mainType == "page", "親 page を観測範囲に含めない");
    var starting = observer.Start(default);
    var root = await fixture.Read(); AutoAttachRequest(root, "observed-page");
    await fixture.Reply(root, new()); await starting;
    await fixture.Send(new JsonObject { ["method"] = "Target.attachedToTarget", ["params"] = Attached("frame", "iframe"), ["sessionId"] = "observed-page" }.ToJsonString());
    var network = await fixture.Read();
    Check(Json.Text(network["method"]) == "Network.enable" && Json.Text(network["sessionId"]) == "frame"
        && observer.TryGetScope("frame", out var frameType) && frameType == "iframe", "子 iframe を初期化前に登録しない");
    var ping = fixture.Client.Call("Test.observer.ping");
    var request = await fixture.Read();
    Check(Json.Text(request["method"]) == "Test.observer.ping", "子の返信待ちが親の要求を塞いだ");
    await fixture.Reply(request, new() { ["ok"] = true });
    Check((await ping.WaitAsync(TimeSpan.FromSeconds(1)))["ok"]!.GetValue<bool>(), "イベントハンドラーが子の初期化を同期的に待った");
    await fixture.Reply(network, new());
    var runtime = await fixture.Read();
    Check(Json.Text(runtime["method"]) == "Runtime.enable" && Json.Text(runtime["sessionId"]) == "frame", "子 Runtime の例外観測を有効にしない");
    await fixture.Reply(runtime, new());
    var recursive = await fixture.Read(); AutoAttachRequest(recursive, "frame"); await fixture.Reply(recursive, new());
    await fixture.Send(new JsonObject { ["method"] = "Target.attachedToTarget", ["params"] = Attached("nested-worker", "worker"), ["sessionId"] = "frame" }.ToJsonString());
    foreach (var method in new[] { "Network.enable", "Runtime.enable", "Target.setAutoAttach" })
    {
        var worker = await fixture.Read();
        Check(Json.Text(worker["method"]) == method && Json.Text(worker["sessionId"]) == "nested-worker", "孫 worker の初期化が欠けた");
        if (method == "Target.setAutoAttach") AutoAttachRequest(worker, "nested-worker");
        await fixture.Reply(worker, new());
    }
    await ObserverSettled(() => Json.Number(observer.Diagnostic()["pendingInitializations"]) == 0);
    var snapshot = observer.SnapshotScopes();
    Check(snapshot.Count == 3 && snapshot[0] == ("observed-page", "page")
        && Json.Number(observer.Diagnostic()["networkEnabled"]) == 2 && Json.Number(observer.Diagnostic()["runtimeEnabled"]) == 2,
        "親・子・孫の観測範囲または有効化回数が違う");
    observer.OnEvent("Target.attachedToTarget", Attached("unrelated-page", "page"), "observed-page");
    observer.OnEvent("Target.attachedToTarget", Attached("unrelated-worker", "worker"), "other-page");
    Check(!observer.TryGetScope("unrelated-page", out _) && !observer.TryGetScope("unrelated-worker", out _), "無関係な target を観測した");
    await fixture.Send(new JsonObject { ["method"] = "Target.detachedFromTarget", ["params"] = new JsonObject { ["sessionId"] = "frame" }, ["sessionId"] = "observed-page" }.ToJsonString());
    await ObserverSettled(() => removed.Count == 2);
    Check(!observer.TryGetScope("frame", out _) && !observer.TryGetScope("nested-worker", out _)
        && snapshot.Count == 3 && observer.SnapshotScopes().Count == 1, "detach で孫が残るか以前のスナップショットが変化した");
    Check(fixture.Client.PendingCount == 0 && !fixture.Client.Disconnected.IsCompleted, "診断用の観測で本体CDPを壊した");
});

await Test("関連targetの未対応domainを型と件数だけに記録し他の初期化を続ける", async () =>
{
    const string secret = "PRIVATE-URL-TOKEN-INPUT";
    var calls = new ConcurrentQueue<string>();
    var transport = new ObserverTransport((method, _, session, _, _) =>
    {
        calls.Enqueue(session + ":" + method);
        if (session == "worker" && method == "Network.enable") throw new CrerException(4, secret);
        if (session == "worker" && method == "Runtime.enable") throw new InvalidOperationException(secret);
        return Task.FromResult(new JsonObject());
    });
    await using var observer = new RelatedTargetObserver(transport, "page", _ => { });
    await observer.Start(default);
    observer.OnEvent("Target.attachedToTarget", Attached("worker", "service_worker"), "page");
    await ObserverSettled(() => calls.Count == 4 && Json.Number(observer.Diagnostic()["pendingInitializations"]) == 0);
    var diagnostic = observer.Diagnostic();
    Check(observer.TryGetScope("worker", out var type) && type == "service_worker"
        && calls.Contains("worker:Target.setAutoAttach") && Json.Number(diagnostic["failures"]) == 2
        && Json.Number(diagnostic["failureTypes"]?[nameof(CrerException)]) == 1
        && Json.Number(diagnostic["failureTypes"]?[nameof(InvalidOperationException)]) == 1,
        "未対応domainで関連 target の観測全体を中断した");
    Check(!diagnostic.ToJsonString().Contains(secret) && !diagnostic.ToJsonString().Contains("private.test"), "診断へURLや例外メッセージを保存した");
    await using var unsupported = new RelatedTargetObserver(new ObserverTransport((_, _, _, _, _) => throw new CrerException(4, secret)), "page", _ => { });
    await unsupported.Start(default);
    Check(Json.Number(unsupported.Diagnostic()["failures"]) == 1 && unsupported.Diagnostic()["mainAutoAttachEnabled"]?.GetValue<bool>() == false,
        "親 autoAttach の未対応を本体エラーへ伝播した");
});

await Test("関連target観測の開始取消しは元の利用者tokenを維持する", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ignored = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var observer = new RelatedTargetObserver(new ObserverTransport((_, _, _, _, _) =>
    { entered.TrySetResult(); return ignored.Task; }), "page", _ => { });
    using var cancellation = new CancellationTokenSource();
    var starting = observer.Start(cancellation.Token);
    await entered.Task;
    cancellation.Cancel();
    try { await starting; throw new Exception("観測開始が利用者の取消しを無視した"); }
    catch (OperationCanceledException error) { Check(error.CancellationToken == cancellation.Token, "元の利用者tokenを置き換えた"); }
    await observer.DisposeAsync();
    Check(Json.Number(observer.Diagnostic()["failures"]) == 0, "利用者取消しをdomain未対応として数えた");
});

await Test("関連targetの初期化が取消しを無視しても終了時に有限で排出する", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ignored = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
    var removed = new ConcurrentQueue<string>();
    var callToken = CancellationToken.None;
    var callTimeout = 0;
    var transport = new ObserverTransport((method, _, session, token, timeout) =>
    {
        if (session == "child" && method == "Network.enable")
        {
            callToken = token; callTimeout = timeout;
            entered.TrySetResult(); return ignored.Task;
        }
        return Task.FromResult(new JsonObject());
    });
    var observer = new RelatedTargetObserver(transport, "page", removed.Enqueue);
    await observer.Start(default);
    observer.OnEvent("Target.attachedToTarget", Attached("child", "shared_worker"), "page");
    await entered.Task;
    var watch = Stopwatch.StartNew();
    await Task.WhenAll(observer.DisposeAsync().AsTask(), observer.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(1));
    Check(watch.Elapsed < TimeSpan.FromSeconds(1) && removed.SequenceEqual(new[] { "child" })
        && observer.SnapshotScopes().Count == 0 && !observer.TryGetScope("child", out _)
        && !callToken.CanBeCanceled && callTimeout == 1500
        && observer.Diagnostic()["drainTimedOut"]?.GetValue<bool>() == false,
        "終了時に観測scopeが残るか、診断の取消しで再利用する接続の送信へ影響した");
    observer.OnEvent("Target.attachedToTarget", Attached("late", "worker"), "page");
    Check(!observer.TryGetScope("late", out _), "終了後のイベントで監視を再開した");
});

await Test("観測用transportが同期停止しても終了処理は有限で診断する", async () =>
{
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var transport = new ObserverTransport((method, _, session, _, _) =>
    {
        if (session == "blocked" && method == "Network.enable")
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new Exception("調査用transportの解放が遅れた");
        }
        return Task.FromResult(new JsonObject());
    });
    var observer = new RelatedTargetObserver(transport, "page", _ => { });
    try
    {
        await observer.Start(default);
        observer.OnEvent("Target.attachedToTarget", Attached("blocked", "worker"), "page");
        await entered.Task;
        await observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(observer.Diagnostic()["drainTimedOut"]?.GetValue<bool>() == true, "同期停止した診断初期化を無期限に待った");
    }
    finally { release.Set(); await observer.DisposeAsync(); }
    await ObserverSettled(() => Json.Number(observer.Diagnostic()["pendingInitializations"]) == 0);
});

await PageStateMonitorTests.Run(Test, Check);
await RelatedNetworkPlaybackTests.Run(Test, Check);

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class ObserverTransport(Func<string, JsonObject?, string?, CancellationToken, int, Task<JsonObject>> call) : ICdpTransport
{
    public Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null, CancellationToken token = default, int timeoutMs = 10000)
        => call(method, parameters, session, token, timeoutMs);
}

// A real loopback WebSocket, built only from the BCL. No Chrome or HTTP URL ACL is required.
sealed class Connection(Cdp client, TcpClient tcp, WebSocket server) : IAsyncDisposable
{
    public Cdp Client { get; } = client;
    public WebSocket Server { get; } = server;
    private bool clientDisposed;

    public static async Task<Connection> Create()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = Accept(listener);
        var client = new Cdp();
        try
        {
            await client.Open($"ws://127.0.0.1:{port}/devtools/browser/test", CancellationToken.None);
            var (tcp, server) = await accepted;
            return new(client, tcp, server);
        }
        catch { await client.DisposeAsync(); throw; }
        finally { listener.Stop(); }
    }

    private static async Task<(TcpClient, WebSocket)> Accept(TcpListener listener)
    {
        var tcp = await listener.AcceptTcpClientAsync();
        try
        {
            var stream = tcp.GetStream();
            var bytes = new List<byte>();
            var one = new byte[1];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (bytes.Count < 16384)
            {
                if (await stream.ReadAsync(one, deadline.Token) == 0) throw new IOException("WebSocket handshake closed");
                bytes.Add(one[0]);
                if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
            }
            var headers = Encoding.ASCII.GetString(bytes.ToArray());
            var keyLine = headers.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var key = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), deadline.Token);
            return (tcp, WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan));
        }
        catch { tcp.Dispose(); throw; }
    }

    public async Task<JsonObject> Read()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult part;
        do
        {
            part = await Server.ReceiveAsync(buffer, deadline.Token);
            if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Expected text CDP request");
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return JsonNode.Parse(message.ToArray())!.AsObject();
    }

    public Task Reply(JsonObject request, JsonObject result) => Send(new JsonObject
    {
        ["id"] = request["id"]!.GetValue<int>(),
        ["result"] = result,
    }.ToJsonString());

    public Task Send(string message) => Server.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeClient()
    {
        if (clientDisposed) return;
        clientDisposed = true;
        await Client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisposeClient(); }
        finally { Server.Dispose(); tcp.Dispose(); }
    }
}

// Windows loopback buffering can accept a large SendAsync even if its peer stops
// reading. This transport deterministically holds the actual send phase so its
// cancellation and disposal contracts are verified without enormous payloads.
sealed class FailingFlushStream : MemoryStream
{
    public override Task FlushAsync(CancellationToken token) => Task.FromException(new IOException("テスト用の flush 失敗"));
}

sealed class BlockedSendSocket : WebSocket
{
    private readonly CancellationTokenSource aborted = new();
    private WebSocketState state = WebSocketState.Open;
    public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => state;
    public override void Abort() { state = WebSocketState.Aborted; if (!Disposed) aborted.Cancel(); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aborted.Token);
        await Task.Delay(Timeout.Infinite, linked.Token);
        throw new InvalidOperationException("blocked transport should never complete a receive");
    }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        SendStarted.TrySetResult();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aborted.Token);
        await Task.Delay(Timeout.Infinite, linked.Token);
    }
    public override void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        state = WebSocketState.Closed;
        aborted.Cancel();
        aborted.Dispose();
    }
}
