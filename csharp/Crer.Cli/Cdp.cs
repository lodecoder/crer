using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Crer;

public sealed class Cdp : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? receiver;
    private int sequence;
    private Exception? failure;
    private readonly TaskCompletionSource<Exception> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int PendingCount => pending.Count;
    public Task<Exception> Disconnected => disconnected.Task;
    public event Action<string, JsonObject, string?>? Event;

    public async Task Open(string url, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await socket.ConnectAsync(new Uri(url), deadline.Token); }
        catch (Exception e) when (!token.IsCancellationRequested) { throw new CrerException(3, $"CDP WebSocket connection failed: {e.Message}"); }
        receiver = Receive();
    }

    public async Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null,
        CancellationToken token = default, int timeoutMs = 10000)
    {
        token.ThrowIfCancellationRequested();
        if (failure != null || socket.State != WebSocketState.Open) throw new CrerException(3, "CDP connection is closed");
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (session != null) message["sessionId"] = session;
        try
        {
            await sendLock.WaitAsync(token);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, token); }
            finally { sendLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), token);
        }
        catch (TimeoutException) { throw new CrerException(3, $"CDP call timed out: {method}"); }
        catch (WebSocketException e) { Fail(new CrerException(3, $"CDP send failed: {e.Message}")); throw failure!; }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task Receive()
    {
        var buffer = new byte[65536];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer, lifetime.Token);
                    if (part.MessageType == WebSocketMessageType.Close) throw new CrerException(3, "CDP WebSocket connection closed");
                    stream.Write(buffer, 0, part.Count);
                    if (stream.Length > 64 * 1024 * 1024) throw new CrerException(3, "CDP message exceeded limit");
                } while (!part.EndOfMessage);
                var msg = JsonNode.Parse(stream.ToArray())!.AsObject();
                if (msg["method"] != null)
                {
                    Event?.Invoke(Json.Text(msg["method"]), msg["params"]?.AsObject() ?? new(), msg["sessionId"]?.GetValue<string>());
                    continue;
                }
                if (msg["id"] is not JsonNode id || !pending.TryRemove(id.GetValue<int>(), out var completion)) continue;
                if (msg["error"] != null) completion.TrySetException(new CrerException(4, $"CDP: {msg["error"]!["message"]}"));
                else completion.TrySetResult(msg["result"]?.DeepClone().AsObject() ?? new());
            }
        }
        catch (Exception e) { Fail(e is CrerException ? e : new CrerException(3, $"CDP connection lost: {e.Message}")); }
    }

    private void Fail(Exception e)
    {
        Interlocked.CompareExchange(ref failure, e, null);
        disconnected.TrySetResult(failure!);
        foreach (var entry in pending) if (pending.TryRemove(entry.Key, out var task)) task.TrySetException(failure!);
    }

    public async ValueTask DisposeAsync()
    {
        Fail(new CrerException(3, "CDP connection closed by client"));
        lifetime.Cancel();
        socket.Abort();
        if (receiver != null) await receiver;
        socket.Dispose();
        lifetime.Dispose();
        sendLock.Dispose();
    }
}
