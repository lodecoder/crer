using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("Crer.CdpTests")]

namespace Crer;

public sealed class Cdp : IAsyncDisposable, IUrlBlockerTransport
{
    private readonly WebSocket socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateGate = new();
    private Task? receiver;
    private Task? disposal;
    private int activeOperations;
    private TaskCompletionSource? drained;
    private int sequence;
    private Exception? failure;
    private readonly TaskCompletionSource<Exception> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int PendingCount => pending.Count;
    public Task<Exception> Disconnected => disconnected.Task;
    public event Action<string, JsonObject, string?>? Event;

    public Cdp() : this(new ClientWebSocket()) { }
    internal Cdp(WebSocket transport) { socket = transport; }

    public async Task Open(string url, CancellationToken token)
    {
        CancellationToken lifetimeToken;
        lock (stateGate)
        {
            if (failure != null) throw failure;
            activeOperations++;
            lifetimeToken = lifetime.Token;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (socket is ClientWebSocket client) await client.ConnectAsync(new Uri(url), deadline.Token);
                else if (socket.State != WebSocketState.Open) throw new CrerException(3, "CDP transport is not open");
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            { throw Failure() ?? new CrerException(3, $"CDP WebSocket connection failed: {e.Message}"); }
            lock (stateGate)
            {
                if (failure != null) throw failure;
                receiver = Receive();
            }
        }
        finally { OperationEnded(); }
    }

    public async Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null,
        CancellationToken token = default, int timeoutMs = 10000)
    {
        token.ThrowIfCancellationRequested();
        if (timeoutMs < Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        int id;
        CancellationToken lifetimeToken;
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stateGate)
        {
            if (failure != null) throw failure;
            if (socket.State != WebSocketState.Open) throw new CrerException(3, "CDP connection is closed");
            id = Interlocked.Increment(ref sequence);
            pending[id] = completion;
            activeOperations++;
            lifetimeToken = lifetime.Token;
        }
        var sending = false;
        CancellationTokenSource? timeout = null;
        try
        {
            timeout = new CancellationTokenSource();
            if (timeoutMs != Timeout.Infinite) timeout.CancelAfter(timeoutMs);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken, timeout.Token);
            var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            if (session != null) message["sessionId"] = session;
            await sendLock.WaitAsync(operation.Token);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
                operation.Token.ThrowIfCancellationRequested();
                sending = true;
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, operation.Token);
                sending = false;
            }
            finally { sendLock.Release(); }
            return await completion.Task.WaitAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation while sending can leave a partial WebSocket message. The
            // transport cannot safely carry another command after that ambiguity.
            var existing = Failure();
            var timedOut = timeout?.IsCancellationRequested == true;
            var timeoutError = new CrerException(3, $"CDP call timed out: {method}");
            if (sending && existing == null)
            {
                Fail(token.IsCancellationRequested ? new CrerException(3, "CDP send was cancelled") : timeoutError);
                socket.Abort();
            }
            token.ThrowIfCancellationRequested();
            if (timedOut) throw timeoutError;
            throw existing ?? Failure() ?? new CrerException(3, "CDP connection is closed");
        }
        catch (WebSocketException e)
        {
            Fail(new CrerException(3, $"CDP send failed: {e.Message}"));
            token.ThrowIfCancellationRequested();
            throw Failure()!;
        }
        catch (ObjectDisposedException) when (Failure() != null) { throw Failure()!; }
        finally
        {
            timeout?.Dispose();
            pending.TryRemove(id, out _);
            OperationEnded();
        }
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
        lock (stateGate)
        {
            failure ??= e;
            disconnected.TrySetResult(failure);
            foreach (var entry in pending) if (pending.TryRemove(entry.Key, out var task)) task.TrySetException(failure);
            // All queue/send/response waits share this token, including calls whose
            // user token is CancellationToken.None (for example input release).
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
        }
    }

    private Exception? Failure() { lock (stateGate) return failure; }

    private void OperationEnded()
    {
        lock (stateGate)
            if (--activeOperations == 0) drained?.TrySetResult();
    }

    public void Abort(Exception error)
    {
        Fail(error);
        try { socket.Abort(); } catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (stateGate) return new ValueTask(disposal ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        Fail(new CrerException(3, "CDP connection closed by client"));
        socket.Abort();
        if (receiver != null) await receiver;
        Task wait;
        lock (stateGate) wait = activeOperations == 0 ? Task.CompletedTask : (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        await wait;
        socket.Dispose();
        lifetime.Dispose();
        sendLock.Dispose();
    }
}
