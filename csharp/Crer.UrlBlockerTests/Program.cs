using System.Text.Json.Nodes;
using Crer;

var passed = 0; var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); failed++; }
}
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Error(Func<Task> body, string message)
{
    try { await body(); }
    catch (Exception error) { Check(error.Message.Contains(message, StringComparison.Ordinal), "wrong diagnostic: " + error.Message); return; }
    throw new InvalidOperationException("expected failure: " + message);
}
await Test("exact regex sources compile in an owned hidden matcher and repeated decisions route to the page", async () =>
{
    var sources = new JsonArray(@"^https://cdn\.example/images/(ad|banner)\d+\.(png|jpg)(\?.*)?$", @"/track\?");
    var cdp = new Transport { ExpectedPatterns = sources.ToJsonString() };
    await using var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(sources, default);
    foreach (var url in new[] { "https://cdn.example/images/ad1.png", "https://cdn.example/images/banner2.jpg?v=1", "https://site.example/track?id=1", "https://cdn.example/images/ad1.png" })
    {
        cdp.Decisions.Enqueue(true); cdp.Request(url);
        Check(cdp.Calls[^1].Method == "Fetch.failRequest", "matching request was not failed");
        Check(JsonNode.DeepEquals(cdp.Calls[^1].Parameters, new JsonObject { ["requestId"] = "request", ["errorReason"] = "BlockedByClient" }), "blocked request parameters");
    }
    foreach (var url in new[] { "https://cdn.example/images/logo.png", "https://other.example/images/ad1.png", "https://cdn.example/images/ad1.png/extra" })
    { cdp.Decisions.Enqueue(false); cdp.Request(url); Check(cdp.Calls[^1].Method == "Fetch.continueRequest", "allowed request was not continued"); }
    var count = cdp.Calls.Count; cdp.Request("https://site.example/track?id=1", "other-page");
    Check(cdp.Calls.Count == count, "foreign page request intercepted");
    Check(cdp.Compilations.Single() == "globalThis.__crerPatterns=" + sources.ToJsonString() + ".map(s=>new RegExp(s));void 0", "ECMAScript regex sources were converted");
    Check(cdp.Calls.Where(call => call.Method.StartsWith("Fetch.", StringComparison.Ordinal) || call.Method.StartsWith("Network.", StringComparison.Ordinal)).All(call => call.Session == "page"), "interception escaped owned page session");
});
await Test("reused configuration replaces matcher patterns and restores cache/service workers when disabled", async () =>
{
    var cdp = new Transport(); await using var blocker = new UrlBlocker(cdp, "page");
    await blocker.Configure(null, default); Check(cdp.Calls.Count == 0, "empty configuration changed browser state");
    cdp.ExpectedPatterns = "[\"old\"]"; await blocker.Configure(new JsonArray("old"), default);
    var setup = cdp.Calls.Where(call => call.Method.StartsWith("Network.", StringComparison.Ordinal) || call.Method.StartsWith("Fetch.", StringComparison.Ordinal)).ToArray();
    Check(setup.Select(call => call.Method).SequenceEqual(new[] { "Network.setBypassServiceWorker", "Network.setCacheDisabled", "Fetch.enable" }), "enable sequence differs");
    Check(setup[0].Parameters["bypass"]!.GetValue<bool>() && setup[1].Parameters["cacheDisabled"]!.GetValue<bool>(), "cache/service worker bypass missing");
    Check(cdp.Calls.FindIndex(call => call.Method == "Runtime.evaluate") < cdp.Calls.FindIndex(call => call.Method == "Fetch.enable"), "navigation interception started before exact matcher compiled");
    var enabledCalls = cdp.Calls.Count;
    cdp.ExpectedPatterns = "[\"new\"]"; await blocker.Configure(new JsonArray("new"), default);
    Check(cdp.Calls.Count == enabledCalls + 1 && cdp.Calls[^1].Method == "Runtime.evaluate", "pattern replacement reset interception or failed to compile");
    Check(cdp.Calls.Count(call => call.Method == "Target.createTarget") == 1, "replacement created extra matcher targets");
    cdp.Decisions.Enqueue(false); cdp.Request("https://example/old"); Check(cdp.Calls[^1].Method == "Fetch.continueRequest", "obsolete pattern remained");
    cdp.Decisions.Enqueue(true); cdp.Request("https://example/new"); Check(cdp.Calls[^1].Method == "Fetch.failRequest", "new pattern not applied");
    cdp.ExpectedPatterns = "[]"; await blocker.Configure(new(), default);
    Check(cdp.Calls.TakeLast(3).Select(call => call.Method).SequenceEqual(new[] { "Network.setBypassServiceWorker", "Network.setCacheDisabled", "Fetch.disable" }), "disable sequence differs");
    Check(!cdp.Calls[^3].Parameters["bypass"]!.GetValue<bool>() && !cdp.Calls[^2].Parameters["cacheDisabled"]!.GetValue<bool>(), "cache/service worker state was not restored");
    cdp.Decisions.Enqueue(false); cdp.Request("https://example/new"); Check(cdp.Calls[^1].Method == "Fetch.continueRequest", "queued request after disable not released");
});
await Test("interception errors abort the connection and remain terminal environment failures", async () =>
{
    foreach (var method in new[] { "Fetch.failRequest", "Fetch.continueRequest" })
    {
        var cdp = new Transport { ExpectedPatterns = "[\"blocked\"]", RejectMethod = method };
        await using var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(new JsonArray("blocked"), default);
        cdp.Decisions.Enqueue(method == "Fetch.failRequest"); cdp.Request("https://example/" + (method == "Fetch.failRequest" ? "blocked" : "allowed"));
        Check(cdp.Aborted is CrerException { Code: 3 }, "interception failure did not abort as environment failure");
        await Error(() => Task.Run(blocker.AssertHealthy), "URL blocking failed");
        await Error(() => blocker.Configure(new(), default), "URL blocking failed");
    }
});
await Test("setup errors propagate and retry does not create a second hidden matcher", async () =>
{
    var cdp = new Transport { RejectMethod = "Fetch.enable" }; await using var blocker = new UrlBlocker(cdp, "page");
    await Error(() => blocker.Configure(new JsonArray("blocked"), default), "interception rejected");
    cdp.RejectMethod = null; await blocker.Configure(new JsonArray("blocked"), default);
    Check(cdp.Calls.Count(call => call.Method == "Fetch.enable") == 2, "failed setup was treated as enabled");
    Check(cdp.Calls.Count(call => call.Method == "Target.createTarget") == 1, "setup retry leaked an extra matcher");
});
await Test("main-page context destruction cannot block or replace the hidden URL matching realm", async () =>
{
    var cdp = new Transport { ExpectedPatterns = "[\"blocked\"]" };
    await using var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(new JsonArray("blocked"), default);
    cdp.Emit("Runtime.executionContextsCleared", new());
    cdp.Emit("Runtime.executionContextDestroyed", new() { ["executionContextId"] = 123 });
    cdp.Decisions.Enqueue(true); cdp.Request("https://example/blocked");
    Check(cdp.Calls[^1].Method == "Fetch.failRequest" && cdp.Aborted == null, "page navigation destroyed URL matcher availability");
    Check(cdp.Calls.All(call => call.Method is not ("Page.getFrameTree" or "Page.createIsolatedWorld")), "paused main request depends on its execution world");
    Check(cdp.Calls.Count(call => call.Method == "Target.createTarget") == 1, "navigation created another matcher");
});
await Test("compile exceptions retain owned matcher identity for cleanup and do not enable Fetch", async () =>
{
    var cdp = new Transport { CompileError = true }; var blocker = new UrlBlocker(cdp, "page");
    await Error(() => blocker.Configure(new JsonArray("blocked"), default), "CfT rejected");
    Check(cdp.Calls.All(call => call.Method != "Fetch.enable"), "Fetch started despite matcher compilation failure");
    await blocker.DisposeAsync();
    Check(cdp.Calls[^1].Method == "Target.closeTarget" && cdp.Calls[^1].Session == null, "partially configured matcher was not closed");
});
await Test("dispose cancels and drains active evaluations then closes only the owned matcher once", async () =>
{
    var cdp = new Transport { ExpectedPatterns = "[\"blocked\"]", BlockEvaluation = true };
    var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(new JsonArray("blocked"), default);
    cdp.Request("https://example/blocked");
    Check(cdp.EvaluationToken.CanBeCanceled && !cdp.EvaluationToken.IsCancellationRequested, "evaluation has no lifetime token");
    await blocker.DisposeAsync(); await blocker.DisposeAsync();
    Check(cdp.EvaluationToken.IsCancellationRequested && cdp.Aborted == null, "dispose did not cancel cleanly");
    Check(cdp.Calls.Count(call => call.Method == "Target.closeTarget") == 1 && cdp.Calls[^1].Parameters["targetId"]!.GetValue<string>() == "owned-matcher", "matcher cleanup ownership changed");
    var count = cdp.Calls.Count; cdp.Request("https://example/blocked"); Check(cdp.Calls.Count == count, "disposed blocker retained event listener");
});
await Test("evaluation exceptions and missing decisions fail closed instead of allowing traffic", async () =>
{
    foreach (var missing in new[] { false, true })
    {
        var cdp = new Transport { ExpectedPatterns = "[\"blocked\"]", EvaluationError = !missing, MissingDecision = missing };
        await using var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(new JsonArray("blocked"), default);
        cdp.Request("https://example/blocked");
        Check(cdp.Aborted is CrerException { Code: 3 }, "JavaScript regex evaluation failed open");
        Check(cdp.Calls.All(call => call.Method is not ("Fetch.continueRequest" or "Fetch.failRequest")), "unknown decision released traffic");
    }
});
await Test("noncancellation CDP failure during dispose still drains and closes the owned matcher", async () =>
{
    var cdp = new Transport { BlockEvaluation = true, DisconnectOnCancellation = true };
    var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(new JsonArray("blocked"), default);
    cdp.Request("https://example/blocked");
    await blocker.DisposeAsync();
    Check(cdp.Calls.Count(call => call.Method == "Target.closeTarget") == 1 && cdp.Aborted == null, "shutdown disconnect skipped matcher close or was reported as live interception failure");
    Check(cdp.EvaluationCompleted, "shutdown did not await the pending evaluation");
});
await Test("cancellation callback faults cannot skip matcher cleanup", async () =>
{
    var cdp = new Transport { BlockEvaluation = true, FaultCancellationCallback = true };
    var blocker = new UrlBlocker(cdp, "page"); await blocker.Configure(new JsonArray("blocked"), default);
    cdp.Request("https://example/blocked");
    await Error(() => blocker.DisposeAsync().AsTask(), "callback failure");
    Check(cdp.Calls.Count(call => call.Method == "Target.closeTarget") == 1 && cdp.EvaluationCompleted, "callback failure bypassed task drain or owned matcher cleanup");
});
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed record Call(string Method, JsonObject Parameters, string? Session);
sealed class Transport : IUrlBlockerTransport
{
    public event Action<string, JsonObject, string?>? Event;
    public List<Call> Calls { get; } = [];
    public List<string> Compilations { get; } = [];
    public Queue<bool> Decisions { get; } = new();
    public string? ExpectedPatterns { get; set; }
    public string? RejectMethod { get; set; }
    public string? ExpectedUrl { get; set; }
    public bool BlockEvaluation { get; set; }
    public bool CompileError { get; set; }
    public bool EvaluationError { get; set; }
    public bool MissingDecision { get; set; }
    public bool DisconnectOnCancellation { get; set; }
    public bool FaultCancellationCallback { get; set; }
    public bool EvaluationCompleted { get; private set; }
    public CancellationToken EvaluationToken { get; private set; }
    public Exception? Aborted { get; private set; }
    public Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null, CancellationToken token = default, int timeoutMs = 10000)
    {
        parameters ??= new(); Calls.Add(new(method, parameters.DeepClone().AsObject(), session));
        if (method == RejectMethod) return Task.FromException<JsonObject>(new IOException("interception rejected"));
        if (method.StartsWith("Target.", StringComparison.Ordinal) && session != null) throw new Exception("browser Target operation used page session");
        if (method == "Target.createTarget")
        {
            if (parameters["hidden"]?.GetValue<bool>() != true || parameters["background"]?.GetValue<bool>() != true || parameters["url"]?.GetValue<string>() != "data:text/html,") throw new Exception("matcher created a visible page");
            return Task.FromResult(new JsonObject { ["targetId"] = "owned-matcher" });
        }
        if (method == "Target.attachToTarget")
        {
            if (parameters["targetId"]?.GetValue<string>() != "owned-matcher" || parameters["flatten"]?.GetValue<bool>() != true) throw new Exception("matcher attachment ownership changed");
            return Task.FromResult(new JsonObject { ["sessionId"] = "matcher-session" });
        }
        if (method == "Target.closeTarget" && parameters["targetId"]?.GetValue<string>() != "owned-matcher") throw new Exception("closed an unowned target");
        if (method == "Runtime.enable" && session != "matcher-session") throw new Exception("enabled a foreign runtime");
        if (method == "Runtime.evaluate")
        {
            if (session != "matcher-session") throw new Exception("matcher evaluation escaped owned hidden session");
            var expression = parameters["expression"]!.GetValue<string>();
            if (expression.StartsWith("globalThis.__crerPatterns=", StringComparison.Ordinal))
            {
                Compilations.Add(expression);
                if (ExpectedPatterns != null && !expression.Contains(ExpectedPatterns, StringComparison.Ordinal)) throw new Exception("pattern sources changed before browser compilation");
                return Task.FromResult(CompileError ? new JsonObject { ["exceptionDetails"] = new JsonObject { ["text"] = "invalid expression" } } : new JsonObject());
            }
            if (ExpectedUrl != null && !expression.Contains(JsonValue.Create(ExpectedUrl)!.ToJsonString(), StringComparison.Ordinal)) throw new Exception("request URL changed before browser evaluation");
            EvaluationToken = token;
            if (BlockEvaluation) return WaitForCancellation(token);
            if (EvaluationError) return Task.FromResult(new JsonObject { ["exceptionDetails"] = new JsonObject { ["text"] = "regex evaluation error" } });
            return Task.FromResult(new JsonObject { ["result"] = MissingDecision ? new JsonObject() : new JsonObject { ["value"] = Decisions.Count > 0 && Decisions.Dequeue() } });
        }
        return Task.FromResult(new JsonObject());
    }
    private async Task<JsonObject> WaitForCancellation(CancellationToken token)
    {
        using var callback = FaultCancellationCallback ? token.Register(() => throw new IOException("callback failure")) : default;
        try
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (DisconnectOnCancellation) { throw new IOException("CDP closed during cancellation"); }
            return new();
        }
        finally { EvaluationCompleted = true; }
    }
    public void Abort(Exception error) => Aborted = error;
    public void Emit(string method, JsonObject parameters) => Event?.Invoke(method, parameters, "page");
    public void Request(string url, string session = "page")
    {
        ExpectedUrl = url;
        Event?.Invoke("Fetch.requestPaused", new() { ["requestId"] = "request", ["request"] = new JsonObject { ["url"] = url } }, session);
    }
}
