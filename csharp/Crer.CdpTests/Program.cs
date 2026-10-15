using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
