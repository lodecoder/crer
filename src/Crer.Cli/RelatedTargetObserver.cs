using System.Text.Json.Nodes;

namespace Crer;

/// <summary>対象ページに関連付けられた iframe と worker の通信観測だけを追加する。</summary>
internal sealed class RelatedTargetObserver(ICdpTransport transport, string mainSession, Action<string> detached) : IAsyncDisposable
{
    private const int InitializationTimeoutMs = 1500;
    private sealed class Scope(string id, string type, string parent, CancellationToken lifetime)
    {
        public readonly string Id = id, Type = type, Parent = parent;
        public readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Scope> scopes = new(StringComparer.Ordinal);
    private readonly HashSet<Task> pending = [];
    private readonly Dictionary<string, int> failureTypes = new(StringComparer.Ordinal);
    private Task? starting, disposal;
    private int attachedCount, detachedCount, networkEnabledCount, runtimeEnabledCount, autoAttachEnabledCount, failures;
    private bool mainEnabled, disposed, drainTimedOut;

    public Task Start(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            starting ??= Queue(() => EnableMain(token));
            return starting.WaitAsync(token);
        }
    }

    private async Task EnableMain(CancellationToken token)
    {
        if (await Enable("Target.setAutoAttach", AutoAttach(), mainSession, token, true))
            lock (gate) mainEnabled = true;
    }

    private static JsonObject AutoAttach() => new()
    {
        ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true,
        ["filter"] = new JsonArray(
            new JsonObject { ["type"] = "iframe" }, new JsonObject { ["type"] = "worker" },
            new JsonObject { ["type"] = "shared_worker" }, new JsonObject { ["type"] = "service_worker" },
            new JsonObject { ["exclude"] = true }),
    };

    private Task Queue(Func<Task> action)
    {
        // 受信イベントのコールスタックでは CDP 要求やその返信を待たない。
        var task = Task.Run(action);
        pending.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (gate) pending.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public void OnEvent(string method, JsonObject p, string? parentSession)
    {
        if (method is not ("Target.attachedToTarget" or "Target.detachedFromTarget")) return;
        List<Scope>? removed = null;
        try
        {
            lock (gate)
            {
                if (disposed || !Known(parentSession)) return;
                var id = String(p["sessionId"]);
                if (id.Length == 0 || id == mainSession) return;
                if (method == "Target.attachedToTarget")
                {
                    var type = String(p["targetInfo"]?["type"]);
                    if (type is not ("iframe" or "worker" or "shared_worker" or "service_worker") || scopes.ContainsKey(id)) return;
                    var scope = new Scope(id, type, parentSession!, lifetime.Token);
                    scopes.Add(id, scope); attachedCount++;
                    _ = Queue(() => Initialize(scope));
                }
                else if (scopes.ContainsKey(id))
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal) { id };
                    bool added;
                    do
                    {
                        added = false;
                        foreach (var scope in scopes.Values)
                            if (ids.Contains(scope.Parent) && ids.Add(scope.Id)) added = true;
                    } while (added);
                    removed = ids.Select(key => scopes[key]).ToList();
                    foreach (var scope in removed) scopes.Remove(scope.Id);
                    detachedCount += removed.Count;
                }
            }
            if (removed != null) foreach (var scope in removed) Remove(scope);
        }
        catch (Exception error) { Failure(error.GetType().Name); }
    }

    private bool Known(string? id) => id != null && (id == mainSession || scopes.ContainsKey(id));
    private static string String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private async Task Initialize(Scope scope)
    {
        try
        {
            if (await Enable("Network.enable", new(), scope.Id, scope.Lifetime.Token, false))
                lock (gate) networkEnabledCount++;
            if (await Enable("Runtime.enable", new(), scope.Id, scope.Lifetime.Token, false))
                lock (gate) runtimeEnabledCount++;
            // Network 非対応の worker でも、さらに関連付く target の観測は独立して試みる。
            if (await Enable("Target.setAutoAttach", AutoAttach(), scope.Id, scope.Lifetime.Token, false))
                lock (gate) autoAttachEnabledCount++;
        }
        catch (Exception error) { Failure(error.GetType().Name); }
        finally { scope.Lifetime.Dispose(); }
    }

    private async Task<bool> Enable(string method, JsonObject parameters, string session, CancellationToken token, bool propagateCancellation)
    {
        try
        {
            if (token.IsCancellationRequested)
            {
                if (propagateCancellation) token.ThrowIfCancellationRequested();
                return false;
            }
            lock (gate) if (disposed) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(InitializationTimeoutMs);
            // 監視停止の取消しを送信途中へ伝えると、再利用する CDP 接続まで中断する。
            // transport 自身の固定期限は残し、監視側の待機だけを取り消す。
            var call = transport.Call(method, parameters, session, timeoutMs: InitializationTimeoutMs);
            // transport が取消しを無視しても診断用の初期化を無期限に残さない。
            _ = call.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await call.WaitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || lifetime.IsCancellationRequested)
        {
            if (propagateCancellation) token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { Failure(nameof(TimeoutException)); }
        catch (Exception error) { Failure(error.GetType().Name); }
        return false;
    }

    private void Remove(Scope scope)
    {
        try { scope.Lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Failure(error.GetType().Name); }
        try { detached(scope.Id); }
        catch (Exception error) { Failure(error.GetType().Name); }
    }

    public bool TryGetScope(string? id, out string targetType)
    {
        lock (gate)
        {
            targetType = "";
            if (disposed || id == null) return false;
            if (id == mainSession) { targetType = "page"; return true; }
            if (!scopes.TryGetValue(id, out var scope)) return false;
            targetType = scope.Type;
            return true;
        }
    }

    public IReadOnlyList<(string SessionId, string TargetType)> SnapshotScopes()
    {
        lock (gate)
            return disposed ? [] : new[] { (mainSession, "page") }.Concat(scopes.Values.Select(scope => (scope.Id, scope.Type))).ToArray();
    }

    private void Failure(string type)
    {
        lock (gate)
        {
            failures++;
            failureTypes[type] = failureTypes.GetValueOrDefault(type) + 1;
        }
    }

    public JsonObject Diagnostic()
    {
        lock (gate)
        {
            var types = new JsonObject();
            foreach (var (type, count) in failureTypes) types[type] = count;
            var active = new JsonObject();
            foreach (var group in scopes.Values.GroupBy(scope => scope.Type)) active[group.Key] = group.Count();
            return new()
            {
                ["mainAutoAttachEnabled"] = mainEnabled, ["attached"] = attachedCount, ["detached"] = detachedCount,
                ["active"] = active, ["networkEnabled"] = networkEnabledCount, ["runtimeEnabled"] = runtimeEnabledCount, ["recursiveAutoAttachEnabled"] = autoAttachEnabledCount,
                ["pendingInitializations"] = pending.Count, ["failures"] = failures, ["failureTypes"] = types,
                ["disposed"] = disposed, ["drainTimedOut"] = drainTimedOut,
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal != null) return new(disposal);
            disposed = true;
            var remaining = scopes.Values.ToArray();
            scopes.Clear();
            var tasks = pending.ToArray();
            // detach 通知先も別のロックを使うため、通知や取消しをこのロック内で開始しない。
            disposal = Task.Run(() => DisposeCore(remaining, tasks));
            return new(disposal);
        }
    }

    private async Task DisposeCore(Scope[] remaining, Task[] tasks)
    {
        try { lifetime.Cancel(); }
        catch (Exception error) { Failure(error.GetType().Name); }
        foreach (var scope in remaining) Remove(scope);
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { lock (gate) drainTimedOut = true; }
        catch (OperationCanceledException) { }
        catch (Exception error) { Failure(error.GetType().Name); }
        lifetime.Dispose();
    }
}
