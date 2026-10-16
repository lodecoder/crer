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
                    return await cdp.Call(method, parameters, session, linked.Token);
                }, linked.Token);
            }
            finally { gate.Release(); }
        }
    }

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
