using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Crer;

internal static class PageStateMonitorTests
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("ページと子iframeの状態だけを観測し値の全文やworkerを記録しない", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "crer-page-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await using var fixture = await Connection.Create();
                var log = new AsyncDiagnosticLog(Path.Combine(directory, "state.ndjson"));
                await using var monitor = new PageStateMonitor(fixture.Client,
                    () => [("parent", "page"), ("child", "iframe"), ("worker", "worker")], log);
                foreach (var id in new[] { "parent", "child" })
                {
                    var request = await fixture.Read();
                    check(Json.Text(request["method"]) == "Runtime.evaluate" && Json.Text(request["sessionId"]) == id,
                        "状態監視が想定外の操作かsessionを使用した");
                    check(request["params"]?["silent"]?.GetValue<bool>() == true, "状態監視がページの例外を抑制しなかった");
                    await fixture.Reply(request, new()
                    {
                        ["result"] = new JsonObject { ["value"] = new JsonObject
                        {
                            ["ready"] = "complete", ["visibility"] = "visible", ["focused"] = id == "parent", ["online"] = true,
                            ["url"] = "https://private.invalid/?token=secret", ["message"] = "secret",
                        } },
                    });
                }
                await monitor.DisposeAsync();
                await log.CompleteAsync();
                var text = File.ReadAllText(Path.Combine(directory, "state.ndjson"));
                var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
                check(rows.Length == 2 && rows.All(row => Json.Text(row["status"]) == "ok"), "親とiframeの観測が揃わなかった");
                check(rows[1]["focused"]?.GetValue<bool>() == false && !text.Contains("secret") && !text.Contains("private.invalid"),
                    "iframeの状態を取り違えたか任意のページ値を保存した");
                check(monitor.Diagnostic()["stopped"]?.GetValue<bool>() == true, "監視を停止できなかった");
            }
            finally { Directory.Delete(directory, true); }
        });

        await test("rendererが状態照会へ応答しなくても監視を有限に終了しCDP接続を維持する", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "crer-page-state-timeout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await using var fixture = await Connection.Create();
                var log = new AsyncDiagnosticLog(Path.Combine(directory, "state.ndjson"));
                await using var monitor = new PageStateMonitor(fixture.Client, () => [("parent", "page")], log);
                await fixture.Read();
                var watch = Stopwatch.StartNew();
                await monitor.DisposeAsync();
                check(watch.Elapsed < TimeSpan.FromSeconds(2) && !fixture.Client.Disconnected.IsCompleted,
                    "監視停止が待ち続けたか再利用するCDP接続を切断した");
                var next = fixture.Client.Call("Test.afterStateTimeout");
                await fixture.Reply(await fixture.Read(), new() { ["ok"] = true });
                check((await next)["ok"]?.GetValue<bool>() == true, "状態照会timeout後に操作できなかった");
                await log.CompleteAsync();
                var row = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "state.ndjson")))!;
                check(Json.Text(row["status"]) == "unavailable" && !row.AsObject().ContainsKey("message"), "状態取得不能の安全な記録がない");
            }
            finally { Directory.Delete(directory, true); }
        });

        await test("ダイアログとJavaScript例外の診断は本文や入力値を保存しない", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "crer-page-events-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var playback = new Playback();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                typeof(Playback).GetMethod("InitializeRunDirectory", flags)!.Invoke(playback, [directory]);
                typeof(Playback).GetField("session", flags)!.SetValue(playback, "page-events");
                var handler = typeof(Playback).GetMethod("OnNetworkEvent", flags)!
                    .CreateDelegate<Action<string, JsonObject, string?>>(playback);
                handler("Page.javascriptDialogOpening", new() { ["type"] = "beforeunload", ["message"] = "secret", ["url"] = "secret" }, "page-events");
                handler("Page.javascriptDialogClosed", new() { ["result"] = true, ["userInput"] = "secret" }, "page-events");
                handler("Runtime.exceptionThrown", new() { ["exceptionDetails"] = new JsonObject { ["text"] = "secret" } }, "page-events");
                var stateLog = (AsyncDiagnosticLog)typeof(Playback).GetField("pageStateLog", flags)!.GetValue(playback)!;
                await stateLog.CompleteAsync();
                var text = File.ReadAllText(Path.Combine(directory, "page-state.ndjson"));
                var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
                check(rows.Length == 3 && !text.Contains("secret") && Json.Text(rows[0]["dialogType"]) == "beforeunload",
                    "イベント診断が欠落したか本文を保存した");
                var networkLog = (AsyncDiagnosticLog)typeof(Playback).GetField("networkLog", flags)!.GetValue(playback)!;
                await networkLog.CompleteAsync();
            }
            finally { Directory.Delete(directory, true); }
        });
    }
}
