using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Crer;

public interface IUrlBlockerTransport
{
    event Action<string, JsonObject, string?>? Event;
    Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null, CancellationToken token = default, int timeoutMs = 10000);
    void Abort(Exception error);
}

/// <summary>Owns interception and a hidden Chromium matcher in this browser/profile only.</summary>
public sealed class UrlBlocker(IUrlBlockerTransport cdp, string session) : IAsyncDisposable
{
    private bool enabled, disposed, subscribed;
    private Exception? error;
    private string? matcherTarget, matcherSession;
    private readonly ConcurrentDictionary<long, Task> pending = new();
    private long sequence;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateGate = new();
    public string? MatcherTargetId => matcherTarget;

    public void AssertHealthy()
    {
        if (error != null) throw new CrerException(3, $"URL blocking failed: {error.Message}");
    }

    public async Task Configure(JsonArray? sources, CancellationToken token)
    {
        AssertHealthy();
        var next = sources?.Count > 0;
        if (next && matcherSession == null)
        {
            // A navigation destroys its previous execution world before Fetch can resolve
            // the main request. An owned hidden `other` target keeps V8 available without
            // another page/tab/window and without converting ECMAScript to .NET Regex.
            var target = await cdp.Call("Target.createTarget", new()
            { ["url"] = "data:text/html,", ["background"] = true, ["hidden"] = true }, token: token);
            matcherTarget = Json.Text(target["targetId"]);
            var attached = await cdp.Call("Target.attachToTarget", new()
            { ["targetId"] = matcherTarget, ["flatten"] = true }, token: token);
            matcherSession = Json.Text(attached["sessionId"]);
            if (matcherTarget.Length == 0 || matcherSession.Length == 0) throw new CrerException(3, "CfT did not expose its owned hidden URL matcher");
            await cdp.Call("Runtime.enable", session: matcherSession, token: token);
        }
        if (matcherSession != null)
        {
            var compiled = await cdp.Call("Runtime.evaluate", new()
            { ["expression"] = "globalThis.__crerPatterns=" + (sources?.ToJsonString() ?? "[]") + ".map(s=>new RegExp(s));void 0", ["returnByValue"] = true }, matcherSession, token);
            if (compiled["exceptionDetails"] != null) throw new CrerException(3, "CfT rejected the URL blocking expressions");
        }
        lock (stateGate)
        {
            if (!subscribed) { cdp.Event += OnEvent; subscribed = true; }
        }
        if (enabled == next) return;
        await cdp.Call("Network.setBypassServiceWorker", new() { ["bypass"] = next }, session, token);
        await cdp.Call("Network.setCacheDisabled", new() { ["cacheDisabled"] = next }, session, token);
        await cdp.Call(next ? "Fetch.enable" : "Fetch.disable", next ? new()
        { ["patterns"] = new JsonArray((JsonNode)new JsonObject { ["urlPattern"] = "*", ["requestStage"] = "Request" }) } : new(), session, token);
        enabled = next;
        AssertHealthy();
    }

    private void OnEvent(string method, JsonObject data, string? id)
    {
        if (id != session || method != "Fetch.requestPaused") return;
        lock (stateGate)
        {
            if (disposed) return;
            var serial = Interlocked.Increment(ref sequence);
            var task = Handle(data.DeepClone().AsObject());
            pending[serial] = task;
            _ = task.ContinueWith(_ => pending.TryRemove(serial, out var ignored), TaskScheduler.Default);
        }
    }

    private async Task Handle(JsonObject data)
    {
        try
        {
            var value = await cdp.Call("Runtime.evaluate", new()
            { ["expression"] = "globalThis.__crerPatterns.some(p=>p.test(" + (data["request"]?["url"]?.ToJsonString() ?? "\"\"") + "))", ["returnByValue"] = true }, matcherSession, lifetime.Token);
            if (value["exceptionDetails"] != null) throw new CrerException(3, "URL expression evaluation failed");
            var blocked = value["result"]?["value"]?.GetValue<bool>() ?? throw new CrerException(3, "URL matcher did not return a decision");
            await cdp.Call(blocked ? "Fetch.failRequest" : "Fetch.continueRequest", blocked ? new()
            { ["requestId"] = data["requestId"]!.DeepClone(), ["errorReason"] = "BlockedByClient" } : new()
            { ["requestId"] = data["requestId"]!.DeepClone() }, session, lifetime.Token);
        }
        catch (Exception e) when (!lifetime.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref error, e, null);
            cdp.Abort(new CrerException(3, $"URL blocking failed: {e.Message}"));
        }
        // CDP may complete a pending evaluation with a connection error while its
        // cancellation is racing shutdown. Every shutdown response must be drained.
        catch (Exception) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        var failures = new List<Exception>();
        lock (stateGate)
        {
            if (disposed) return;
            disposed = true;
            if (subscribed) cdp.Event -= OnEvent;
            try { lifetime.Cancel(); } catch (Exception error) { failures.Add(error); }
            tasks = pending.Values.ToArray();
        }
        try
        {
            try { await Task.WhenAll(tasks); } catch (Exception error) { failures.Add(error); }
        }
        finally
        {
            try
            {
                if (matcherTarget != null)
                {
                    try { await cdp.Call("Target.closeTarget", new() { ["targetId"] = matcherTarget }, token: CancellationToken.None, timeoutMs: 1500); }
                    catch (CrerException e) when (e.Code is 3 or 4) { }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            finally { lifetime.Dispose(); }
        }
        if (failures.Count > 0)
        {
            if (failures.Count > 1) failures[0].Data["cleanupErrors"] = failures.Skip(1).Select(error => error.Message).ToArray();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
    }
}
