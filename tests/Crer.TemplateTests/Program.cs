using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crer;

var passed = 0; var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); failed++; }
}
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-template-vectors.json")))!;
var temporary = Path.Combine(Path.GetTempPath(), "crer-template-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var file = Path.Combine(temporary, "pixel.png"); await File.WriteAllBytesAsync(file, [0]);
try
{
    await Test("template point selection matches legacy sizes and inset boundaries", () =>
    {
        foreach (var vector in vectors["points"]!.AsArray())
        {
            var randomIndex = 0;
            var actual = Template.RandomPointInMatch(TemplateMatch.From(vector!["match"])!, vector["inset"]!.GetValue<double>(), () => new[] { .25, .75 }[randomIndex++]);
            Check(actual == ActionPoint.From(vector["point"]!), "random inset coordinate differs");
            Check(randomIndex == 2, "template point consumed different random count");
        }
        return Task.CompletedTask;
    });
    await Test("sampled RGB browser expression matches fixed baseline single and multiple search", () =>
    {
        for (var index = 0; index < 2; index++)
        {
            var actual = Template.Expression("\"data:image/png;base64,c2NyZWVu\"", "\"data:image/png;base64,AA==\"", index == 1, .9, 5);
            var expected = vectors["expressions"]![index]!.GetValue<string>();
            Check(Regex.Replace(actual, @"\s+", "") == Regex.Replace(expected, @"\s+", ""), $"browser expression differs for search mode {index}");
        }
        return Task.CompletedTask;
    });
    await Test("relative template paths resolve against scenario directory", () =>
    {
        Check(Template.PathFor("fixtures/playback", "templates/button.png") == Path.GetFullPath("fixtures/playback/templates/button.png"), "relative template path");
        Check(Template.PathFor(temporary, file) == file, "absolute template path"); return Task.CompletedTask;
    });
    await Test("single matching preserves browser match and in-memory screenshot", async () =>
    {
        var calls = new List<string>();
        var result = await Template.MatchAsync((method, _) =>
        {
            calls.Add(method);
            return Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "c2NyZWVu" }
                : new JsonObject { ["result"] = new JsonObject { ["value"] = new TemplateMatch(10, 20, 30, 40, .99).ToJson() } });
        }, new() { ["path"] = file }, null);
        Check(result.Match == new TemplateMatch(10, 20, 30, 40, .99) && result.Screenshot == "c2NyZWVu", "single template result");
        Check(calls.SequenceEqual(new[] { "Page.captureScreenshot", "Runtime.evaluate" }), "template CDP sequence");
    });
    await Test("multiple matching returns all browser rectangles in order", async () =>
    {
        var matches = new[] { new TemplateMatch(10, 20, 30, 40, .99), new TemplateMatch(50, 60, 30, 40, .98) };
        var result = await Template.MatchManyAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "c2NyZWVu" }
            : new JsonObject { ["result"] = new JsonObject { ["value"] = new JsonArray(matches.Select(value => (JsonNode)value.ToJson()).ToArray()) } }), new() { ["path"] = file }, null, .9, 5);
        Check(result.Matches.SequenceEqual(matches) && result.Screenshot == "c2NyZWVu", "multiple template result");
    });
    await Test("evaluation exception retains screenshot for failure-only policy", async () =>
    {
        try
        {
            await Template.MatchAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "ZmFpbHVyZQ==" }
                : new JsonObject { ["exceptionDetails"] = new JsonObject { ["exception"] = new JsonObject { ["description"] = "template is too large" } } }), new() { ["path"] = file }, null);
            throw new Exception("invalid template succeeded");
        }
        catch (TemplateMatchException error) { Check(error.Message.Contains("template match evaluation failed", StringComparison.Ordinal) && error.Screenshot == "ZmFpbHVyZQ==", "failure screenshot or diagnostic lost"); }
    });
    await Test("invalid matches reject dimensions missing values and nonnumeric fields", async () =>
    {
        foreach (var invalid in new JsonNode?[] { null, new JsonObject(), new TemplateMatch(1, 2, 0, 4, 1).ToJson(), new JsonObject { ["x"] = "1", ["y"] = 2, ["width"] = 3, ["height"] = 4, ["similarity"] = 1 } })
        {
            try
            {
                await Template.MatchAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "screen" }
                    : new JsonObject { ["result"] = new JsonObject { ["value"] = invalid?.DeepClone() } }), new() { ["path"] = file }, null);
                throw new Exception("invalid match succeeded");
            }
            catch (TemplateMatchException error) { Check(error.Message.Contains("invalid result", StringComparison.Ordinal) && error.Screenshot == "screen", "invalid result lost screenshot"); }
        }
    });
    await Test("multiple search rejects a nonarray and any invalid rectangle", async () =>
    {
        foreach (var invalid in new JsonNode[] { new JsonObject(), new JsonArray(new TemplateMatch(1, 2, 3, 4, 1).ToJson(), new JsonObject()) })
        {
            try
            {
                await Template.MatchManyAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "screen" }
                    : new JsonObject { ["result"] = new JsonObject { ["value"] = invalid.DeepClone() } }), new() { ["path"] = file }, null, .9, 5);
                throw new Exception("invalid matches succeeded");
            }
            catch (TemplateMatchException error) { Check(error.Message.Contains("invalid result", StringComparison.Ordinal), "invalid matches diagnostic"); }
        }
    });
    await Test("multiple search accepts zero matches", async () =>
    {
        var result = await Template.MatchManyAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "screen" }
            : new JsonObject { ["result"] = new JsonObject { ["value"] = new JsonArray() } }), new() { ["path"] = file }, null, .9, 5);
        Check(result.Matches.Count == 0, "zero matches rejected");
    });
    await Test("照合は専用の非表示環境を再利用し、撮影と対象ページのCSPを分離する", async () =>
    {
        var cdp = new MatcherTransport();
        var matcher = new TemplateMatcher(cdp, "page");
        await matcher.MatchAsync(new() { ["path"] = file }, null);
        await matcher.MatchManyAsync(new() { ["path"] = file }, null, .9, 5);
        await matcher.DisposeAsync(); await matcher.DisposeAsync();
        var calls = cdp.Calls.ToArray();
        Check(calls.Count(call => call.Method == "Target.createTarget") == 1, "照合ごとに環境を作り直しました");
        Check(calls.Single(call => call.Method == "Target.createTarget").Parameters!["hidden"]!.GetValue<bool>(), "表示タブを作成しました");
        Check(calls.Where(call => call.Method == "Page.captureScreenshot").All(call => call.Session == "page"), "別のページを撮影しました");
        Check(calls.Where(call => call.Method == "Runtime.evaluate").All(call => call.Session == "matcher"), "対象ページ内で照合しました");
        Check(calls.All(call => call.Method != "Page.setBypassCSP"), "対象ページのCSPを解除しました");
        var close = calls.Single(call => call.Method == "Target.closeTarget");
        Check(close.Parameters!["targetId"]!.GetValue<string>() == "owned-template" && close.Session == null && close.Timeout == 1500 && !close.Token.IsCancellationRequested,
            "所有する環境の終了処理が不正です");
    });
    await Test("照合環境の初期化失敗は環境エラーとし、再試行と終了時に所有情報を保持する", async () =>
    {
        foreach (var rejected in new[] { "Target.attachToTarget", "Runtime.enable" })
        {
            var cdp = new MatcherTransport { RejectMethod = rejected };
            var matcher = new TemplateMatcher(cdp, "page");
            try { await matcher.MatchAsync(new() { ["path"] = file }, null); throw new Exception("初期化失敗を無視しました"); }
            catch (CrerException error) when (error.Code == 3) { }
            cdp.RejectMethod = null;
            await matcher.MatchAsync(new() { ["path"] = file }, null);
            await matcher.DisposeAsync();
            Check(cdp.Calls.Count(call => call.Method == "Target.createTarget") == 1, "初期化の再試行で環境を重複作成しました");
            Check(cdp.Calls.Count(call => call.Method == "Target.closeTarget") == 1, "初期化途中の環境を閉じませんでした");
        }
    });
    await Test("取消し済みの照合は撮影や環境作成を開始しない", async () =>
    {
        var cdp = new MatcherTransport();
        await using var matcher = new TemplateMatcher(cdp, "page");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await matcher.MatchAsync(new() { ["path"] = file }, null, cancellation.Token); throw new Exception("取消しを無視しました"); }
        catch (OperationCanceledException) { }
        Check(cdp.Calls.IsEmpty, "取消し後にCDPを呼びました");
    });
    await Test("評価待ちの取消し後も次の呼出しは同じ照合環境を使用できる", async () =>
    {
        var cdp = new MatcherTransport { BlockEvaluation = true };
        await using var matcher = new TemplateMatcher(cdp, "page");
        using var cancellation = new CancellationTokenSource();
        var matching = matcher.MatchAsync(new() { ["path"] = file }, null, cancellation.Token);
        await cdp.Evaluating.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try { await matching.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("評価の取消しを無視しました"); }
        catch (OperationCanceledException) { }
        cdp.BlockEvaluation = false;
        await matcher.MatchAsync(new() { ["path"] = file }, null);
        Check(cdp.Calls.Count(call => call.Method == "Target.createTarget") == 1, "取消し後に環境を重複作成しました");
    });
    await Test("終了処理は実行中の評価を取り消してから環境を一度だけ閉じる", async () =>
    {
        var cdp = new MatcherTransport { BlockEvaluation = true };
        var matcher = new TemplateMatcher(cdp, "page");
        var matching = matcher.MatchAsync(new() { ["path"] = file }, null);
        await cdp.Evaluating.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await matcher.DisposeAsync(); await matcher.DisposeAsync();
        try { await matching; throw new Exception("終了時の評価を取り消しませんでした"); }
        catch (OperationCanceledException) { }
        Check(cdp.EvaluationCancelled && cdp.Calls.Count(call => call.Method == "Target.closeTarget") == 1, "評価を回収せず、または環境を重複して閉じました");
        try { await matcher.MatchAsync(new() { ["path"] = file }, null); throw new Exception("終了後に照合しました"); }
        catch (ObjectDisposedException) { }
    });
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed record MatcherCall(string Method, JsonObject? Parameters, string? Session, CancellationToken Token, int Timeout);
sealed class MatcherTransport : ICdpTransport
{
    public System.Collections.Concurrent.ConcurrentQueue<MatcherCall> Calls { get; } = new();
    public TaskCompletionSource Evaluating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? RejectMethod { get; set; }
    public bool BlockEvaluation { get; set; }
    public bool EvaluationCancelled { get; private set; }
    public async Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null, CancellationToken token = default, int timeoutMs = 10000)
    {
        Calls.Enqueue(new(method, parameters?.DeepClone().AsObject(), session, token, timeoutMs));
        token.ThrowIfCancellationRequested();
        if (method == RejectMethod) throw new CrerException(4, "初期化を拒否しました");
        if (method == "Target.createTarget") return new() { ["targetId"] = "owned-template" };
        if (method == "Target.attachToTarget") return new() { ["sessionId"] = "matcher" };
        if (method == "Page.captureScreenshot") return new() { ["data"] = "c2NyZWVu" };
        if (method == "Runtime.evaluate")
        {
            if (BlockEvaluation)
            {
                Evaluating.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { EvaluationCancelled = true; throw; }
            }
            var match = new TemplateMatch(1, 2, 3, 4, 1).ToJson();
            return new() { ["result"] = new JsonObject { ["value"] = Json.Text(parameters?["expression"]).Contains("const candidates", StringComparison.Ordinal) ? new JsonArray((JsonNode)match) : match } };
        }
        return new();
    }
}
