using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Crer;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(10)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Same<T>(IEnumerable<T> actual, params T[] expected) => Check(actual.SequenceEqual(expected), "expected [" + string.Join(",", expected) + "], got [" + string.Join(",", actual) + "]");
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
JsonObject Result(int code = 0, params string[] failures) => new() { ["code"] = code, ["failures"] = new JsonArray(failures.Select(failure => (JsonNode?)JsonValue.Create(failure)).ToArray()) };

// scheduler_test.ts: concurrency, nested gates, queue cancellation, fail-fast, keyed
// locks, and exception draining. These exercise asynchronous lifecycle behavior.
await Test("limits concurrent work while preserving result order", async () =>
{
    var active = 0; var peak = 0; var state = new object();
    var result = await Scheduler.MapWithConcurrency(new[] { 1, 2, 3, 4 }, 2, async value =>
    {
        lock (state) peak = Math.Max(peak, ++active);
        await Task.Delay(value == 1 ? 20 : 5);
        lock (state) active--;
        return value * 10;
    });
    Check(peak == 2, "concurrency did not reach exactly two"); Same(result, 10, 20, 30, 40);
});
await Test("shares one concurrency limit across nested work", async () =>
{
    using var gate = new WorkGate(2); var active = 0; var peak = 0; var state = new object();
    Task<string?> Leaf() => gate.Run(async () =>
    {
        lock (state) peak = Math.Max(peak, ++active);
        await Task.Delay(5); lock (state) active--; return "done";
    });
    await Task.WhenAll(Task.WhenAll(Leaf(), Leaf()), Task.WhenAll(Leaf(), Leaf()));
    Check(peak == 2, "nested concurrency limit changed");
});
await Test("does not start queued work after cancellation", async () =>
{
    for (var attempt = 0; attempt < 32; attempt++)
    {
    using var gate = new WorkGate(1); using var cancel = new CancellationTokenSource(); var started = false;
    var release = Signal();
    var first = gate.Run(async () => { await release.Task; return "first"; });
    var second = gate.Run(() => { started = true; return Task.FromResult("second"); }, cancel.Token);
    cancel.Cancel(); release.SetResult(); await Task.WhenAll(first, second);
    Check(!started && await second == null, "cancelled queued action started");
    }
});
await Test("stops scheduling after a fail-fast result", async () =>
{
    var started = new List<int>();
    var result = await Scheduler.MapWithConcurrency(new[] { 1, 2, 3 }, 1, value => { started.Add(value); return Task.FromResult(value); }, value => value == 1);
    Same(started, 1); Same(result, 1);
});
await Test("fail-fast aborts running work and does not start queued work", async () =>
{
    var started = new ConcurrentQueue<int>(); var runningAborted = false;
    var result = await Scheduler.MapWithCancellation(new[] { 1, 2, 3 }, 2, async (value, token) =>
    {
        started.Enqueue(value);
        if (value == 1) return "failed";
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) { runningAborted = true; }
        return "cancelled";
    }, result => result == "failed");
    Same(started.OrderBy(value => value), 1, 2); Same(result, "failed", "cancelled"); Check(runningAborted, "running sibling was not aborted");
});
await Test("serializes identical persistent profile keys only", async () =>
{
    var locks = new KeyedWorkLock(); var active = 0; var peak = 0; var otherStarted = false;
    Task<string?> Run(string key) => locks.Run(key, async () =>
    {
        if (key.Equals("same", StringComparison.OrdinalIgnoreCase)) peak = Math.Max(peak, Interlocked.Increment(ref active));
        else otherStarted = true;
        await Task.Delay(5);
        if (key.Equals("same", StringComparison.OrdinalIgnoreCase)) Interlocked.Decrement(ref active);
        return key;
    });
    await Task.WhenAll(Run("same"), Run("SAME"), Run("other"));
    Check(peak == 1 && otherStarted && locks.Count == 0, "keyed lock isolation or release failed");
});
await Test("worker exceptions abort and drain siblings before session cleanup", async () =>
{
    var started = new ConcurrentQueue<int>(); var drained = false;
    try
    {
        await Scheduler.MapWithCancellation(new[] { 1, 2, 3 }, 2, async (value, token) =>
        {
            started.Enqueue(value);
            if (value == 1) throw new Exception("invalid scenario");
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            await Task.Delay(10); drained = true; return value;
        }, _ => false);
        throw new Exception("worker failure was swallowed");
    }
    catch (Exception error) when (error.Message == "invalid scenario") { }
    Same(started.OrderBy(value => value), 1, 2); Check(drained, "exception escaped before sibling cleanup completed");
});
await Test("cancelled keyed waiters release their dictionary ownership", async () =>
{
    var locks = new KeyedWorkLock(); var release = Signal(); using var cancel = new CancellationTokenSource();
    var first = locks.Run("key", async () => { await release.Task; return "first"; });
    var second = locks.Run<string>("KEY", () => throw new Exception("cancelled waiter started"), cancel.Token);
    cancel.Cancel(); release.SetResult(); await Task.WhenAll(first, second);
    Check(locks.Count == 0, "cancelled keyed entry leaked");
});

// plan_policy_test.ts, plus mixed-code priority and default-policy boundaries.
await Test("classifies and continues a plan timeout", () =>
{
    var results = new[] { Result(4, "plan:timeout:worker_ms") };
    Check(PlanPolicy.FailureKind(results) == "timeout", "timeout classification changed");
    Check(!PlanPolicy.ShouldAbort(results, new() { ["timeout"] = "continue" }), "timeout continue was ignored");
    Check(PlanPolicy.AggregateExitCode(results) == 4, "timeout exit code changed"); return Task.CompletedTask;
});
await Test("environment failures respect explicit and default policies while retaining exit code three", () =>
{
    var results = new[] { Result(3, "CDP disconnected") };
    Check(PlanPolicy.FailureKind(results) == "environment" && PlanPolicy.ShouldAbort(results), "environment default abort changed");
    Check(!PlanPolicy.ShouldAbort(results, new() { ["environment"] = "continue" }), "environment continue was ignored");
    Check(!PlanPolicy.ShouldAbort(results, new() { ["default"] = "continue" }), "environment default continue was ignored");
    Check(PlanPolicy.ShouldAbort(results, new() { ["default"] = "continue", ["environment"] = "abort" }), "environment explicit abort was ignored");
    Check(PlanPolicy.AggregateExitCode(results) == 3, "environment exit code changed"); return Task.CompletedTask;
});
await Test("a continued environment failure does not hide another failure policy", () =>
{
    var scenarioResults = new[] { Result(3, "CDP disconnected"), Result(4, "assertion failed") };
    var timeoutResults = new[] { Result(3, "CDP disconnected"), Result(4, "plan:timeout:worker_ms") };
    Check(PlanPolicy.ShouldAbort(scenarioResults, new() { ["environment"] = "continue" }), "continued environment failure hid scenario abort");
    Check(PlanPolicy.ShouldAbort(timeoutResults, new() { ["environment"] = "continue" }), "continued environment failure hid timeout abort");
    Check(!PlanPolicy.ShouldAbort(scenarioResults, new() { ["environment"] = "continue", ["scenario_failure"] = "continue" }), "all-continue mixed results aborted");
    Check(!PlanPolicy.ShouldAbort(timeoutResults, new() { ["environment"] = "continue", ["timeout"] = "continue" }), "all-continue mixed timeout results aborted");
    Check(PlanPolicy.AggregateExitCode(scenarioResults) == 3 && PlanPolicy.AggregateExitCode(timeoutResults) == 3, "mixed environment exit priority changed");
    return Task.CompletedTask;
});
await Test("prefers an interrupt exit code", () =>
{
    Check(PlanPolicy.AggregateExitCode(new[] { Result(4), Result(3), Result(5, "interrupted") }) == 5, "interrupt priority changed");
    Check(PlanPolicy.AggregateExitCode([]) == 0 && !PlanPolicy.ShouldAbort([]), "empty plan did not succeed"); return Task.CompletedTask;
});
await Test("scenario failure respects explicit and default policies", () =>
{
    var results = new[] { Result(4) };
    Check(PlanPolicy.FailureKind(results) == "scenario_failure" && PlanPolicy.ShouldAbort(results), "default policy changed");
    Check(!PlanPolicy.ShouldAbort(results, new() { ["default"] = "continue" }), "default continue ignored");
    Check(PlanPolicy.ShouldAbort(results, new() { ["default"] = "continue", ["scenario_failure"] = "abort" }), "specific abort ignored"); return Task.CompletedTask;
});

var testId = Guid.NewGuid().ToString("N");
var directory = Path.GetFullPath(Path.Combine(".crer", "plan-unit", testId));
var profile = Path.GetFullPath(Path.Combine(".crer", "profiles", "plan-unit", testId));
Directory.CreateDirectory(directory);
string ScenarioFile(string name, bool persistent = false)
{
    var path = Path.Combine(directory, name + ".crer.yaml");
    File.WriteAllText(path, $"version: 1\nname: {name}\nbrowser:\n  initial_url: https://example.test/{name}\n" + (persistent ? $"  profile: 'persistent:{profile}'\n" : "") + "steps: []\n");
    return Path.GetFileName(path);
}
var successFile = ScenarioFile("success"); var failureFile = ScenarioFile("failure"); var otherFile = ScenarioFile("other");
var persistentFile = ScenarioFile("persistent", true); var persistentNextFile = ScenarioFile("persistentNext", true);
JsonObject Plan(JsonObject node, JsonObject? policy = null, bool reuse = false, int maximum = 1, double? timeout = null) => new()
{
    ["version"] = 1, ["name"] = "test", ["max_parallel"] = maximum, ["run"] = node,
    ["on_failure"] = policy ?? new JsonObject(), ["timeouts"] = new JsonObject { ["worker_ms"] = timeout ?? 0 },
    ["browser_session"] = reuse ? new JsonObject { ["reuse"] = "same-profile", ["focus"] = "before-step" } : null,
};
JsonObject ValidPlan(JsonObject node, JsonObject? policy = null, bool reuse = false, int maximum = 1, double? timeout = null)
{
    var plan = Plan(node, policy, reuse, maximum, timeout); if (!reuse) plan.Remove("browser_session"); return plan;
}
JsonObject Leaf(string file) => new() { ["scenario"] = file };
JsonObject Serial(params JsonObject[] children) => new() { ["serial"] = new JsonArray(children.Select(child => (JsonNode)child).ToArray()) };
JsonObject Parallel(bool failFast, params JsonObject[] children) => new() { ["parallel"] = new JsonObject { ["fail_fast"] = failFast, ["jobs"] = new JsonArray(children.Select(child => (JsonNode)child).ToArray()) } };
CliOptions Options() => new("run", Path.Combine(directory, "plan.crer.yaml"), "fake-chrome", false, null, null, null, false);
try
{
    await Test("serial stops by default and continues only by configured policy", async () =>
    {
        var started = new List<string>();
        Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain) { started.Add(scenario.Name); return Task.FromResult(Result(scenario.Name == "failure" ? 4 : 0)); }
        var result = await PlanRunner.Run(ValidPlan(Serial(Leaf(failureFile), Leaf(successFile))), Options(), "fake", default, () => new FakePlayback(Play));
        Same(started, "failure"); Check(result.Code == 4 && result.Results.Count == 1, "serial abort aggregation failed"); started.Clear();
        result = await PlanRunner.Run(ValidPlan(Serial(Leaf(failureFile), Leaf(successFile)), new() { ["scenario_failure"] = "continue" }), Options(), "fake", default, () => new FakePlayback(Play));
        Same(started, "failure", "success"); Check(result.Code == 4 && result.Results.Count == 2, "serial continue aggregation failed");
    });
    await Test("serial environment failures continue only when their resolved policy allows it", async () =>
    {
        foreach (var (policy, continues) in new[]
        {
            (new JsonObject(), false),
            (new JsonObject { ["environment"] = "continue" }, true),
            (new JsonObject { ["default"] = "continue" }, true),
            (new JsonObject { ["default"] = "continue", ["environment"] = "abort" }, false),
        })
        {
            var started = new List<string>();
            Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
            {
                started.Add(scenario.Name);
                return Task.FromResult(scenario.Name == "failure" ? Result(3, "Initial navigation failed") : Result());
            }
            var result = await PlanRunner.Run(ValidPlan(Serial(Leaf(failureFile), Leaf(successFile)), policy), Options(), "fake", default, () => new FakePlayback(Play));
            Same(started, continues ? ["failure", "success"] : ["failure"]);
            Check(result.Code == 3 && result.Results.Count == (continues ? 2 : 1), "environment continuation or aggregate exit code changed");
        }
    });
    await Test("parallel environment continue preserves running siblings and subsequent leaves", async () =>
    {
        var siblingStarted = Signal(); var continuationStarted = Signal(); var siblingCancelled = false;
        var started = new ConcurrentQueue<string>();
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            started.Enqueue(scenario.Name);
            if (scenario.Name == "failure") { await siblingStarted.Task; return Result(3, "Initial navigation failed"); }
            if (scenario.Name == "success") { continuationStarted.TrySetResult(); return Result(); }
            siblingStarted.TrySetResult();
            try { await continuationStarted.Task.WaitAsync(token); }
            catch (OperationCanceledException) { siblingCancelled = true; return Result(5, "cancelled"); }
            return Result();
        }
        var result = await PlanRunner.Run(ValidPlan(Parallel(false, Serial(Leaf(failureFile), Leaf(successFile)), Leaf(otherFile)),
            new() { ["environment"] = "continue" }, maximum: 2), Options(), "fake", default, () => new FakePlayback(Play));
        Same(result.Results.OfType<JsonObject>().Select(item => Json.Number(item["code"])), 3d, 0d, 0d);
        Check(started.Count == 3 && !siblingCancelled && result.Code == 3, "environment continue cancelled a running sibling or lost the continuation");
    });
    await Test("parallel environment abort cancels running siblings and leaves queued work unstarted", async () =>
    {
        var siblingStarted = Signal(); var drained = false; var started = new ConcurrentQueue<string>();
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            started.Enqueue(scenario.Name);
            if (scenario.Name == "failure") { await siblingStarted.Task; return Result(3, "Initial navigation failed"); }
            siblingStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            drained = true; return Result(5, "cancelled");
        }
        var result = await PlanRunner.Run(ValidPlan(Parallel(false, Leaf(failureFile), Leaf(otherFile), Leaf(successFile)),
            new() { ["default"] = "continue", ["environment"] = "abort" }, maximum: 2), Options(), "fake", default, () => new FakePlayback(Play));
        Same(started.OrderBy(name => name), "failure", "other");
        Same(result.Results.OfType<JsonObject>().Select(item => Json.Number(item["code"])), 3d, 4d);
        Check(drained && result.Code == 3, "environment abort did not cancel/drain running work or lost exit priority");
    });
    await Test("worker timeout cancels and classifies the completed worker result", async () =>
    {
        var started = new List<string>(); var drained = false;
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            started.Add(scenario.Name);
            if (scenario.Name != "failure") return Result();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            drained = true; return Result(5, "cancelled");
        }
        var result = await PlanRunner.Run(ValidPlan(Serial(Leaf(failureFile), Leaf(successFile)), new() { ["timeout"] = "continue" }, timeout: 10), Options(), "fake", default, () => new FakePlayback(Play));
        Same(started, "failure", "success"); Check(drained && result.Code == 4 && Json.Text(result.Results[0]!["failures"]![1]) == "plan:timeout:worker_ms", "worker timeout result or draining changed");
    });
    await Test("plan interrupt prevents queued leaves and takes exit priority", async () =>
    {
        using var cancel = new CancellationTokenSource(); var started = new List<string>();
        Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain) { started.Add(scenario.Name); cancel.Cancel(); return Task.FromResult(Result(5, "interrupted")); }
        var result = await PlanRunner.Run(ValidPlan(Serial(Leaf(successFile), Leaf(otherFile))), Options(), "fake", cancel.Token, () => new FakePlayback(Play));
        Same(started, "success"); Check(result.Code == 5, "interrupt exit code changed");
    });
    await Test("same-profile sessions reuse the instance and close once after both leaves", async () =>
    {
        var instances = new List<FakePlayback>(); var calls = new List<string>();
        Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain) { Check(retain, "persistent session was not retained"); calls.Add(scenario.Name); return Task.FromResult(Result()); }
        FakePlayback Factory() { var item = new FakePlayback(Play); instances.Add(item); return item; }
        var result = await PlanRunner.Run(ValidPlan(Serial(Leaf(persistentFile), Leaf(persistentNextFile)), reuse: true), Options(), "fake", default, Factory);
        Same(calls, "persistent", "persistentNext"); Check(result.Code == 0 && instances.Count == 1 && instances[0].Closes == 1 && instances[0].FocusPolicy == "before-step", "session reuse/lifetime/focus failed");
    });
    await Test("same-profile playback owner executes the next leaf after a continued environment failure", async () =>
    {
        var instances = new List<FakePlayback>(); var calls = new List<string>();
        Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            Check(retain && !token.IsCancellationRequested, "continued persistent leaf lost retention or received cancellation");
            calls.Add(scenario.Name);
            return Task.FromResult(scenario.Name == "persistent" ? Result(3, "Initial navigation failed") : Result());
        }
        FakePlayback Factory() { var item = new FakePlayback(Play); instances.Add(item); return item; }
        var result = await PlanRunner.Run(ValidPlan(Serial(Leaf(persistentFile), Leaf(persistentNextFile)),
            new() { ["environment"] = "continue" }, reuse: true), Options(), "fake", default, Factory);
        Same(calls, "persistent", "persistentNext");
        Check(result.Code == 3 && instances.Count == 1 && instances[0].Closes == 1, "continued environment failure changed profile ownership or final cleanup");
    });
    await Test("same-profile work serializes before entering the global gate", async () =>
    {
        var active = 0; var peak = 0; var otherRanConcurrently = false;
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            if (scenario.Name.StartsWith("persistent")) peak = Math.Max(peak, Interlocked.Increment(ref active));
            else otherRanConcurrently = Volatile.Read(ref active) == 1;
            await Task.Delay(20);
            if (scenario.Name.StartsWith("persistent")) Interlocked.Decrement(ref active);
            return Result();
        }
        var result = await PlanRunner.Run(ValidPlan(Parallel(false, Leaf(persistentFile), Leaf(persistentNextFile), Leaf(otherFile)), reuse: true, maximum: 3), Options(), "fake", default, () => new FakePlayback(Play));
        Check(peak == 1 && otherRanConcurrently && result.Results.Count == 3, "profile queue blocked unrelated work or allowed overlap");
    });
    await Test("parallel fail-fast drains running workers before closing shared sessions", async () =>
    {
        var otherStarted = Signal(); var drained = false; var instances = new List<FakePlayback>(); var started = new ConcurrentQueue<string>();
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            started.Enqueue(scenario.Name);
            if (scenario.Name == "persistent") { await otherStarted.Task; return Result(4, "failed"); }
            otherStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            await Task.Delay(5); drained = true; return Result(5, "cancelled");
        }
        FakePlayback Factory() { var playback = new FakePlayback(Play, () => Check(drained, "session closed while sibling was running")); instances.Add(playback); return playback; }
        var result = await PlanRunner.Run(ValidPlan(Parallel(true, Leaf(persistentFile), Leaf(otherFile), Leaf(successFile)), new() { ["default"] = "continue" }, reuse: true, maximum: 2), Options(), "fake", default, Factory);
        Check(started.Count == 2 && drained && instances[0].Closes == 1 && result.Code == 4, "fail-fast failed to cancel/drain or queued leaf started");
    });
    await Test("parallel fail-fast overrides environment continue and keeps exit code three", async () =>
    {
        var siblingStarted = Signal(); var drained = false; var started = new ConcurrentQueue<string>();
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            started.Enqueue(scenario.Name);
            if (scenario.Name == "failure") { await siblingStarted.Task; return Result(3, "Initial navigation failed"); }
            siblingStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            drained = true; return Result(5, "cancelled");
        }
        var result = await PlanRunner.Run(ValidPlan(Parallel(true, Leaf(failureFile), Leaf(otherFile), Leaf(successFile)),
            new() { ["environment"] = "continue" }, maximum: 2), Options(), "fake", default, () => new FakePlayback(Play));
        Same(started.OrderBy(name => name), "failure", "other");
        Same(result.Results.OfType<JsonObject>().Select(item => Json.Number(item["code"])), 3d, 4d);
        Check(drained && result.Code == 3, "fail-fast did not override environment continue or failed to drain running work");
    });
    await Test("invalid sibling scenario aborts and drains active workers", async () =>
    {
        var drained = false;
        async Task<JsonObject> Play(Scenario scenario, CliOptions _, CancellationToken token, bool retain)
        {
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            drained = true; return Result(5);
        }
        try
        {
            await PlanRunner.Run(ValidPlan(Parallel(false, Leaf(persistentFile), Leaf("missing.crer.yaml")), reuse: true, maximum: 2), Options(), "fake", default,
                () => new FakePlayback(Play, () => Check(drained, "session closed before active worker drained")));
            throw new Exception("invalid scenario was ignored");
        }
        catch (CrerException error) when (error.Code == 2 && error.Message.Contains("invalid scenario")) { }
        Check(drained, "invalid sibling did not drain running work");
    });
    await Test("retained session cleanup failure is reported as an environment error", async () =>
    {
        var closed = 0;
        try
        {
            await PlanRunner.Run(ValidPlan(Serial(Leaf(persistentFile)), reuse: true), Options(), "fake", default,
                () => new FakePlayback((_, _, _, _) => Task.FromResult(Result()), () => { closed++; throw new IOException("owned cleanup failure"); }));
            throw new Exception("session cleanup failure was ignored");
        }
        catch (CrerException error) when (error.Code == 3 && error.Message.Contains("owned cleanup failure")) { }
        Check(closed == 1, "retained session cleanup was not completed exactly once");
    });
    await Test("run overrides pass through and play position does not leak", async () =>
    {
        var requested = Options() with { BoundsOverride = new() { ["left"] = 10, ["top"] = 20 }, OpacityOverride = 0.4, Position = new() { ["left"] = 99, ["top"] = 99 } };
        Task<JsonObject> Play(Scenario scenario, CliOptions actual, CancellationToken token, bool retain)
        {
            Check(actual.Position == null && actual.BoundsOverride!["left"]!.GetValue<int>() == 10 && actual.OpacityOverride == 0.4 && actual.File == Path.Combine(directory, successFile), "run/play override isolation failed");
            return Task.FromResult(Result());
        }
        await PlanRunner.Run(ValidPlan(Leaf(successFile)), requested, "fake", default, () => new FakePlayback(Play));
    });
}
finally
{
    foreach (var owned in new[] { directory, profile })
    {
        var resolved = Path.GetFullPath(owned);
        var parent = Path.GetFullPath(Path.Combine(".crer", owned == directory ? "plan-unit" : "profiles/plan-unit"));
        if (resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved) == testId && Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }
}
Console.WriteLine($"plan: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

internal sealed class FakePlayback(Func<Scenario, CliOptions, CancellationToken, bool, Task<JsonObject>> run, Action? close = null) : IPlanPlayback
{
    public string FocusPolicy { get; set; } = "once";
    public int Closes { get; private set; }
    public Task<JsonObject> RunScenario(Scenario scenario, CliOptions options, string chrome, CancellationToken token, bool retainSession) => run(scenario, options, token, retainSession);
    public Task CloseSession() { Closes++; close?.Invoke(); return Task.CompletedTask; }
}
