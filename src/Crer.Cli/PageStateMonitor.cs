using System.Text.Json.Nodes;

namespace Crer;

/// <summary>入力や前景状態を変更せず、ページと iframe の状態を低頻度で観測する。</summary>
internal sealed class PageStateMonitor : IAsyncDisposable
{
    private readonly ICdpTransport transport;
    private readonly Func<IReadOnlyList<(string SessionId, string TargetType)>> scopes;
    private readonly AsyncDiagnosticLog log;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task runner;
    private long samples, unavailable;
    private int stopped;

    internal PageStateMonitor(ICdpTransport transport,
        Func<IReadOnlyList<(string SessionId, string TargetType)>> scopes, AsyncDiagnosticLog log,
        TimeSpan? interval = null)
    {
        this.transport = transport;
        this.scopes = scopes;
        this.log = log;
        this.interval = interval ?? TimeSpan.FromSeconds(10);
        runner = Task.Run(Run);
    }

    internal JsonObject Diagnostic() => new()
    {
        ["samples"] = Interlocked.Read(ref samples), ["unavailable"] = Interlocked.Read(ref unavailable),
        ["stopped"] = runner.IsCompleted,
    };

    private async Task Run()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                foreach (var scope in scopes())
                {
                    if (lifetime.IsCancellationRequested) break;
                    if (scope.TargetType is not ("page" or "iframe")) continue;
                    var record = new JsonObject
                    {
                        ["at"] = DateTime.UtcNow.ToString("O"), ["method"] = "Page.stateSample",
                        ["sessionId"] = scope.SessionId, ["targetType"] = scope.TargetType,
                    };
                    try
                    {
                        // 監視停止の取消しを送信途中へ伝えず、再利用中の接続を傷めない。
                        // 応答待ちは最大1秒で、ページが応答しなくても操作を待たせない。
                        var reply = await transport.Call("Runtime.evaluate", new()
                        {
                            ["expression"] = "({ready:document.readyState,visibility:document.visibilityState,focused:document.hasFocus(),online:navigator.onLine})",
                            ["returnByValue"] = true, ["silent"] = true,
                        }, scope.SessionId, timeoutMs: 1000);
                        if (reply["exceptionDetails"] != null || reply["result"]?["value"] is not JsonObject value)
                            throw new CrerException(4, "page state unavailable");
                        record["status"] = "ok";
                        // 任意のページ値や例外文を保存せず、許可した状態値だけ取り出す。
                        var ready = Json.Text(value["ready"]);
                        if (ready is "loading" or "interactive" or "complete") record["ready"] = ready;
                        var visibility = Json.Text(value["visibility"]);
                        if (visibility is "visible" or "hidden" or "prerender") record["visibility"] = visibility;
                        if (value["focused"] is JsonValue focused && focused.TryGetValue<bool>(out var hasFocus)) record["focused"] = hasFocus;
                        if (value["online"] is JsonValue online && online.TryGetValue<bool>(out var isOnline)) record["online"] = isOnline;
                    }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref unavailable);
                        record["status"] = "unavailable";
                        record["errorType"] = error.GetType().Name;
                    }
                    Interlocked.Increment(ref samples);
                    log.Append(record.ToJsonString());
                }
                await Task.Delay(interval, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lifetime.Cancel();
        // 実 CDP の評価期限より長い終了予算を設け、監視で後始末を無期限に待たせない。
        try { await runner.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        if (runner.IsCompleted) lifetime.Dispose();
    }
}
