using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Crer;

internal static class ProbeTools
{
    public static async Task<int> Hidden(string chrome, bool probeNetwork = false)
    {
        await using var fixture = new FixtureServer(); fixture.Start();
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(".crer", "integration", "hidden-" + id));
        var profile = Path.GetFullPath(Path.Combine(".crer", "profiles", "hidden-" + id)); Directory.CreateDirectory(directory); Directory.CreateDirectory(profile);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--app=" + (probeNetwork ? fixture.Base + "/reuse/probe0" : "data:text/html,"), "--no-first-run", "--no-default-browser-check", "--force-device-scale-factor=1", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port, "--user-data-dir=" + profile }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Exception("owned probe CfT could not start");
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await using var cdp = new Cdp();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }); string? websocket = null;
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && websocket == null)
            {
                try { websocket = Json.Text(JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"))?["webSocketDebuggerUrl"]); }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            if (string.IsNullOrEmpty(websocket)) throw new Exception("owned probe CDP endpoint unavailable");
            await cdp.Open(websocket, default);
            var before = await cdp.Call("Target.getTargets");
            if (probeNetwork)
            {
                var page = before["targetInfos"]!.AsArray().Single(target => Json.Text(target!["type"]) == "page")!;
                var attachedPage = await cdp.Call("Target.attachToTarget", new() { ["targetId"] = page["targetId"]!.DeepClone(), ["flatten"] = true });
                var pageSession = Json.Text(attachedPage["sessionId"]);
                var events = new List<JsonObject>(); var observations = new JsonArray();
                cdp.Event += (method, data, session) =>
                {
                    if (session != pageSession || !method.StartsWith("Network.")) return;
                    lock (events) events.Add(new() { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["method"] = method, ["data"] = data.DeepClone() });
                };
                await cdp.Call("Network.enable", session: pageSession); await cdp.Call("Runtime.enable", session: pageSession);
                nint window = 0; var windowDeadline = DateTime.UtcNow.AddSeconds(5);
                while (window == 0 && DateTime.UtcNow < windowDeadline) { window = NativeWindows.Find(process.Id); await Task.Delay(50); }
                if (window == 0) throw new Exception("owned network probe HWND unavailable");
                NativeWindows.SetOwnedAlpha(window, 0);
                foreach (var name in new[] { "probe1", "probe2", "probe3" })
                {
                    if (name == "probe3") NativeWindows.SetOwnedAlpha(window, 255);
                    await cdp.Call("Page.navigate", new() { ["url"] = fixture.Base + "/reuse/" + name }, pageSession);
                    await Task.Delay(2500);
                    observations.Add(new JsonObject { ["name"] = name, ["alpha"] = NativeWindows.Alpha(window), ["state"] = await cdp.Call("Runtime.evaluate", new()
                    { ["expression"] = "({ready:document.readyState,resources:performance.getEntriesByType('resource').map(r=>({name:r.name,transferSize:r.transferSize,responseEnd:r.responseEnd}))})", ["returnByValue"] = true }, pageSession) });
                }
                List<JsonObject> snapshot; lock (events) snapshot = events.Select(item => item.DeepClone().AsObject()).ToList();
                var requests = snapshot.Where(item => Json.Text(item["method"]) == "Network.requestWillBeSent").Select(item => Json.Text(item["data"]!["requestId"])).ToHashSet();
                foreach (var item in snapshot.Where(item => Json.Text(item["method"]) is "Network.loadingFinished" or "Network.loadingFailed")) requests.Remove(Json.Text(item["data"]!["requestId"]));
                var bodies = new JsonArray();
                foreach (var request in requests)
                {
                    try { bodies.Add(new JsonObject { ["requestId"] = request, ["body"] = await cdp.Call("Network.getResponseBody", new() { ["requestId"] = request }, pageSession, timeoutMs: 2000) }); }
                    catch (CrerException error) { bodies.Add(new JsonObject { ["requestId"] = request, ["error"] = error.Message }); }
                }
                var result = new JsonObject { ["events"] = new JsonArray(snapshot.Select(item => (JsonNode)item).ToArray()), ["observations"] = observations, ["pendingBodies"] = bodies,
                    ["fixture"] = new JsonArray(fixture.Exchanges.Select(item => (JsonNode)new JsonObject { ["path"] = item.Path, ["phase"] = item.Phase }).ToArray()) };
                File.WriteAllText(Path.Combine(directory, "network-probe.json"), result.ToJsonString(Json.Indented));
                Console.WriteLine("pending requests: " + string.Join(",", requests)); Console.WriteLine("evidence: " + directory);
                await cdp.Call("Browser.close"); return 0;
            }
            var created = await cdp.Call("Target.createTarget", new() { ["url"] = "data:text/html,", ["background"] = true, ["hidden"] = true });
            var after = await cdp.Call("Target.getTargets");
            var all = await cdp.Call("Target.getTargets", new() { ["filter"] = new JsonArray((JsonNode)new JsonObject { ["exclude"] = false }) });
            var attached = await cdp.Call("Target.attachToTarget", new() { ["targetId"] = created["targetId"]!.DeepClone(), ["flatten"] = true });
            var value = await cdp.Call("Runtime.evaluate", new() { ["expression"] = @"new RegExp('(?<$name>ad)\\.svg$').test('ad.svg')", ["returnByValue"] = true }, Json.Text(attached["sessionId"]));
            var observation = new JsonObject { ["pid"] = process.Id, ["profile"] = profile, ["before"] = before, ["created"] = created, ["after"] = after, ["all"] = all, ["match"] = value };
            File.WriteAllText(Path.Combine(directory, "probe.json"), observation.ToJsonString(Json.Indented));
            Console.WriteLine(observation.ToJsonString(Json.Indented)); Console.WriteLine("evidence: " + directory);
            var protocol = await http.GetStringAsync($"http://127.0.0.1:{port}/json/protocol"); File.WriteAllText(Path.Combine(directory, "protocol.json"), protocol);
            await cdp.Call("Browser.close"); return 0;
        }
        finally
        {
            try { await cdp.Call("Browser.close", timeoutMs: 1000); } catch (CrerException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            File.WriteAllText(Path.Combine(directory, "chrome-stderr.log"), await stderr); File.WriteAllText(Path.Combine(directory, "chrome-stdout.log"), await stdout);
        }
    }
}
