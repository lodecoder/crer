using System.Text.Json.Nodes;
using Crer;
using Context = Crer.ExecutionContext;

var passed = 0; var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); failed++; }
}
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
void Equal(JsonNode? value, JsonNode? expected) => Check(JsonNode.DeepEquals(value, expected), $"expected {expected}, got {value}");
await Test("nested step indexes and one-child cache scope match baseline executor", async () =>
{
    var context = new Context(); var visited = new List<(string, string?)>();
    var executor = new StepExecutor<string>(context, async (frame, run) =>
    {
        visited.Add((frame.Index, frame.CachedTemplate));
        if (frame.Step["do"]!.GetValue<string>() == "parent")
            await run(new JsonArray(new JsonObject { ["do"] = "child" }, new JsonObject { ["do"] = "sibling" }), frame.Index, "cached");
    });
    await executor.ExecuteAsync(new JsonArray(new JsonObject { ["do"] = "parent" }, new JsonObject { ["do"] = "last" }));
    Check(visited.SequenceEqual(new[] { ("0", (string?)null), ("0.0", "cached"), ("0.1", null), ("1", null) }), "nested indexing or cache differs");
});
await Test("stop state prevents nested and root siblings", async () =>
{
    var context = new Context(); var visited = new List<string>();
    var executor = new StepExecutor<string>(context, async (frame, run) =>
    {
        visited.Add(frame.Index);
        if (frame.Index == "0") await run(new JsonArray(new JsonObject(), new JsonObject()));
        else context.Stop();
    });
    await executor.ExecuteAsync(new JsonArray(new JsonObject(), new JsonObject()));
    Check(visited.SequenceEqual(new[] { "0", "0.0" }), "stopped traversal continued");
});
await Test("custom function parent indexes remain stable", async () =>
{
    var visited = new List<string>(); var context = new Context();
    var executor = new StepExecutor<string>(context, (frame, _) => { visited.Add(frame.Index); return Task.CompletedTask; });
    await executor.ExecuteAsync(new JsonArray(new JsonObject(), new JsonObject()), "3.search");
    Check(visited.SequenceEqual(new[] { "3.search.0", "3.search.1" }), "function indexes changed");
});
await Test("recursive and indirect recursive functions are rejected with a call path", () =>
{
    var context = new Context(); var first = context.EnterFunction("one"); var second = context.EnterFunction("two");
    try { context.EnterFunction("one"); throw new Exception("recursion was accepted"); }
    catch (InvalidOperationException error) { Check(error.Message == "recursive function call: one -> two -> one", "call path missing"); }
    second(); first(); var after = context.EnterFunction("one"); after(); return Task.CompletedTask;
});
await Test("unbalanced call stack is rejected", () =>
{
    var context = new Context(); var one = context.EnterFunction("one"); context.EnterFunction("two");
    try { one(); throw new Exception("unbalanced stack accepted"); }
    catch (InvalidOperationException error) { Check(error.Message.Contains("unbalanced", StringComparison.Ordinal), "stack diagnostic missing"); }
    return Task.CompletedTask;
});
await Test("whole argument references preserve numeric type and embedded references stringify", () =>
{
    var source = new JsonObject { ["x"] = "${count}", ["message"] = "count=${count}; ${missing}", ["nested"] = new JsonArray("${name}", new JsonObject { ["x"] = "${count}" }) };
    var expanded = StepArguments.Expand(source, new() { ["count"] = 3, ["name"] = "日本語" });
    Equal(expanded, new JsonObject { ["x"] = 3, ["message"] = "count=3; ${missing}", ["nested"] = new JsonArray("日本語", new JsonObject { ["x"] = 3 }) });
    Check(source["x"]!.GetValue<string>() == "${count}", "expansion mutated function definition"); return Task.CompletedTask;
});
await Test("argument expansion is one pass and accepts hyphenated names", () =>
{
    Equal(StepArguments.Expand(JsonValue.Create("${a}-${some-name}"), new() { ["a"] = "${other}", ["other"] = 1, ["some-name"] = 2 }), JsonValue.Create("${other}-2"));
    Equal(StepArguments.Expand(JsonValue.Create("${0bad}"), new() { ["0bad"] = 1 }), JsonValue.Create("${0bad}"));
    return Task.CompletedTask;
});
await Test("embedded numeric arguments use JavaScript exponent and zero formatting", () =>
{
    foreach (var (value, expected) in new[] { (1e20, "100000000000000000000"), (1e21, "1e+21"), (1e-6, "0.000001"), (1e-7, "1e-7"), (-0d, "0"), (-2.5e18, "-2500000000000000000") })
        Check(StepArguments.String(JsonValue.Create(value)) == expected, $"numeric string differs for {value:R}");
    return Task.CompletedTask;
});
await Test("IANA weekday calculation crosses UTC and local date boundaries", () =>
{
    var instant = DateTimeOffset.Parse("2026-10-15T01:00:00Z");
    Check(StepArguments.Weekday(instant, "Asia/Tokyo") == "thu", "Tokyo weekday");
    Check(StepArguments.Weekday(instant, "America/Los_Angeles") == "wed", "Los Angeles weekday");
    Check(StepArguments.Weekday(instant, "UTC") == "thu", "UTC weekday");
    Check(StepArguments.Weekday(DateTimeOffset.Parse("2026-10-18T23:30:00Z"), "Pacific/Kiritimati") == "mon", "international date line weekday");
    return Task.CompletedTask;
});
await Test("IANA DST gap uses instant conversion", () =>
{
    Check(StepArguments.Weekday(DateTimeOffset.Parse("2026-03-08T10:00:00Z"), "America/Los_Angeles") == "sun", "DST transition weekday");
    return Task.CompletedTask;
});
await Test("traversal propagates cancellation instead of executing siblings", async () =>
{
    var calls = 0; var context = new Context();
    var executor = new StepExecutor<string>(context, (_, _) => { calls++; throw new OperationCanceledException(); });
    try { await executor.ExecuteAsync(new JsonArray(new JsonObject(), new JsonObject())); throw new Exception("cancellation swallowed"); }
    catch (OperationCanceledException) { Check(calls == 1, "sibling executed after cancellation"); }
});
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
