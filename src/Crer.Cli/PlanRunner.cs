using System.Text.Json.Nodes;

namespace Crer;

public sealed record PlanOutcome(JsonArray Results, int Code);

public interface IPlanPlayback
{
    string FocusPolicy { get; set; }
    Task<JsonObject> RunScenario(Scenario scenario, CliOptions options, string chrome, CancellationToken token, bool retainSession);
    Task CloseSession();
}

public static class PlanPolicy
{
    public static string? FailureKind(IEnumerable<JsonObject> results)
    {
        var list = results.ToArray();
        if (!list.Any(result => Json.Number(result["code"]) != 0)) return null;
        if (list.Any(result => Json.Number(result["code"]) == 3)) return "environment";
        return list.Any(result => result["failures"] is JsonArray failures && failures.Any(failure => Json.Text(failure) == "plan:timeout:worker_ms"))
            ? "timeout" : "scenario_failure";
    }
    public static bool ShouldAbort(IEnumerable<JsonObject> results, JsonObject? policy = null)
    {
        // ネストした job に複数種の失敗があっても、continue の結果で別の abort を隠さない。
        return results.Any(result =>
        {
            var kind = FailureKind([result]);
            return kind != null && Json.Text(policy?[kind] ?? policy?["default"], "abort") == "abort";
        });
    }
    public static int AggregateExitCode(IEnumerable<JsonObject> results)
    {
        var codes = results.Select(result => Json.Number(result["code"])).ToArray();
        if (codes.Contains(5)) return 5;
        if (codes.Contains(3)) return 3;
        return codes.Any(code => code != 0) ? 4 : 0;
    }
}

/// <summary>Owns the complete plan lifetime, including profile serialization and shared browser teardown.</summary>
public sealed class PlanRunner
{
    private sealed class BrowserPlayback(CancellationToken userCancellation) : IPlanPlayback
    {
        private readonly Playback playback = new() { UserCancellation = userCancellation };
        public string FocusPolicy { get => playback.FocusPolicy; set => playback.FocusPolicy = value; }
        public Task<JsonObject> RunScenario(Scenario scenario, CliOptions options, string chrome, CancellationToken token, bool retainSession)
            => playback.RunScenario(scenario, options, chrome, token, retainSession);
        public Task CloseSession() => playback.CloseSession();
    }

    private readonly JsonObject plan;
    private readonly CliOptions options;
    private readonly string chrome;
    private readonly Func<IPlanPlayback> createPlayback;
    private readonly Dictionary<string, IPlanPlayback> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPlanPlayback> acquiredSessions = [];
    private readonly object sessionState = new();
    private readonly KeyedWorkLock profileLocks = new();
    private readonly string directory;
    private readonly int maximum;
    private readonly JsonObject? onFailure;
    private readonly double workerMs;
    private readonly bool reuse;
    private readonly string focus;
    private CancellationToken userCancellation;

    private PlanRunner(JsonObject plan, CliOptions options, string chrome, Func<IPlanPlayback>? factory)
    {
        this.plan = Scenario.ValidatePlan(plan);
        this.options = options;
        this.chrome = chrome;
        createPlayback = factory ?? (() => new BrowserPlayback(userCancellation));
        directory = Path.GetDirectoryName(Path.GetFullPath(options.File!))!;
        maximum = (int)Math.Min(int.MaxValue, Json.Number(plan["max_parallel"], 1));
        onFailure = plan["on_failure"] as JsonObject;
        workerMs = Json.Number((plan["timeouts"] as JsonObject)?["worker_ms"]);
        reuse = plan["browser_session"] is JsonObject;
        focus = Json.Text(plan["browser_session"]?["focus"], "once");
    }

    public static Task<PlanOutcome> Run(JsonObject plan, CliOptions options, string chrome, CancellationToken token,
        Func<IPlanPlayback>? playbackFactory = null) => new PlanRunner(plan, options, chrome, playbackFactory).Execute(token);

    private async Task<PlanOutcome> Execute(CancellationToken token)
    {
        userCancellation = token;
        using var gate = new WorkGate(maximum);
        List<JsonObject> results;
        try { results = await RunNode(plan["run"]!.AsObject(), gate, token); }
        finally
        {
            List<Exception> errors = [];
            foreach (var playback in acquiredSessions.AsEnumerable().Reverse())
                try { await playback.CloseSession(); }
                catch (Exception error) { errors.Add(error); }
            sessions.Clear();
            acquiredSessions.Clear();
            if (errors.Count > 0) throw new CrerException(3, "Could not close plan browser sessions: " + string.Join("; ", errors.Select(error => error.Message)));
        }
        var array = new JsonArray(results.Select(result => (JsonNode)result).ToArray());
        return new(array, token.IsCancellationRequested ? 5 : PlanPolicy.AggregateExitCode(results));
    }

    private IPlanPlayback ForProfile(string profile)
    {
        // The caller already owns this profile's execution lock. The short dictionary
        // lock also protects simultaneous acquisition of distinct profiles.
        lock (sessionState)
        {
            if (sessions.TryGetValue(profile, out var session)) return session;
            session = createPlayback();
            session.FocusPolicy = focus;
            sessions.Add(profile, session);
            acquiredSessions.Add(session);
            return session;
        }
    }

    private async Task<List<JsonObject>> RunNode(JsonObject node, WorkGate gate, CancellationToken signal,
        Func<JsonObject, Task>? leafCompleted = null)
    {
        if (node.ContainsKey("scenario"))
        {
            var path = "";
            Scenario? scenario = null;
            string? persistentProfile = null;
            Exception? setupError = null;
            try
            {
                path = Path.GetFullPath(Path.Combine(directory, Json.Text(node["scenario"])));
                try { scenario = Scenario.Load(path); }
                catch (CrerException error) { throw new CrerException(2, $"invalid scenario {path}: {error.Message}"); }
                var configuredProfile = Profiles.Configured(scenario, options);
                persistentProfile = configuredProfile == null ? null : Profiles.PersistentDirectory(configuredProfile);
            }
            catch (Exception error) { setupError = error; }
            async Task<List<JsonObject>> RunLeaf()
            {
                return await gate.Run(async () =>
                {
                    // 取消で開始されない葉の設定エラーは、先行する失敗より優先させない。
                    if (setupError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(setupError).Throw();
                    var retain = reuse && persistentProfile != null;
                    var playback = retain ? ForProfile(persistentProfile!) : createPlayback();
                    using var worker = CancellationTokenSource.CreateLinkedTokenSource(signal);
                    var timedOut = 0;
                    using var timer = workerMs > 0 ? new Timer(_ =>
                    {
                        Interlocked.Exchange(ref timedOut, 1);
                        try { worker.Cancel(); } catch (ObjectDisposedException) { }
                    }, null, TimeSpan.FromMilliseconds(workerMs > int.MaxValue ? 1 : Math.Truncate(workerMs)), Timeout.InfiniteTimeSpan) : null;
                    var leafOptions = options with { Command = "run", File = path, Position = null, KeepArtifacts = false, Seed = null, StepDelay = null };
                    var result = await playback.RunScenario(scenario!, leafOptions, chrome, worker.Token, retain);
                    if (timer != null) await timer.DisposeAsync();
                    if (!userCancellation.IsCancellationRequested && worker.IsCancellationRequested && Json.Number(result["code"]) == 5)
                        result["code"] = 4;
                    if (Volatile.Read(ref timedOut) != 0)
                    {
                        if (retain) await playback.CloseSession();
                        result["code"] = 4;
                        var failures = result["failures"] as JsonArray;
                        if (failures == null) { failures = new(); result["failures"] = failures; }
                        failures.Add((JsonNode?)JsonValue.Create("plan:timeout:worker_ms"));
                    }
                    // ゲートを解放する前に祖先へ通知し、失敗後の待機中シナリオを開始させない。
                    if (leafCompleted != null) await leafCompleted(result);
                    return new List<JsonObject> { result };
                }, signal) ?? [];
            }
            return persistentProfile == null ? await RunLeaf() : await profileLocks.Run(persistentProfile, RunLeaf, signal) ?? [];
        }
        if (node["serial"] is JsonArray serial)
        {
            List<JsonObject> output = [];
            foreach (var child in serial)
            {
                var results = await RunNode(child!.AsObject(), gate, signal, leafCompleted);
                output.AddRange(results);
                if (signal.IsCancellationRequested || PlanPolicy.ShouldAbort(results, onFailure)) break;
            }
            return output;
        }
        var parallel = node["parallel"]!.AsObject();
        var children = parallel["jobs"]!.AsArray().Select(child => child!.AsObject()).ToArray();
        var failFast = parallel["fail_fast"]?.GetValue<bool>() == true;
        using var group = CancellationTokenSource.CreateLinkedTokenSource(signal);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Completed(JsonObject result)
        {
            // 同期完了した最初の葉も、兄弟の初期投入が終わるまでは取消を発火させない。
            await initialized.Task;
            if (failFast ? Json.Number(result["code"]) != 0 : PlanPolicy.ShouldAbort([result], onFailure)) group.Cancel();
            if (leafCompleted != null) await leafCompleted(result);
        }
        // 同時実行数は共有する葉ゲートだけで制限する。profile 待ちの job で別 profile を塞がない。
        var pending = Scheduler.MapWithConcurrency(children, children.Length,
            child => RunNode(child, gate, group.Token, Completed), _ => group.IsCancellationRequested, () => group.Cancel());
        initialized.SetResult();
        var groups = await pending;
        return groups.SelectMany(group => group).ToList();
    }
}
