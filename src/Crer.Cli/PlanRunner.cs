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

/// <summary>profile の直列化と最終利用後の共有ブラウザーの終了を管理する。</summary>
public sealed class PlanRunner
{
    private sealed class PlanNode(JsonObject definition)
    {
        public JsonObject Definition { get; } = definition;
        public PlanNode[] Children { get; set; } = [];
        public string Path { get; set; } = "";
        public Scenario? Scenario { get; set; }
        public string? PersistentProfile { get; set; }
        public Exception? SetupError { get; set; }
        public bool Released { get; set; }
    }

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
    private readonly Dictionary<string, int> remainingProfiles = new(StringComparer.OrdinalIgnoreCase);
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
        var root = Prepare(plan["run"]!.AsObject());
        List<JsonObject> results;
        try { results = await RunNode(root, gate, token); }
        finally
        {
            try { await CloseSessions(acquiredSessions.AsEnumerable().Reverse().ToArray()); }
            finally { sessions.Clear(); acquiredSessions.Clear(); }
        }
        var array = new JsonArray(results.Select(result => (JsonNode)result).ToArray());
        return new(array, token.IsCancellationRequested ? 5 : PlanPolicy.AggregateExitCode(results));
    }

    private PlanNode Prepare(JsonObject definition)
    {
        var node = new PlanNode(definition);
        if (definition.ContainsKey("scenario"))
        {
            if (reuse)
            {
                // 別列や外側の serial に残る利用も含め、開始前に profile の利用予定を確定する。
                LoadScenario(node);
                if (node.PersistentProfile is string profile)
                    remainingProfiles[profile] = remainingProfiles.GetValueOrDefault(profile) + 1;
            }
        }
        else
        {
            var children = definition["serial"] as JsonArray ?? definition["parallel"]!["jobs"]!.AsArray();
            node.Children = children.Select(child => Prepare(child!.AsObject())).ToArray();
        }
        return node;
    }

    private void LoadScenario(PlanNode node)
    {
        try
        {
            node.Path = Path.GetFullPath(Path.Combine(directory, Json.Text(node.Definition["scenario"])));
            try { node.Scenario = Scenario.Load(node.Path); }
            catch (CrerException error) { throw new CrerException(2, $"invalid scenario {node.Path}: {error.Message}"); }
            var configuredProfile = Profiles.Configured(node.Scenario, options);
            node.PersistentProfile = configuredProfile == null ? null : Profiles.PersistentDirectory(configuredProfile);
        }
        // 未開始の葉の設定エラーは、取消しの原因になった先行の失敗を上書きしない。
        catch (Exception error) { node.SetupError = error; }
    }

    private void Release(PlanNode node)
    {
        if (node.Released) return;
        node.Released = true;
        if (reuse && node.PersistentProfile is string profile) remainingProfiles[profile]--;
        // serial の中断で開始されなかった葉も、今後の利用予定から除く。
        foreach (var child in node.Children) Release(child);
    }

    private async Task CloseUnusedSessions()
    {
        List<IPlanPlayback> unused = [];
        lock (sessionState)
        {
            foreach (var (profile, playback) in sessions.ToArray())
            {
                if (remainingProfiles[profile] != 0) continue;
                sessions.Remove(profile);
                acquiredSessions.Remove(playback);
                unused.Add(playback);
            }
        }
        await CloseSessions(unused);
    }

    private static async Task CloseSessions(IEnumerable<IPlanPlayback> playbacks)
    {
        List<Exception> errors = [];
        foreach (var playback in playbacks)
            try { await playback.CloseSession(); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new CrerException(3, "Could not close plan browser sessions: " + string.Join("; ", errors.Select(error => error.Message)));
    }

    private IPlanPlayback ForProfile(string profile)
    {
        // 呼び出し元は profile の実行ロックを取得済み。別 profile の同時取得も辞書のロックで保護する。
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

    private async Task<List<JsonObject>> RunNode(PlanNode node, WorkGate gate, CancellationToken signal,
        Func<JsonObject, Task>? leafCompleted = null)
    {
        try { return await ExecuteNode(node, gate, signal, leafCompleted); }
        finally
        {
            lock (sessionState) Release(node);
            // 取消し中の葉からは早期終了を要求せず、親スコープの後始末へ委ねる。
            if (!signal.IsCancellationRequested) await CloseUnusedSessions();
        }
    }

    private async Task<List<JsonObject>> ExecuteNode(PlanNode prepared, WorkGate gate, CancellationToken signal,
        Func<JsonObject, Task>? leafCompleted = null)
    {
        var node = prepared.Definition;
        if (node.ContainsKey("scenario"))
        {
            if (!reuse) LoadScenario(prepared);
            var persistentProfile = prepared.PersistentProfile;
            async Task<List<JsonObject>> RunLeaf()
            {
                return await gate.Run(async () =>
                {
                    // 取消で開始されない葉の設定エラーは、先行する失敗より優先させない。
                    if (prepared.SetupError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(prepared.SetupError).Throw();
                    var retain = reuse && persistentProfile != null;
                    var playback = retain ? ForProfile(persistentProfile!) : createPlayback();
                    using var worker = CancellationTokenSource.CreateLinkedTokenSource(signal);
                    var timedOut = 0;
                    using var timer = workerMs > 0 ? new Timer(_ =>
                    {
                        Interlocked.Exchange(ref timedOut, 1);
                        try { worker.Cancel(); } catch (ObjectDisposedException) { }
                    }, null, TimeSpan.FromMilliseconds(workerMs > int.MaxValue ? 1 : Math.Truncate(workerMs)), Timeout.InfiniteTimeSpan) : null;
                    var leafOptions = options with { Command = "run", File = prepared.Path, Position = null, KeepArtifacts = false, Seed = null, StepDelay = null };
                    var result = await playback.RunScenario(prepared.Scenario!, leafOptions, chrome, worker.Token, retain);
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
        if (node.ContainsKey("serial"))
        {
            List<JsonObject> output = [];
            foreach (var child in prepared.Children)
            {
                var results = await RunNode(child, gate, signal, leafCompleted);
                output.AddRange(results);
                if (signal.IsCancellationRequested || PlanPolicy.ShouldAbort(results, onFailure)) break;
            }
            return output;
        }
        var parallel = node["parallel"]!.AsObject();
        var children = prepared.Children;
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
