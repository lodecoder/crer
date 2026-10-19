using System.Reflection;
using System.Text.Json.Nodes;
using Crer;

internal static class RelatedNetworkPlaybackTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("親子の同じrequestIdを分離して記録しdetachしたscopeだけを除去する", async () =>
        {
            await using var fixture = new PlaybackFixture();
            fixture.Attach("child", "iframe");
            fixture.Event("Network.requestWillBeSent", new() { ["requestId"] = "same", ["type"] = "Fetch", ["url"] = "secret" }, "parent");
            fixture.Event("Network.requestWillBeSent", new() { ["requestId"] = "same", ["type"] = "Fetch", ["url"] = "secret" }, "child");
            fixture.Event("Network.requestWillBeSent", new() { ["requestId"] = "other" }, "unrelated");
            check(fixture.Count("observedRequests") == 2 && fixture.Count("requests") == 1, "要求ID衝突か無関係なtargetの混入がある");
            fixture.Event("Network.loadingFinished", new() { ["requestId"] = "same", ["encodedDataLength"] = 42 }, "child");
            check(fixture.Count("observedRequests") == 1 && fixture.Count("requests") == 1, "子の終了で親要求を消した");
            fixture.Event("Network.requestWillBeSent", new() { ["requestId"] = "pending" }, "child");
            fixture.Event("Target.detachedFromTarget", new() { ["sessionId"] = "child" }, "parent");
            check(fixture.Count("observedRequests") == 1 && fixture.Count("requests") == 1, "detachで親要求を消したか子要求が残った");
            var text = await fixture.NetworkText();
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
            check(rows.Any(row => Json.Text(row["sessionId"]) == "child" && Json.Text(row["targetType"]) == "iframe"), "子iframeの診断scopeがない");
            check(rows.Last()["removedPending"]?.GetValue<int>() == 1 && !text.Contains("secret"), "detach件数が違うか任意のページ値を保存した");
        });

        await test("子の長期通信を記録しても従来のmain文書のnetwork_idle集計を変えない", async () =>
        {
            await using var fixture = new PlaybackFixture();
            fixture.Attach("worker", "worker");
            typeof(Playback).GetField("lastNetworkActivity", Flags)!.SetValue(fixture.Playback, 123L);
            fixture.Event("Network.requestWillBeSent", new() { ["requestId"] = "long" }, "worker");
            check(fixture.Count("observedRequests") == 1 && fixture.Count("requests") == 0, "子の通信がmain idleを妨げた");
            check((long)typeof(Playback).GetField("lastNetworkActivity", Flags)!.GetValue(fixture.Playback)! == 123L,
                "子の通信でmainの最終通信時刻を更新した");
            fixture.Event("Network.requestWillBeSent", new() { ["requestId"] = "main" }, "parent");
            check(fixture.Count("requests") == 1 && (long)typeof(Playback).GetField("lastNetworkActivity", Flags)!.GetValue(fixture.Playback)! != 123L,
                "mainの通信までidle集計から外した");
        });
    }

    private sealed class PlaybackFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "crer-related-network-" + Guid.NewGuid().ToString("N"));
        private readonly RelatedTargetObserver observer;
        private readonly Action<string, JsonObject, string?> handler;
        internal Playback Playback { get; } = new();

        internal PlaybackFixture()
        {
            typeof(Playback).GetMethod("InitializeRunDirectory", Flags)!.Invoke(Playback, [directory]);
            typeof(Playback).GetField("session", Flags)!.SetValue(Playback, "parent");
            var detach = typeof(Playback).GetMethod("DetachNetworkScope", Flags)!.CreateDelegate<Action<string>>(Playback);
            observer = new RelatedTargetObserver(new ImmediateTransport(), "parent", detach);
            typeof(Playback).GetField("relatedTargets", Flags)!.SetValue(Playback, observer);
            handler = typeof(Playback).GetMethod("OnNetworkEvent", Flags)!.CreateDelegate<Action<string, JsonObject, string?>>(Playback);
        }

        internal void Attach(string id, string type) => Event("Target.attachedToTarget",
            new() { ["sessionId"] = id, ["targetInfo"] = new JsonObject { ["type"] = type } }, "parent");
        internal void Event(string method, JsonObject parameters, string session) => handler(method, parameters, session);
        internal int Count(string field)
        {
            var value = typeof(Playback).GetField(field, Flags)!.GetValue(Playback)!;
            return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
        }
        internal async Task<string> NetworkText()
        {
            await Log("networkLog").CompleteAsync();
            return File.ReadAllText(Path.Combine(directory, "network.ndjson"));
        }
        private AsyncDiagnosticLog Log(string field) => (AsyncDiagnosticLog)typeof(Playback).GetField(field, Flags)!.GetValue(Playback)!;
        public async ValueTask DisposeAsync()
        {
            await observer.DisposeAsync();
            await Log("networkLog").CompleteAsync();
            await Log("pageStateLog").CompleteAsync();
            Directory.Delete(directory, true);
        }
    }

    private sealed class ImmediateTransport : ICdpTransport
    {
        public Task<JsonObject> Call(string method, JsonObject? parameters = null, string? session = null,
            CancellationToken token = default, int timeoutMs = 10000) => Task.FromResult(new JsonObject());
    }
}
