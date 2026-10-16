using System.Globalization;
using System.Text.Json.Nodes;
using Crer;

var passed = 0;
var failed = 0;
var maximumCoordinateDifference = 0d;
var comparedJitterCoordinates = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(10)); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
void Equal(JsonNode? actual, JsonNode? expected, string message) => Check(JsonNode.DeepEquals(actual, expected), $"{message}: expected {expected}, got {actual}");
async Task<Exception> Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) { Check(error.Message.Contains(message, StringComparison.Ordinal), $"missing '{message}': {error.Message}"); return error; }
    throw new InvalidOperationException($"expected failure '{message}'");
}
Task Execute(Adapter fixture, JsonObject step, ActionPoint? at = null, JitterConfig? jitter = null)
    => Actions.ExecuteAtomicAsync(fixture.Value, step, at, jitter, new DeterministicRandom(1), 1000);

var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-prng-vectors.json")))!.AsObject();
await Test("fixed baseline xoshiro vectors include zero, high-bit and uint64 max seeds", () =>
{
    foreach (var vector in vectors["numbers"]!.AsArray())
    {
        var random = new DeterministicRandom(ulong.Parse(vector!["seed"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        foreach (var value in vector["values"]!.AsArray()) Check(random.Next() == value!.GetValue<double>(), $"random vector differs for seed {vector["seed"]}");
    }
    return Task.CompletedTask;
});
await Test("uniform coordinates and consumption match fixed legacy vectors", () =>
{
    foreach (var vector in vectors["jitters"]!.AsArray())
    {
        // 旧 normal の偏りと半径超過は互換対象にせず、分布の性質を下の回帰テストで保証する。
        if (vector!["config"]?["distribution"]?.GetValue<string>() == "normal") continue;
        var random = new DeterministicRandom(ulong.Parse(vector!["seed"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        foreach (var expected in vector["points"]!.AsArray())
        {
            comparedJitterCoordinates++;
            var actual = Prng.Jitter(ActionPoint.From(vector["point"]!), JitterConfig.From(vector["config"]), random, ActionPoint.From(vector["size"]!));
            if (expected == null) Check(actual == null, "expected jitter bounds failure");
            else
            {
                Check(actual != null, "unexpected jitter bounds failure");
                var difference = Math.Max(Math.Abs(actual!.Value.X - expected["x"]!.GetValue<double>()), Math.Abs(actual.Value.Y - expected["y"]!.GetValue<double>()));
                maximumCoordinateDifference = Math.Max(maximumCoordinateDifference, difference);
                Check(difference <= 1e-12, $"coordinate difference {difference:R}");
            }
        }
        Check(random.Next() == vector["next"]!.GetValue<double>(), "jitter consumed a different count of random numbers");
    }
    return Task.CompletedTask;
});
await Test("normal jitter は最大半径を守り、左右上下に対称な分布を生成する", () =>
{
    var origin = new ActionPoint(500, 500);
    var viewport = new ActionPoint(1000, 1000);
    var config = new JitterConfig(true, "normal", 30, 0, "fail");
    var quadrants = new int[4];
    double sumX = 0, sumY = 0, squareX = 0, squareY = 0;
    const int samples = 4096;
    for (ulong seed = 0; seed < samples; seed++)
    {
        var first = new DeterministicRandom(seed); var replay = new DeterministicRandom(seed);
        var point = Prng.Jitter(origin, config, first, viewport);
        Check(point != null && point == Prng.Jitter(origin, config, replay, viewport), "同じ seed の再生結果が一致しない");
        Check(first.Next() == replay.Next(), "同じ seed の乱数消費が一致しない");
        var x = point!.Value.X - origin.X; var y = point.Value.Y - origin.Y;
        Check(x * x + y * y <= 30 * 30 + 1e-10, $"seed {seed}: 最大半径を超過した");
        quadrants[(x < 0 ? 1 : 0) + (y < 0 ? 2 : 0)]++;
        sumX += x; sumY += y; squareX += x * x; squareY += y * y;
    }
    Check(quadrants.All(count => count > 800), "いずれかの方向に分布が偏っている");
    Check(Math.Abs(sumX / samples) < .75 && Math.Abs(sumY / samples) < .75, "分布の中心が原点からずれている");
    Check(squareX / samples is > 80 and < 110 && squareY / samples is > 80 and < 110, "標準偏差 radius/3 の切断正規分布から外れている");
    return Task.CompletedTask;
});
await Test("disabled and none jitter preserve outside points and consume no random values", () =>
{
    foreach (var config in new JitterConfig?[] { null, new(false, "uniform", 2, 10, "fail"), new(true, "none", 2, 10, "fail") })
    {
        var random = new DeterministicRandom(42);
        var point = new ActionPoint(-50, 1000);
        Check(Prng.Jitter(point, config, random, new(100, 100)) == point, "disabled jitter imposed bounds");
        Check(random.Next() == new DeterministicRandom(42).Next(), "disabled jitter consumed random values");
    }
    return Task.CompletedTask;
});
await Test("zero-radius edge boundaries are inclusive and retries consume exactly 32 values", () =>
{
    var config = new JitterConfig(true, "uniform", 0, 10, "fail");
    Check(Prng.Jitter(new(10, 90), config, new(42), new(100, 100)) == new ActionPoint(10, 90), "inclusive edge rejected");
    var random = new DeterministicRandom(42); var reference = new DeterministicRandom(42);
    Check(Prng.Jitter(new(9.999999, 90), config, random, new(100, 100)) == null, "outside edge accepted");
    for (var i = 0; i < 32; i++) reference.Next();
    Check(random.Next() == reference.Next(), "16 jitter retries did not consume two values each");
    return Task.CompletedTask;
});
await Test("cryptographic seed is a replayable decimal uint64", () =>
{
    for (var i = 0; i < 32; i++) { var seed = Prng.RandomSeed(); Check(seed.All(char.IsAsciiDigit) && ulong.TryParse(seed, out _), "invalid seed"); }
    return Task.CompletedTask;
});
await Test("step jitter configuration completely replaces scenario settings", () =>
{
    var overridden = JitterConfig.From(new JsonObject { ["enabled"] = false, ["distribution"] = "none", ["radius_px"] = 0, ["min_distance_from_edge_px"] = 0, ["out_of_bounds"] = "disable-for-step" });
    Check(overridden == new JitterConfig(false, "none", 0, 0, "disable-for-step"), "configuration merged inherited values");
    return Task.CompletedTask;
});
await Test("navigation routes through CDP adapter", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "navigate", ["url"] = "https://example.com" });
    Check(fixture.Events.Single().Method == "Page.navigate", "wrong method");
    Equal(fixture.Events[0].Parameters, new JsonObject { ["url"] = "https://example.com" }, "navigation parameters");
});
await Test("navigate は CDP の通常応答に含まれる遷移エラーを失敗として返す", async () =>
{
    foreach (var error in new[] { "net::ERR_CONNECTION_REFUSED", "net::ERR_NAME_NOT_RESOLVED", "net::ERR_BLOCKED_BY_CLIENT" })
    {
        var fixture = new Adapter(response: (_, _) => new() { ["frameId"] = "page", ["errorText"] = error });
        await Reject(() => Execute(fixture, new() { ["do"] = "navigate", ["url"] = "https://example.test" }), error);
        Check(fixture.Events.Count == 1, "失敗後に余分な CDP 操作が行われた");
    }
    var success = new Adapter(response: (_, _) => new() { ["frameId"] = "page", ["errorText"] = "" });
    await Execute(success, new() { ["do"] = "navigate", ["url"] = "https://example.test" });
});
await Test("click mouse events are paired without checking disabled jitter bounds", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "click" }, new(-10, 900));
    Check(fixture.Events.Count == 3 && fixture.Events.All(value => value.Method == "Input.dispatchMouseEvent"), "wrong input events");
    Equal(fixture.Events[1].Parameters, new JsonObject { ["type"] = "mousePressed", ["x"] = -10d, ["y"] = 900d, ["button"] = "left", ["buttons"] = 1, ["clickCount"] = 1 }, "click down");
    Check(fixture.Events[^1].Type == "mouseReleased", "missing mouse release");
});
await Test("double click increments clickCount and holds both clicks", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "double_click", ["hold_ms"] = "12" }, new(10, 20));
    var downs = fixture.Events.Where(value => value.Type == "mousePressed").ToArray();
    Check(downs.Length == 2 && downs[0].Parameters["clickCount"]!.GetValue<int>() == 1 && downs[1].Parameters["clickCount"]!.GetValue<int>() == 2, "double click counts");
    Check(fixture.Sleeps.SequenceEqual(new double[] { 12, 12 }), "hold consumption differs");
});
await Test("click count belongs to orchestrator and atomic click always executes once", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "click", ["count"] = 7, ["delay_ms"] = 10 }, new(10, 20));
    Check(fixture.Events.Count == 3 && fixture.Sleeps.Count == 0, "atomic handler repeated count or consumed orchestrator delays");
});
await Test("invalid hold duration fails before sending input", async () =>
{
    foreach (var value in new JsonNode[] { JsonValue.Create(-1)!, JsonValue.Create("bad")! })
    {
        var fixture = new Adapter(); await Reject(() => Execute(fixture, new() { ["do"] = "click", ["hold_ms"] = value.DeepClone() }, new(10, 20)), "click hold_ms");
        Check(fixture.Events.Count == 0, "invalid hold emitted input");
    }
});
await Test("mouse move and scroll retain effective coordinates and wheel deltas", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "mouse_move" }, new(10, 20));
    await Execute(fixture, new() { ["do"] = "scroll", ["delta"] = new JsonObject { ["y"] = -17 } }, new(30, 40));
    Equal(fixture.Events[^1].Parameters, new JsonObject { ["type"] = "mouseWheel", ["x"] = 30d, ["y"] = 40d, ["deltaX"] = 0d, ["deltaY"] = -17d }, "wheel input");
});
await Test("drag uses ten interpolated moves with pressed left button", async () =>
{
    var fixture = new Adapter();
    await Execute(fixture, new() { ["do"] = "drag", ["from"] = new JsonObject { ["x"] = 10, ["y"] = 20 }, ["to"] = new JsonObject { ["x"] = 30, ["y"] = 60 } });
    Check(fixture.Events.Count == 13, "drag interpolation count");
    var move = fixture.Events[2].Parameters;
    Check(ActionNumber.Read(move["x"]) == 12 && ActionNumber.Read(move["y"]) == 24 && move["buttons"]!.GetValue<int>() == 1, "first drag interpolation");
    Check(fixture.Effective.Single() == (new ActionPoint(10, 20), new ActionPoint(30, 60)), "effective drag coordinates missing");
});
await Test("drag preserves a single jitter offset for both endpoints", async () =>
{
    var fixture = new Adapter();
    await Execute(fixture, new() { ["do"] = "drag", ["from"] = new JsonObject { ["x"] = 100, ["y"] = 200 }, ["to"] = new JsonObject { ["x"] = 300, ["y"] = 400 } }, jitter: new(true, "uniform", 3, 0, "fail"));
    var pair = fixture.Effective.Single();
    Check(Math.Abs(pair.Item2!.Value.X - pair.Item1.X - 200) < 1e-12 && Math.Abs(pair.Item2.Value.Y - pair.Item1.Y - 200) < 1e-12, "drag endpoints used different jitter offsets");
});
await Test("out of bounds drag destination fails before pressing input", async () =>
{
    var fixture = new Adapter(); var error = await Reject(() => Execute(fixture, new() { ["do"] = "drag", ["from"] = new JsonObject { ["x"] = 10, ["y"] = 20 }, ["to"] = new JsonObject { ["x"] = 801, ["y"] = 60 } }), "bounds");
    Check(error is JitterBoundsException && fixture.Events.Count == 0, "drag bounds not classified");
});
await Test("drag rejects missing coordinates", async () =>
{
    await Reject(() => Execute(new(), new() { ["do"] = "drag", ["from"] = new JsonObject { ["x"] = 10 }, ["to"] = new JsonObject { ["x"] = 20, ["y"] = 30 } }), "requires from and to");
});
await Test("drag failure releases pressed mouse", async () =>
{
    var moved = 0; var fixture = new Adapter((_, value) => value["type"]?.GetValue<string>() == "mouseMoved" && ++moved == 2 ? new IOException("drag move failed") : null);
    await Reject(() => Execute(fixture, new() { ["do"] = "drag", ["from"] = new JsonObject { ["x"] = 10, ["y"] = 10 }, ["to"] = new JsonObject { ["x"] = 20, ["y"] = 20 } }), "drag move failed");
    Check(fixture.Events[^1].Type == "mouseReleased", "mouse remained pressed");
});
await Test("text uses Input.insertText", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "text", ["value"] = "日本語 A" });
    Check(fixture.Events.Single().Method == "Input.insertText", "text did not use CDP");
    Equal(fixture.Events[0].Parameters, new JsonObject { ["text"] = "日本語 A" }, "text parameters");
});
await Test("key code VK text and modifiers match legacy keyboard dispatch", async () =>
{
    foreach (var (name, key, code, vk, text) in new[] { ("A", "A", "KeyA", 65, (string?)null), ("a", "a", "KeyA", 65, null), ("7", "7", "Digit7", 55, null), ("Enter", "Enter", "Enter", 13, "\r"), ("Space", " ", "Space", 32, " "), ("Tab", "Tab", "Tab", 9, null), ("ArrowLeft", "ArrowLeft", "ArrowLeft", 37, null), ("F12", "F12", "F12", 0, null) })
    {
        var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "key", ["key"] = name });
        var expected = new JsonObject { ["type"] = text == null ? "rawKeyDown" : "keyDown", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk, ["modifiers"] = 0 };
        if (text != null) { expected["text"] = text; expected["unmodifiedText"] = text; }
        Equal(fixture.Events[0].Parameters, expected, "keyboard down");
        Check(fixture.Events[1].Type == "keyUp", "keyboard release");
    }
});
await Test("key chord presses modifiers in order and releases in reverse with exact masks", async () =>
{
    var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "key_chord", ["keys"] = new JsonArray("Control", "Shift", "Enter") });
    Check(fixture.Events.Select(value => value.Type).SequenceEqual(new[] { "rawKeyDown", "rawKeyDown", "rawKeyDown", "keyUp", "keyUp", "keyUp" }), "chord event types");
    Check(fixture.Events.Select(value => value.Parameters["modifiers"]!.GetValue<int>()).SequenceEqual(new[] { 0, 2, 10, 10, 2, 0 }), "chord masks");
    Check(fixture.Events.All(value => value.Parameters["text"] == null), "modified Enter inserted text");
});
await Test("invalid chords fail before input", async () =>
{
    foreach (var keys in new[] { new JsonArray("A"), new JsonArray("Other", "A"), new JsonArray(1, "A") })
    {
        var fixture = new Adapter(); await Reject(() => Execute(fixture, new() { ["do"] = "key_chord", ["keys"] = keys }), "key_chord");
        Check(fixture.Events.Count == 0, "invalid chord emitted input");
    }
});
await Test("keyUp failure is retried and preserves the original error", async () =>
{
    var failedUp = false; var fixture = new Adapter((_, value) => value["type"]?.GetValue<string>() == "keyUp" && !failedUp && (failedUp = true) ? new IOException("key up failed") : null);
    await Reject(() => Execute(fixture, new() { ["do"] = "key", ["key"] = "A" }), "key up failed");
    Check(fixture.Events.TakeLast(2).All(value => value.Type == "keyUp"), "key release not retried");
});
await Test("ambiguous chord down releases uncertain final key then prior modifiers", async () =>
{
    var fixture = new Adapter((_, value) => value["type"]?.GetValue<string>() == "rawKeyDown" && value["code"]?.GetValue<string>() == "KeyA" ? new IOException("response lost") : null);
    await Reject(() => Execute(fixture, new() { ["do"] = "key_chord", ["keys"] = new JsonArray("Control", "A") }), "response lost");
    Check(fixture.Events.TakeLast(2).Select(value => value.Parameters["code"]!.GetValue<string>()).SequenceEqual(new[] { "KeyA", "ControlLeft" }), "uncertain keys not released in reverse");
});
await Test("ambiguous mouse down gets a conservative release", async () =>
{
    var fixture = new Adapter((_, value) => value["type"]?.GetValue<string>() == "mousePressed" ? new IOException("response lost") : null);
    await Reject(() => Execute(fixture, new() { ["do"] = "click" }, new(10, 20)), "response lost");
    Check(fixture.Events[^1].Type == "mouseReleased", "uncertain mouse press not released");
});
await Test("cancellation during hold uses an uncancelled cleanup adapter", async () =>
{
    var cleanup = new List<JsonObject>();
    InputCall call = (_, _) => Task.FromResult(new JsonObject());
    InputCall release = (_, parameters) => { cleanup.Add(parameters.DeepClone().AsObject()); return Task.FromResult(new JsonObject()); };
    var adapter = new AtomicActionAdapter { Call = call, CleanupCall = release, Viewport = new(100, 100), Sleep = _ => throw new OperationCanceledException(), WaitFor = (_, _) => Task.CompletedTask, AssertState = _ => Task.CompletedTask, Capture = _ => Task.CompletedTask, Log = _ => { } };
    var error = await Reject(() => Actions.ExecuteAtomicAsync(adapter, new() { ["do"] = "click", ["hold_ms"] = 100 }, new(10, 20), null, new(1), 1000), "canceled");
    Check(error is OperationCanceledException && cleanup.Count == 1 && cleanup[0]["type"]!.GetValue<string>() == "mouseReleased", "cancellation leaked input");
});
await Test("cleanup failure is attached without replacing original action error", async () =>
{
    var guard = new InputGuard((_, _) => Task.FromResult(new JsonObject()), (_, _) => throw new IOException("cleanup failure"));
    var error = await Reject(() => guard.RunAsync(async input => { await input.MouseDownAsync(10, 20); throw new IOException("primary failure"); }), "primary failure");
    Check(InputGuard.CleanupErrors(error)?.Single() == "cleanup failure", "cleanup diagnostic lost");
});
await Test("cleanup failure after a successful guard body retains terminal release diagnostics", async () =>
{
    var guard = new InputGuard((_, _) => Task.FromResult(new JsonObject()), (_, _) => throw new IOException("release rejected"));
    var error = await Reject(() => guard.RunAsync(input => input.MouseDownAsync(10, 20)), "input cleanup failed");
    Check(InputGuard.CleanupErrors(error)?.Single() == "release rejected", "terminal cleanup failure was not classified");
});
await Test("guard releases successful chord keys in reverse order on failure", async () =>
{
    var events = new List<JsonObject>(); var guard = new InputGuard((_, parameters) => { events.Add(parameters); return Task.FromResult(new JsonObject()); });
    await Reject(() => guard.RunAsync(async input =>
    {
        await input.KeyDownAsync(new() { ["type"] = "rawKeyDown", ["key"] = "Control", ["code"] = "ControlLeft", ["windowsVirtualKeyCode"] = 17, ["modifiers"] = 0 });
        await input.KeyDownAsync(new() { ["type"] = "rawKeyDown", ["key"] = "A", ["code"] = "KeyA", ["windowsVirtualKeyCode"] = 65, ["modifiers"] = 2 });
        throw new IOException("chord failed");
    }), "chord failed");
    Check(events.TakeLast(2).Select(value => value["code"]!.GetValue<string>()).SequenceEqual(new[] { "KeyA", "ControlLeft" }), "reverse release order");
});
await Test("wait assertion capture log and sleep use provided adapters", async () =>
{
    var fixture = new Adapter();
    await Execute(fixture, new() { ["do"] = "wait_for" }); await Execute(fixture, new() { ["do"] = "assert" });
    await Execute(fixture, new() { ["do"] = "screenshot" }); await Execute(fixture, new() { ["do"] = "log", ["message"] = "hello" });
    await Execute(fixture, new() { ["do"] = "sleep", ["ms"] = "0x10" });
    Check(fixture.WaitTimeout == 1000 && fixture.Assertions == 1 && fixture.Captures.Single() == "screenshot" && fixture.Logs.Single() == "hello" && fixture.Sleeps.Single() == 16, "adapter routing differs");
});
await Test("sleep rejects negative and nonnumeric expanded arguments", async () =>
{
    foreach (var value in new[] { "-1", "not-a-number", "Infinity" }) await Reject(() => Execute(new(), new() { ["do"] = "sleep", ["ms"] = value }), "sleep ms");
});
await Test("explicit failure is distinct and cannot be treated as an ordinary leaf failure", async () =>
{
    var error = await Reject(() => Execute(new(), new() { ["do"] = "fail", ["message"] = "unconditional stop" }), "unconditional stop");
    Check(error is ExplicitFailureException, "explicit failure classification lost");
});
await Test("control steps are rejected at atomic boundary", async () =>
{
    await Reject(() => Execute(new(), new() { ["do"] = "repeat", ["count"] = 1, ["steps"] = new JsonArray() }), "unsupported atomic step");
});
await Test("actual fixed-baseline action traces match C# dispatch", async () =>
{
    var traces = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-action-vectors.json")))!;
    foreach (var trace in traces["values"]!.AsArray())
    {
        var fixture = new Adapter();
        await Execute(fixture, trace!["step"]!.DeepClone().AsObject(), new(10, 20));
        Equal(new JsonArray(fixture.Events.Select(value => (JsonNode)new JsonObject { ["method"] = value.Method, ["params"] = value.Parameters.DeepClone() }).ToArray()), trace["calls"], $"legacy events for {trace["step"]}");
        Check(fixture.Sleeps.SequenceEqual(trace["sleeps"]!.AsArray().Select(value => value!.GetValue<double>())), "legacy sleep sequence differs");
    }
});
await Test("single-character VK uppercase conversion matches legacy Unicode mapping", async () =>
{
    var traces = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-key-vectors.json")))!;
    foreach (var trace in traces["mappings"]!.AsArray())
    {
        var character = (char)trace!["code"]!.GetValue<int>();
        var fixture = new Adapter(); await Execute(fixture, new() { ["do"] = "key", ["key"] = character.ToString() });
        Check(fixture.Events[0].Parameters["windowsVirtualKeyCode"]!.GetValue<int>() == trace["upper"]!.GetValue<int>(), $"Unicode VK differs for U+{(int)character:X4}");
    }
});
Console.WriteLine($"PRNG exact values: 60; jitter coordinates: {comparedJitterCoordinates}; maximum coordinate difference: {maximumCoordinateDifference:R}");
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed record Event(string Method, JsonObject Parameters)
{
    public string Type => Parameters["type"]?.GetValue<string>() ?? "";
}
sealed class Adapter
{
    public AtomicActionAdapter Value { get; }
    public List<Event> Events { get; } = [];
    public List<double> Sleeps { get; } = [];
    public List<string> Captures { get; } = [];
    public List<string> Logs { get; } = [];
    public List<(ActionPoint, ActionPoint?)> Effective { get; } = [];
    public int Assertions { get; private set; }
    public double WaitTimeout { get; private set; }

    public Adapter(Func<string, JsonObject, Exception?>? failure = null, Func<string, JsonObject, JsonObject>? response = null)
    {
        Value = new()
        {
            Viewport = new(800, 600),
            Call = (method, parameters) =>
            {
                Events.Add(new(method, parameters.DeepClone().AsObject()));
                var error = failure?.Invoke(method, parameters);
                return error == null ? Task.FromResult(response?.Invoke(method, parameters) ?? new JsonObject()) : Task.FromException<JsonObject>(error);
            },
            Sleep = ms => { Sleeps.Add(ms); return Task.CompletedTask; },
            WaitFor = (_, timeout) => { WaitTimeout = timeout; return Task.CompletedTask; },
            AssertState = _ => { Assertions++; return Task.CompletedTask; },
            Capture = name => { Captures.Add(name); return Task.CompletedTask; }, Log = Logs.Add,
            EffectivePoint = (from, to) => Effective.Add((from, to))
        };
    }
}
