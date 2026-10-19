using System.Text.Json.Nodes;

namespace Crer;

/// <summary>対象ページのCSPやJavaScriptから独立した、同じブラウザー内の非表示照合環境を所有する。</summary>
public sealed class TemplateMatcher(ICdpTransport cdp, string pageSession) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly object stateGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private string? target, session;
    private bool ready, disposed;
    private Task? disposal;

    public Task<TemplateResult> MatchAsync(JsonObject options, string? directory, CancellationToken token = default) =>
        Run((call, cancellation) => Template.MatchAsync(call, options, directory, cancellation), token);

    public Task<TemplateMatchesResult> MatchManyAsync(JsonObject options, string? directory, double threshold, double maximum, CancellationToken token = default) =>
        Run((call, cancellation) => Template.MatchManyAsync(call, options, directory, threshold, maximum, cancellation), token);

    private async Task<T> Run<T>(Func<InputCall, CancellationToken, Task<T>> action, CancellationToken token)
    {
        CancellationTokenSource linked;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        }
        using (linked)
        {
            await gate.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                return await action(async (method, parameters) =>
                {
                    if (method != "Runtime.evaluate") return await cdp.Call(method, parameters, pageSession, linked.Token);
                    try { await EnsureRealm(linked.Token); }
                    catch (CrerException error) when (error.Code == 4)
                    { throw new CrerException(3, $"could not initialize the hidden template matcher: {error.Message}"); }
                    return await Evaluate(parameters, linked.Token);
                }, linked.Token);
            }
            finally { gate.Release(); }
        }
    }

    private async Task<JsonObject> Evaluate(JsonObject? parameters, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(10000);
        var contextUnavailable = false;
        try
        {
            while (true)
            {
                // CDP要求がparamsを所有するため、再評価ごとに独立したノードを渡す。
                try { return await cdp.Call("Runtime.evaluate", parameters?.DeepClone().AsObject(), session, deadline.Token); }
                catch (CrerException error) when (IsContextUnavailable(error))
                {
                    // 非表示targetの初回遷移中は、Runtime.enable後も実行先が未作成の場合がある。
                    // 保存済み画像から照合する評価だけを再試行し、撮影や入力は繰り返さない。
                    contextUnavailable = true;
                    await Task.Delay(50, deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new CrerException(3, contextUnavailable
                ? "hidden template matcher timed out waiting for its default execution context"
                : "CDP call timed out: Runtime.evaluate");
        }
    }

    private static bool IsContextUnavailable(CrerException error) => error.Code == 4 &&
        (error.Message.Contains("Cannot find default execution context", StringComparison.OrdinalIgnoreCase) ||
         error.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
         error.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase));

    private async Task EnsureRealm(CancellationToken token)
    {
        if (target == null)
        {
            var created = await cdp.Call("Target.createTarget", new()
            { ["url"] = "data:text/html,", ["background"] = true, ["hidden"] = true }, token: token);
            target = Json.Text(created["targetId"]);
            if (target.Length == 0) throw new CrerException(3, "CfT did not expose its owned hidden template matcher");
        }
        if (session == null)
        {
            var attached = await cdp.Call("Target.attachToTarget", new() { ["targetId"] = target, ["flatten"] = true }, token: token);
            session = Json.Text(attached["sessionId"]);
            if (session.Length == 0) throw new CrerException(3, "CfT did not attach its owned hidden template matcher");
        }
        if (!ready)
        {
            await cdp.Call("Runtime.enable", session: session, token: token);
            ready = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (stateGate) return new(disposal ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        disposed = true;
        try { lifetime.Cancel(); }
        finally
        {
            // 取得・評価の取消しを待ってから、自分が作成したtargetだけを閉じる。
            await gate.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(target))
                    await cdp.Call("Target.closeTarget", new() { ["targetId"] = target }, token: CancellationToken.None, timeoutMs: 1500);
            }
            finally { gate.Release(); lifetime.Dispose(); }
        }
    }
}
