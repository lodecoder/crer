using System.Diagnostics;
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

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

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
