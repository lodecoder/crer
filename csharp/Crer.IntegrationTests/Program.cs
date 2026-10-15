using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Crer;

string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
if (args.Contains("--serve"))
{
    await using var server = new FixtureServer(); server.Start();
    var record = new JsonObject { ["base"] = server.Base, ["pid"] = Environment.ProcessId };
    if (Option("--server-info") is { } info) File.WriteAllText(info, record.ToJsonString());
    Console.WriteLine(record.ToJsonString());
    await Task.Delay(Option("--duration-ms") is { } duration ? int.Parse(duration) : 30000);
    return 0;
}
var exe = Path.GetFullPath(Option("--exe") ?? Environment.GetEnvironmentVariable("CRER_TEST_EXE") ?? "dist/win-x64/crer.exe");
var chrome = Option("--chrome") ?? Environment.GetEnvironmentVariable("CRER_TEST_CHROME");
var dll = Path.GetFullPath(Option("--dll") ?? "dist/win-x64/crer-win-input.dll");
if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(chrome) || !File.Exists(exe) || !File.Exists(chrome) || !File.Exists(dll))
{
    Console.Error.WriteLine("Usage: dotnet run --project csharp/Crer.IntegrationTests -- --exe <public NativeAOT crer.exe> --chrome <CfT chrome.exe> --dll <crer-win-input.dll> [--windows] [--only plan,blocking,keyboard,windows,display,profiles]");
    return 2;
}
if (args.Contains("--probe-hidden")) return await ProbeTools.Hidden(chrome!);
if (args.Contains("--probe-network")) return await ProbeTools.Hidden(chrome!, true);
if (args.Contains("--probe-startup")) return await StartupProbe.Run(chrome!);
var selected = Option("--only")?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
var id = Guid.NewGuid().ToString("N")[..10];
var evidence = Path.GetFullPath(Path.Combine(".crer", "integration", id)); Directory.CreateDirectory(evidence);
var ownProfiles = new List<string>();
var passed = 0; var failed = 0; var inconclusive = 0;
var summary = new JsonArray();
await using var fixture = new FixtureServer(); fixture.Start();
var ordinaryChrome = new Dictionary<int, string>();
foreach (var process in Process.GetProcessesByName("chrome"))
    using (process)
        try
        {
            var image = process.MainModule?.FileName;
            if (image != null && !Path.GetFullPath(image).Equals(Path.GetFullPath(chrome!), StringComparison.OrdinalIgnoreCase)) ordinaryChrome[process.Id] = image;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string Name(string name) => "integration-" + id + "-" + name;
string Profile(string name)
{
    var path = Path.GetFullPath(Path.Combine(".crer", "profiles", "integration-" + id + "-" + name)); ownProfiles.Add(path); return path;
}
string Write(string label, JsonObject value)
{
    var path = Path.Combine(evidence, label + ".yaml"); File.WriteAllText(path, value.ToJsonString()); return path;
}
JsonObject Sleep(int ms) => new() { ["do"] = "sleep", ["ms"] = ms };
JsonObject Wait(string text) => new() { ["do"] = "wait_for", ["state"] = "visible", ["locator_hint"] = new JsonObject { ["role"] = "status", ["text"] = text } };
JsonObject Idle() => new() { ["do"] = "wait_for", ["state"] = "network_idle" };
JsonObject Scenario(string label, string url, string? profile = null, JsonObject? window = null, params JsonObject[] steps) => new()
{
    ["version"] = 1, ["name"] = Name(label), ["browser"] = new JsonObject
    {
        ["initial_url"] = url, ["profile"] = profile == null ? "ephemeral" : "persistent:" + profile,
        ["window"] = window ?? new JsonObject { ["bounds"] = new JsonObject { ["left"] = 20, ["top"] = 20, ["width"] = 600, ["height"] = 400 } },
    }, ["steps"] = new JsonArray(steps.Select(step => (JsonNode)step).ToArray()),
};
JsonObject Leaf(string scenario) => new() { ["scenario"] = Path.GetFileName(scenario) };
JsonObject Serial(params JsonObject[] nodes) => new() { ["serial"] = new JsonArray(nodes.Select(node => (JsonNode)node).ToArray()) };
JsonObject Parallel(bool fast, params JsonObject[] nodes) => new() { ["parallel"] = new JsonObject { ["fail_fast"] = fast, ["jobs"] = new JsonArray(nodes.Select(node => (JsonNode)node).ToArray()) } };
JsonObject Plan(JsonObject node, bool reuse = true, int maximum = 2, int worker = 0) => new()
{
    ["version"] = 1, ["name"] = Name("plan"), ["max_parallel"] = maximum,
    ["browser_session"] = new JsonObject { ["reuse"] = "same-profile", ["focus"] = "before-step" },
    ["timeouts"] = new JsonObject { ["worker_ms"] = worker }, ["on_failure"] = new JsonObject { ["timeout"] = "continue" }, ["run"] = node,
};
RunningCommand Start(string label, params string[] parameters) => new(exe, parameters.Concat(new[] { "--chrome", chrome!, "--dll", dll }).ToArray(), evidence, label);
JsonNode TrailingJson(string text)
{
    for (var offset = 0; offset < text.Length; offset++)
        if ((offset == 0 || text[offset - 1] == '\n') && text[offset] is '[' or '{')
            try { return JsonNode.Parse(text[offset..])!; } catch (System.Text.Json.JsonException) { }
    throw new Exception("public CLI did not emit a trailing JSON result: " + text);
}
async Task<(CommandResult Command, JsonNode Json)> Run(string label, int expected, params string[] parameters)
{
    using var process = Start(label, parameters); var output = await process.Finish();
    Check(output.Code == expected, $"public CLI exit {output.Code}, expected {expected}: {output.Stderr}");
    return (output, TrailingJson(output.Stdout));
}
JsonObject Read(string directory, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name)))!.AsObject();
string RunDirectory(JsonNode result) => Path.GetFullPath(Json.Text(result["runDir"]));
void Cleanup(JsonNode result)
{
    var directory = RunDirectory(result); var record = Read(directory, "cleanup.json");
    Check(record["chromeExited"]?.GetValue<bool>() == true && Json.Number(record["pendingCdpRequests"]) == 0 && record["errors"]!.AsArray().Count == 0, "browser/pending/cleanup leak: " + record);
    var pid = (int)Json.Number(Read(directory, "run.json")["chromePid"]);
    try { using var process = Process.GetProcessById(pid); Check(process.HasExited, "owned Chrome remains alive: " + pid); } catch (ArgumentException) { }
}
async Task<(string Directory, JsonObject Metadata, JsonObject Launch, nint Window)> Ready(string label)
{
    var until = DateTime.UtcNow.AddSeconds(45);
    while (DateTime.UtcNow < until)
    {
        foreach (var directory in Directory.EnumerateDirectories(Path.GetFullPath(".crer/runs"), "*-" + Name(label) + "-*"))
            try
            {
                var metadata = Read(directory, "run.json"); var launch = Read(directory, "launch.json");
                var window = NativeWindows.Find((int)Json.Number(metadata["chromePid"]));
                if (Json.Text(launch["stage"]) == "ready" && window != 0) return (directory, metadata, launch, window);
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { }
        await Task.Delay(50);
    }
    throw new Exception("public CLI did not expose a ready owned CfT window for " + label);
}
async Task<Cdp> ObserveCdp(JsonObject metadata)
{
    var endpoint = Json.Text(metadata["cdpEndpoint"]); Check(endpoint.Length > 0, "ready run.json omitted CDP endpoint");
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    var version = JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/version"))!;
    var cdp = new Cdp(); await cdp.Open(Json.Text(version["webSocketDebuggerUrl"]), default); return cdp;
}
async Task Test(string key, string label, Func<Task> action)
{
    if (selected != null && !selected.Contains(key)) return;
    var began = DateTimeOffset.UtcNow;
    try
    {
        await action().WaitAsync(TimeSpan.FromMinutes(3)); passed++; Console.WriteLine("PASS " + label);
        summary.Add(new JsonObject { ["test"] = label, ["passed"] = true, ["startedAt"] = began.ToString("O") });
    }
    catch (ObservationInconclusive error)
    {
        inconclusive++; Console.Error.WriteLine("INCONCLUSIVE " + label + ": " + error.Message);
        summary.Add(new JsonObject { ["test"] = label, ["passed"] = false, ["status"] = "inconclusive", ["reason"] = error.Message, ["startedAt"] = began.ToString("O") });
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine("FAIL " + label + ": " + error.Message);
        summary.Add(new JsonObject { ["test"] = label, ["passed"] = false, ["error"] = error.ToString(), ["startedAt"] = began.ToString("O") });
    }
    File.WriteAllText(Path.Combine(evidence, "summary.json"), summary.ToJsonString(Json.Indented));
}

await Test("plan", "public plan parallel profile reuse and isolated worker timeout", async () =>
{
    var profileA = Profile("plan-a"); var profileB = Profile("plan-b");
    string File(string name, string profile, int sleep = 100) => Write(name, Scenario(name, fixture.Base + "/reuse/" + name, profile,
        new() { ["opacity"] = .7, ["foreground"] = false, ["bounds"] = new JsonObject { ["left"] = 20, ["top"] = 20, ["width"] = 400, ["height"] = 300 } },
        new() { ["do"] = "log", ["message"] = "started" }, Idle(), Sleep(sleep)));
    var a1 = File("a1", profileA); var a2 = File("a2", profileA); var b1 = File("b1", profileB); var b2 = File("b2", profileB);
    var path = Write("parallel-reuse", Plan(Parallel(false, Serial(Leaf(a1), Leaf(a2)), Serial(Leaf(b1), Leaf(b2)))));
    var playback = Run("parallel-reuse", 0, "run", path, "--plan-window-opacity-override", "0");
    var liveWindows = Task.WhenAll(Ready("a1"), Ready("a2"), Ready("b1"), Ready("b2"));
    var (command, value) = await playback; var results = value.AsArray(); var observedWindows = await liveWindows;
    Check(observedWindows[0].Window == observedWindows[1].Window && observedWindows[2].Window == observedWindows[3].Window
        && observedWindows[0].Window != observedWindows[2].Window, "same-profile HWND identity changed or distinct profiles shared one window");
    System.IO.File.WriteAllText(Path.Combine(evidence, "plan-window-identities.json"), new JsonArray(observedWindows.Select(window => (JsonNode)new JsonObject
    { ["scenario"] = window.Metadata["scenario"]!.DeepClone(), ["pid"] = window.Metadata["chromePid"]!.DeepClone(), ["hwnd"] = "0x" + window.Window.ToString("x") }).ToArray()).ToJsonString(Json.Indented));
    Check(results.Count == 4 && fixture.Overlapped, "parallel leaves did not overlap or four results were not returned");
    Check(!command.Stdout.Contains("\x1b[") && !command.Stdout.Contains("[crer]"), "redirected scenario logs changed");
    foreach (var pair in new[] { ("a1", "a2"), ("b1", "b2") })
    {
        var first = results.Single(result => Json.Text(Read(RunDirectory(result!), "run.json")["scenario"]) == Name(pair.Item1))!;
        var last = results.Single(result => Json.Text(Read(RunDirectory(result!), "run.json")["scenario"]) == Name(pair.Item2))!;
        var initial = Read(RunDirectory(first), "run.json"); var final = Read(RunDirectory(last), "run.json");
        Check(initial["browserSession"]!["reused"]!.GetValue<bool>() == false && final["browserSession"]!["reused"]!.GetValue<bool>() && Json.Number(initial["chromePid"]) == Json.Number(final["chromePid"]), "profile process was not reused");
        var initialLaunch = Read(RunDirectory(first), "launch.json"); var finalLaunch = Read(RunDirectory(last), "launch.json");
        Check(Json.Text(initialLaunch["targetId"]) == Json.Text(finalLaunch["targetId"]) && Json.Text(initialLaunch["sessionId"]) == Json.Text(finalLaunch["sessionId"]), "page/target/session identity changed on reuse");
        Check(fixture.Tokens.TryGetValue(pair.Item1, out var token) && token == fixture.Tokens[pair.Item2], "sessionStorage did not survive same-profile reuse");
        Check(Json.Number(initial["windowOpacityOverride"]) == 0, "run opacity override not recorded"); Cleanup(last);
    }
    Check(fixture.Tokens["a1"] != fixture.Tokens["b1"], "profiles shared a page");
    var t1 = File("t1", profileA, 15000); var t2 = File("t2", profileA); var u1 = File("u1", profileB, 2000); var u2 = File("u2", profileB, 3000);
    path = Write("parallel-timeout", Plan(Parallel(false, Serial(Leaf(t1), Leaf(t2)), Serial(Leaf(u1), Leaf(u2))), worker: 6000));
    (_, value) = await Run("parallel-timeout", 4, "run", path); results = value.AsArray(); Check(results.Count == 4, "timeout incorrectly aborted unrelated profile or continuation");
    foreach (var result in results)
    {
        var metadata = Read(RunDirectory(result!), "run.json"); var name = Json.Text(metadata["scenario"]);
        if (name == Name("t1")) Check(result!["failures"]!.AsArray().Any(failure => Json.Text(failure).Contains("plan:timeout:worker_ms")), "worker timeout marker missing");
        else Check(Json.Number(result!["code"]) == 0, "unrelated/replacement scenario failed: " + result);
        if (name == Name("t2")) Check(metadata["browserSession"]!["reused"]!.GetValue<bool>() == false, "timed-out session was reused");
        if (name == Name("u2")) Check(metadata["browserSession"]!["reused"]!.GetValue<bool>(), "unrelated retained session was discarded");
        if (name != Name("u1")) Cleanup(result!);
    }
});

await Test("plan", "public parallel fail-fast returns four and drains owned processes", async () =>
{
    var fail = Write("fast-fail", Scenario("fast-fail", fixture.Base + "/window", Profile("fail"), null, Sleep(2000), new() { ["do"] = "fail", ["message"] = "explicit integration failure" }));
    var running = Write("fast-running", Scenario("fast-running", fixture.Base + "/window", Profile("running"), null, Sleep(20000)));
    var queued = Write("fast-queued", Scenario("fast-queued", fixture.Base + "/window", null, null, Sleep(10)));
    var path = Write("fast-plan", Plan(Parallel(true, Leaf(fail), Leaf(running), Leaf(queued))));
    var (_, value) = await Run("parallel-failfast", 4, "run", path); Check(value.AsArray().Count == 2, "fail-fast started queued work");
    foreach (var result in value.AsArray()) { Check(Json.Number(result!["code"]) == 4, "internal cancellation was reported as user interrupt"); Cleanup(result!); }
});

await Test("blocking", "public URL blocking initial navigation replacement removal and JS regex", async () =>
{
    var profile = Profile("blocking"); var files = new List<string>();
    var expected = new[] { "ad.svg:blocked logo.svg:loaded redirect:blocked", "ad.svg:loaded logo.svg:blocked redirect:loaded", "ad.svg:loaded logo.svg:loaded redirect:loaded", "ad.svg:blocked logo.svg:loaded redirect:blocked" };
    for (var index = 0; index < expected.Length; index++)
    {
        var url = fixture.Base + "/block?case=" + index;
        var scenario = Scenario("block" + index, url, profile, null, Wait(expected[index]), Idle(), new() { ["do"] = "navigate", ["url"] = url + "&again=1" }, Wait(expected[index]), Idle(), Sleep(3000));
        if (index < 2) scenario["browser"]!["block_urls"] = new JsonArray((JsonNode?)JsonValue.Create(index == 0 ? @"/ad\.svg(\?.*)?$" : @"/logo\.svg$"));
        if (index == 3) scenario["browser"]!["block_urls"] = new JsonArray((JsonNode?)JsonValue.Create("[]"), (JsonNode?)JsonValue.Create(@"/(?<$image>ad)\.svg(\?.*)?$"));
        files.Add(Write("block" + index, scenario));
    }
    var path = Write("blocking-plan", Plan(Serial(files.Select(Leaf).ToArray())));
    using var process = Start("blocking-plan", "run", path);
    var ready = await Ready("block0"); await using (var cdp = await ObserveCdp(ready.Metadata))
    {
        var targets = await cdp.Call("Target.getTargets"); Check(targets["targetInfos"]!.AsArray().Count(target => Json.Text(target!["type"]) == "page") == 1, "app launched extra page targets");
        var attached = await cdp.Call("Target.attachToTarget", new() { ["targetId"] = ready.Launch["targetId"]!.DeepClone(), ["flatten"] = true });
        var display = await cdp.Call("Runtime.evaluate", new() { ["expression"] = "matchMedia('(display-mode: standalone)').matches", ["returnByValue"] = true }, Json.Text(attached["sessionId"]));
        Check(display["result"]!["value"]!.GetValue<bool>(), "CfT lost app-window mode");
    }
    var command = await process.Finish(); Check(command.Code == 0, "blocking plan failed: " + command.Stderr); var results = TrailingJson(command.Stdout).AsArray();
    Check(results.Count == expected.Length, "blocking continuation results missing");
    for (var index = 0; index < expected.Length; index++)
    {
        var requests = fixture.BlockRequests.Where(item => item.Case == index.ToString()).Select(item => item.Path).ToArray();
        if (index == 0) Check(!requests.Contains("/ad.svg") && requests.Contains("/logo.svg"), "initial ad blocker failed open");
        if (index == 1) Check(requests.Contains("/ad.svg") && !requests.Contains("/logo.svg"), "replacement blocker not applied or cache bypass failed");
        // With blocking disabled, cache use is restored as well. The baseline asserts
        // the loaded page result here; it does not require a fresh resource request.
        if (index == 3) Check(!requests.Contains("/ad.svg") && requests.Contains("/logo.svg"), "ECMAScript-only regex syntax changed at runtime");
    }
    Cleanup(results.Last()!);
});

await Test("blocking", "public URL blocker rejects initial main request and cleans up", async () =>
{
    var scenario = Scenario("block-main", fixture.Base + "/blocked-initial");
    scenario["browser"]!["block_urls"] = new JsonArray((JsonNode?)JsonValue.Create("/blocked-initial$"));
    var path = Write("block-main", scenario); var (_, result) = await Run("block-main", 3, "play", path);
    Check(!fixture.Requests.Contains("/blocked-initial"), "main navigation escaped interception"); Cleanup(result);
});

await Test("keyboard", "public raw normalize validate replay retains Tab and both Space recordings", async () =>
{
    foreach (var spaceKind in new[] { 9, 7 })
    {
        var raw = Path.Combine(evidence, "keyboard-" + spaceKind + ".ndjson"); var events = new List<JsonObject>();
        void Push(int kind, int data) => events.Add(new() { ["qpc"] = (events.Count + 1).ToString(), ["kind"] = kind, ["data"] = data, ["x"] = 80, ["y"] = 15 });
        void Key(int key) { Push(7, key << 16); Push(8, key << 16); }
        void Text(string value) { foreach (var character in value) Push(9, character); }
        void Space() { if (spaceKind == 9) Text(" "); else Key(32); }
        Push(2, 0); Push(3, 0); Text("demo@example.test"); Key(9); Text("demo"); Space(); Text("password"); Key(9); Key(9); Space(); Key(9); Space();
        File.WriteAllLines(raw, events.Select(value => value.ToJsonString())); var yaml = Path.Combine(evidence, "keyboard-" + spaceKind + ".yaml");
        using (var normalize = new RunningCommand(exe, ["normalize", raw, "--output", yaml, "--url", fixture.Base + "/keyboard?case=" + spaceKind, "--name", Name("keyboard" + spaceKind)], evidence, "normalize" + spaceKind))
        { var output = await normalize.Finish(); Check(output.Code == 0, "public normalize failed: " + output.Stderr); }
        var scenario = Crer.Scenario.Load(yaml); scenario.Steps.Add(Wait("submitted")); File.WriteAllText(yaml, scenario.Root.ToJsonString());
        using (var validate = new RunningCommand(exe, ["validate", yaml], evidence, "validate" + spaceKind))
        { var output = await validate.Finish(); Check(output.Code == 0, "public validate failed: " + output.Stderr); }
        var (_, result) = await Run("keyboard-play" + spaceKind, 0, "play", yaml); Cleanup(result);
        Check(fixture.Snapshots.TryGetValue(spaceKind.ToString(), out var snapshot), "fixture did not observe a login submission"); var state = JsonNode.Parse(snapshot!)!;
        Check(Json.Text(state["submitted"]!["email"]) == "demo@example.test" && Json.Text(state["submitted"]!["password"]) == "demo password" && state["submitted"]!["remember"]!.GetValue<bool>(), "normalized login result changed");
        var observed = state["events"]!.AsArray();
        Check(observed.Where(item => Json.Text(item!["type"]) == "focusin").Select(item => Json.Text(item!["id"])).SequenceEqual(new[] { "email", "password", "toggle", "remember", "login" }), "Tab focus order changed");
        foreach (var control in new[] { "password", "remember", "login" })
            Check(observed.Where(item => Json.Text(item!["id"]) == control && Json.Text(item["code"]) == "Space").Select(item => Json.Text(item!["type"]) + ":" + Json.Text(item["key"])).SequenceEqual(new[] { "keydown: ", "keyup: " }), "Space key/code/down/up changed on " + control);
    }
});

await Test("profiles", "public profile exclusion and parent traversal refusal", async () =>
{
    var profile = Profile("exclusive"); var path = Write("exclusive", Scenario("exclusive", fixture.Base + "/window", profile, null, Sleep(6000)));
    using var first = Start("exclusive-first", "play", path); await Ready("exclusive");
    var (_, result) = await Run("exclusive-second", 3, "play", path); Check(result["failures"]!.AsArray().Any(item => Json.Text(item).Contains("already in use")), "profile exclusion diagnostic missing");
    var output = await first.Finish(); Check(output.Code == 0, "first profile owner failed"); Cleanup(TrailingJson(output.Stdout));
    using var escape = Start("profile-escape", "play", path, "--profile-dir", ".crer/profiles/../outside"); var rejected = await escape.Finish(); Check(rejected.Code == 2, "profile traversal was not rejected");
});

await Test("profiles", "public profile junction escape is rejected before browser launch", async () =>
{
    var target = Path.Combine(evidence, "junction-target"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "sentinel.txt"), "preserved");
    var link = Profile("junction"); var path = Write("junction", Scenario("junction", fixture.Base + "/window"));
    try
    {
        Junction.Create(link, target);
        using var command = Start("profile-junction", "play", path, "--profile-dir", link); var result = await command.Finish();
        Check(result.Code == 2 && result.Stderr.Contains("outside", StringComparison.Ordinal), "profile junction escape was accepted: " + result.Stderr);
        Check(File.ReadAllText(Path.Combine(target, "sentinel.txt")) == "preserved" && !Directory.Exists(Path.Combine(target, "Default")), "escaped target was modified before rejection");
    }
    finally
    {
        var owned = Path.GetFullPath(link); var expectedParent = Path.GetFullPath(".crer/profiles") + Path.DirectorySeparatorChar;
        if (owned.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(owned) == "integration-" + id + "-junction" && Directory.Exists(owned))
            Directory.Delete(owned, false); // Delete only this owned junction, never traverse its target.
    }
});

await Test("display", "public DPI compatibility removal physical bounds and CSS viewport", async () =>
{
    var path = Write("display", Scenario("display", fixture.Base + "/window", Profile("display"), new()
    { ["bounds"] = new JsonObject { ["left"] = 25, ["top"] = 12 }, ["content"] = new JsonObject { ["width"] = 1330, ["height"] = 1200 } }, Sleep(4000)));
    using var process = new RunningCommand(exe, ["play", path, "--ignore-viewport-mismatch", "--chrome", chrome!, "--dll", dll], evidence, "display",
        new() { ["__COMPAT_LAYER"] = "GDIDPISCALING DPIUNAWARE" });
    var ready = await Ready("display"); var physical = NativeWindows.Measure(ready.Window);
    var display = Read(ready.Directory, "display.json"); Check(physical.Awareness > 0, "CfT remained DPI-unaware");
    await using (var cdp = await ObserveCdp(ready.Metadata))
    {
        var window = await cdp.Call("Browser.getWindowForTarget", new() { ["targetId"] = ready.Launch["targetId"]!.DeepClone() });
        var bounds = await cdp.Call("Browser.getWindowBounds", new() { ["windowId"] = window["windowId"]!.DeepClone() });
        Check(physical.Rect.Right - physical.Rect.Left == Json.Number(bounds["bounds"]!["width"]) && physical.Rect.Bottom - physical.Rect.Top == Json.Number(bounds["bounds"]!["height"]), "physical and CDP bounds differ");
    }
    Check(physical.Rect.Left == 25 && physical.Rect.Top == 12 && Json.Number(display["actual"]!["dpr"]) == 1 && Json.Number(display["actual"]!["scale"]) == 1 && Json.Number(display["actual"]!["width"]) == 1330 && Json.Number(display["actual"]!["height"]) == 1200, "DPI/CSS size alignment changed: " + display);
    var output = await process.Finish(); Check(output.Code == 0, "display playback failed: " + output.Stderr); Cleanup(TrailingJson(output.Stdout));
});

await Test("display", "public strict advisory and off DPR diagnostics", async () =>
{
    foreach (var mode in new[] { "strict", "advisory", "off" })
    {
        var scenario = Scenario("dpr-" + mode, fixture.Base + "/window");
        scenario["browser"]!["display"] = new JsonObject { ["expected_dpr"] = 2, ["browser_zoom"] = 100, ["zoom_check"] = mode };
        var path = Write("dpr-" + mode, scenario); var (command, result) = await Run("dpr-" + mode, mode == "strict" ? 3 : 0, "play", path);
        Check(command.Stderr.Contains("DPR mismatch", StringComparison.Ordinal) == (mode != "off"), "DPR diagnostic policy changed for " + mode);
        Check(Json.Number(Read(RunDirectory(result), "display.json")["actual"]!["dpr"]) == 1, "test did not measure actual DPR1"); Cleanup(result);
    }
});

await Test("display", "public viewport mismatch strict failure and explicit ignore diagnostic", async () =>
{
    var scenario = Scenario("viewport-mismatch", fixture.Base + "/window", null, new()
    { ["content"] = new JsonObject { ["width"] = 600, ["height"] = 400 }, ["viewport"] = new JsonObject { ["width"] = 601, ["height"] = 400 } });
    var path = Write("viewport-mismatch", scenario); var (strict, rejected) = await Run("viewport-strict", 3, "play", path);
    Check(strict.Stderr.Contains("Viewport mismatch", StringComparison.Ordinal), "strict viewport mismatch diagnostic missing"); Cleanup(rejected);
    var (ignored, result) = await Run("viewport-ignore", 0, "play", path, "--ignore-viewport-mismatch");
    var display = Read(RunDirectory(result), "display.json");
    Check(ignored.Stderr.Contains("--ignore-viewport-mismatch", StringComparison.Ordinal) && display["ignoreViewportMismatch"]!.GetValue<bool>()
        && Json.Number(display["actual"]!["width"]) == 600 && Json.Number(display["expectedViewport"]!["width"]) == 601, "explicit viewport investigation did not preserve mismatch diagnostic"); Cleanup(result);
});

await Test("record", "public timed recording removes inherited DPI compatibility layers", async () =>
{
    var raw = Path.Combine(evidence, "record-dpi.ndjson");
    using var command = new RunningCommand(exe, ["record", raw, "--chrome", chrome!, "--dll", dll, "--url", fixture.Base + "/window", "--position", "25,12", "--content-size", "600,400", "--duration-ms", "1500"],
        evidence, "record-dpi", new() { ["__COMPAT_LAYER"] = "DPIUNAWARE" });
    var output = await command.Finish(); Check(output.Code == 0, "timed record DPI probe failed: " + output.Stderr);
    var metadata = JsonNode.Parse(File.ReadAllText(raw + ".meta.json"))!;
    Check(Json.Number(metadata["content_rect_screen_px"]!["width"]) == 600 && Json.Number(metadata["content_rect_screen_px"]!["height"]) == 400
        && Json.Number(metadata["css_viewport"]!["x"]) == 600 && Json.Number(metadata["css_viewport"]!["y"]) == 400, "recording physical/CSS coordinate spaces diverged: " + metadata);
    Check(metadata["incomplete"]?.GetValue<bool>() != true, "timed DPI recording was incomplete");
});

await Test("observation", "public background CDP actions preserve cursor and foreground", async () =>
{
    var originalForeground = NativeWindows.GetForegroundWindow(); var originalCursor = NativeWindows.Cursor();
    var observations = new JsonArray();
    var path = Write("background", Scenario("background", fixture.Base + "/window", null, null,
        Sleep(500), new() { ["do"] = "mouse_move", ["at"] = new JsonObject { ["x"] = 200, ["y"] = 150 } },
        new() { ["do"] = "click", ["at"] = new JsonObject { ["x"] = 100, ["y"] = 100 } }, Sleep(1000)));
    using var command = Start("background", "play", path);
    var cursorChanged = false; var foregroundChanged = false;
    while (!command.Process.HasExited)
    {
        var cursor = NativeWindows.Cursor(); var foreground = NativeWindows.GetForegroundWindow();
        cursorChanged |= cursor.X != originalCursor.X || cursor.Y != originalCursor.Y;
        foregroundChanged |= foreground != originalForeground;
        observations.Add(new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["foreground"] = "0x" + foreground.ToString("x"), ["foregroundPid"] = NativeWindows.Owner(foreground), ["cursorX"] = cursor.X, ["cursorY"] = cursor.Y });
        await Task.Delay(25);
    }
    var output = await command.Finish(); File.WriteAllText(Path.Combine(evidence, "background-observations.json"), observations.ToJsonString());
    Check(output.Code == 0, "background public playback failed: " + output.Stderr); Cleanup(TrailingJson(output.Stdout));
    var run = TrailingJson(output.Stdout); var ownedPid = Json.Number(Read(RunDirectory(run), "run.json")["chromePid"]);
    File.WriteAllText(Path.Combine(evidence, "background-attribution.json"), new JsonObject { ["ownedCftPid"] = ownedPid,
        ["ownedCftForegroundSamples"] = observations.Count(sample => Json.Number(sample!["foregroundPid"]) == ownedPid), ["cursorChanged"] = cursorChanged,
        ["foregroundChanged"] = foregroundChanged, ["samples"] = observations.Count }.ToJsonString(Json.Indented));
    if (cursorChanged || foregroundChanged) throw new ObservationInconclusive("cursor or foreground changed during playback; background-observations.json preserves raw observations for attribution against external user activity");
});

if (args.Contains("--windows")) await Test("windows", "public standalone always once opacity position merge and restore", async () =>
{
    foreach (var mode in new[] { "always", "once" })
    {
        var window = new JsonObject { ["foreground"] = true, ["opacity"] = mode == "once" ? 0 : .5,
            ["bounds"] = new JsonObject { ["left"] = 30, ["top"] = 30, ["width"] = 400, ["height"] = 300 } };
        if (mode == "once") window["foreground_mode"] = mode; // Omitted mode exercises default always.
        var path = Write("standalone-" + mode, Scenario("standalone-" + mode, fixture.Base + "/window", null, window, Sleep(3000), new() { ["do"] = "wait_for", ["state"] = "complete" }));
        using var process = Start("standalone-" + mode, "play", path, "--position", "60,70"); var ready = await Ready("standalone-" + mode); await Task.Delay(200);
        Check(NativeWindows.Topmost(ready.Window) && NativeWindows.Alpha(ready.Window) == (mode == "once" ? 0 : 128), "standalone topmost or opacity failed for " + mode);
        var physical = NativeWindows.Measure(ready.Window);
        Check(physical.Rect.Left == 60 && physical.Rect.Top == 70 && physical.Rect.Right - physical.Rect.Left == 400 && physical.Rect.Bottom - physical.Rect.Top == 300, "play position replaced size instead of merging it");
        if (mode == "always")
        {
            Check(NativeWindows.SetWindowPos(ready.Window, -2, 0, 0, 0, 0, 0x13), "could not perturb standalone topmost flag");
            var foreground = NativeWindows.GetForegroundWindow(); var until = DateTime.UtcNow.AddMilliseconds(1500);
            while (!NativeWindows.Topmost(ready.Window) && DateTime.UtcNow < until) await Task.Delay(25);
            Check(NativeWindows.Topmost(ready.Window) && NativeWindows.GetForegroundWindow() == foreground, "standalone watchdog changed focus or failed recovery");
        }
        var output = await process.Finish(); Check(output.Code == 0, "standalone public playback failed: " + output.Stderr); var result = TrailingJson(output.Stdout); Cleanup(result);
        var diagnostic = Read(RunDirectory(result), "foreground.json");
        Check(Json.Text(diagnostic["mode"]) == mode && Json.Number(diagnostic["restoreStatus"], -1) == 0, "standalone foreground restoration failed");
        Check(mode == "once" ? Json.Number(diagnostic["topmostAttempts"]) == 1 : Json.Number(diagnostic["topmostAttempts"]) > 2, "standalone maintenance attempts changed");
    }
});

if (args.Contains("--windows")) await Test("windows", "public foreground modes opacity watchdog reuse and restore", async () =>
{
    var profile = Profile("windows"); var files = new List<string>();
    var modes = new[] { ("once", true, "once", .25), ("once-again", true, "once", 1.0), ("always", true, "always", .5), ("disabled", false, "always", .4) };
    foreach (var (name, foreground, mode, opacity) in modes)
    {
        var scenario = Scenario("window-" + name, fixture.Base + "/window", profile, new()
        { ["foreground"] = foreground, ["foreground_mode"] = mode, ["opacity"] = opacity, ["bounds"] = new JsonObject { ["left"] = 30, ["top"] = 30, ["width"] = 400, ["height"] = 300 } }, Sleep(4000), new() { ["do"] = "wait_for", ["state"] = "complete" }, Sleep(1000));
        scenario["browser"]!["block_urls"] = new JsonArray((JsonNode?)JsonValue.Create("/never-block")); files.Add(Write("window-" + name, scenario));
    }
    var plan = Write("window-plan", Plan(Serial(files.Select(Leaf).ToArray()))); var original = NativeWindows.GetForegroundWindow();
    using var process = Start("window-plan", "run", plan);
    foreach (var (name, enabled, mode, opacity) in modes)
    {
        var ready = await Ready("window-" + name); await Task.Delay(200);
        Check(NativeWindows.Topmost(ready.Window) == enabled && NativeWindows.Alpha(ready.Window) == (byte)Math.Floor(opacity * 255 + .5), "foreground/alpha mismatch for " + name);
        if (enabled && mode == "once")
        {
            await Task.Run(() =>
            {
                var overlay = NativeWindows.Overlay(); Check(overlay != 0, "test overlay could not be created");
                var trace = new JsonArray();
                try
                {
                    Check(NativeWindows.SetWindowPos(overlay, -1, 0, 0, 0, 0, 0x13), "test overlay positioning failed");
                    var until = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < until)
                    {
                        var foreground = NativeWindows.GetForegroundWindow(); var cursor = NativeWindows.Cursor();
                        var topmost = NativeWindows.Topmost(ready.Window); var above = NativeWindows.Above(overlay, ready.Window);
                        trace.Add(new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["cfthwnd"] = "0x" + ready.Window.ToString("x"), ["overlay"] = "0x" + overlay.ToString("x"),
                            ["foreground"] = "0x" + foreground.ToString("x"), ["foregroundPid"] = NativeWindows.Owner(foreground), ["cursorX"] = cursor.X, ["cursorY"] = cursor.Y,
                            ["topmost"] = topmost, ["overlayAbove"] = above });
                        Check(topmost && above, name + ": once mode raised above an owned test overlay"); Thread.Sleep(50);
                    }
                }
                finally
                {
                    File.WriteAllText(Path.Combine(evidence, "overlay-" + name + ".json"), trace.ToJsonString(Json.Indented));
                    NativeWindows.DestroyWindow(overlay);
                }
            });
        }
        else if (enabled)
        {
            Check(NativeWindows.SetWindowPos(ready.Window, -2, 0, 0, 0, 0, 0x13), "could not perturb owned CfT topmost flag"); var foreground = NativeWindows.GetForegroundWindow();
            var until = DateTime.UtcNow.AddMilliseconds(1500); while (!NativeWindows.Topmost(ready.Window) && DateTime.UtcNow < until) await Task.Delay(25);
            Check(NativeWindows.Topmost(ready.Window) && NativeWindows.GetForegroundWindow() == foreground, "always watchdog failed to restore topmost without focus change");
        }
        else { await Task.Delay(500); Check(!NativeWindows.Topmost(ready.Window), "disabled scenario retained previous watchdog"); }
    }
    var command = await process.Finish(); Check(command.Code == 0, "foreground plan failed: " + command.Stderr); var results = TrailingJson(command.Stdout).AsArray(); Cleanup(results.Last()!);
    var previousAttempts = 0d;
    for (var index = 0; index < modes.Length; index++)
    {
        // The baseline reads its diagnostic after playback; periodic maintenance
        // updates in-memory counters and the final diagnostic captures every tick.
        var diagnostic = Read(RunDirectory(results[index]!), "foreground.json"); var attempts = Json.Number(diagnostic["topmostAttempts"]);
        if (modes[index].Item2 && modes[index].Item3 == "once") Check(attempts - previousAttempts == 1, "once mode requested topmost more than once");
        if (modes[index].Item2 && modes[index].Item3 == "always") Check(attempts - previousAttempts > 2, "always watchdog did not run while sleeping");
        if (!modes[index].Item2) Check(attempts == previousAttempts, "disabled mode kept requesting topmost");
        previousAttempts = attempts;
    }
    var final = Read(RunDirectory(results.Last()!), "foreground.json"); Check(Json.Number(final["restoreStatus"], -1) == 0, "foreground restoration failed: " + final);
    File.WriteAllText(Path.Combine(evidence, "foreground-observation.json"), new JsonObject { ["before"] = "0x" + original.ToString("x"), ["after"] = "0x" + NativeWindows.GetForegroundWindow().ToString("x"), ["matches"] = original == NativeWindows.GetForegroundWindow() }.ToJsonString());
});

foreach (var pid in ordinaryChrome.Keys)
    try { using var process = Process.GetProcessById(pid); Check(!process.HasExited, "a pre-existing Chrome process exited during integration: " + pid); }
    catch (ArgumentException) { Console.Error.WriteLine("OBSERVATION pre-existing Chrome changed externally; PID " + pid + " is no longer available"); }
File.WriteAllText(Path.Combine(evidence, "environment.json"), new JsonObject { ["exe"] = exe, ["chrome"] = chrome, ["dll"] = dll, ["fixture"] = fixture.Base, ["profiles"] = new JsonArray(ownProfiles.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()), ["ordinaryChromeCountBefore"] = ordinaryChrome.Count }.ToJsonString(Json.Indented));
File.WriteAllText(Path.Combine(evidence, "ordinary-chrome-before.json"), new JsonArray(ordinaryChrome.Select(item => (JsonNode)new JsonObject { ["pid"] = item.Key, ["image"] = item.Value }).ToArray()).ToJsonString(Json.Indented));
File.WriteAllText(Path.Combine(evidence, "fixture-exchanges.json"), new JsonArray(fixture.Exchanges.Select(item => (JsonNode)new JsonObject { ["path"] = item.Path, ["phase"] = item.Phase }).ToArray()).ToJsonString(Json.Indented));
Console.WriteLine($"integration: {passed} passed, {failed} failed, {inconclusive} inconclusive; evidence: {evidence}");
return failed == 0 && inconclusive == 0 ? 0 : 1;

internal sealed class ObservationInconclusive(string message) : Exception(message);
internal sealed record CommandResult(int Code, string Stdout, string Stderr);
internal sealed class RunningCommand : IDisposable
{
    public Process Process { get; }
    private readonly Task<string> stdout, stderr;
    private readonly string directory, label;
    public RunningCommand(string exe, string[] arguments, string directory, string label, Dictionary<string, string>? environment = null)
    {
        this.directory = directory; this.label = label;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null) foreach (var (key, value) in environment) start.Environment[key] = value;
        Process = Process.Start(start) ?? throw new Exception("public crer.exe could not start");
        stdout = Process.StandardOutput.ReadToEndAsync(); stderr = Process.StandardError.ReadToEndAsync();
    }
    public async Task<CommandResult> Finish()
    {
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); var output = await stdout; var errors = await stderr;
        File.WriteAllText(Path.Combine(directory, label + ".stdout.log"), output); File.WriteAllText(Path.Combine(directory, label + ".stderr.log"), errors);
        return new(Process.ExitCode, output, errors);
    }
    public void Dispose()
    {
        if (!Process.HasExited) try { Process.Kill(true); Process.WaitForExit(5000); } catch (InvalidOperationException) { }
        if (stdout.IsCompletedSuccessfully) File.WriteAllText(Path.Combine(directory, label + ".stdout.log"), stdout.Result);
        if (stderr.IsCompletedSuccessfully) File.WriteAllText(Path.Combine(directory, label + ".stderr.log"), stderr.Result);
        Process.Dispose();
    }
}
