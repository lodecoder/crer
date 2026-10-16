using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index++)
{
    if (args[index] is not ("--exe" or "--chrome" or "--root" or "--only") || index + 1 >= args.Length) throw new ArgumentException("Usage: --exe <published-crer.exe> --chrome <CfT.exe> [--root <new-evidence-directory>] [--only <test-name-substring>]");
    options.Add(args[index], args[++index]);
}
var executable = Path.GetFullPath(options.GetValueOrDefault("--exe", "dist/win-x64/crer.exe"));
var chrome = Path.GetFullPath(options.GetValueOrDefault("--chrome", Environment.GetEnvironmentVariable("CRER_CHROME") ?? ""));
if (!File.Exists(executable) || !File.Exists(chrome)) throw new ArgumentException("Specify the published crer executable and Chrome for Testing.");
var root = Path.GetFullPath(options.GetValueOrDefault("--root", Path.Combine(".crer", "control-integration", Guid.NewGuid().ToString("N"))));
if (Directory.Exists(root)) throw new ArgumentException("Evidence directory must be new, to preserve earlier acceptance results.");
Directory.CreateDirectory(root);
var pattern = Fixture.Png(); var missing = Fixture.Png(true);
var patternPath = Path.Combine(root, "pattern.png"); var missingPath = Path.Combine(root, "missing.png");
await File.WriteAllBytesAsync(patternPath, pattern); await File.WriteAllBytesAsync(missingPath, missing);
await using var fixture = new Fixture(pattern);
using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
    if (!(await http.GetStringAsync(fixture.Url)).Contains("Counter", StringComparison.Ordinal)) throw new IOException("Owned fixture server is unreachable.");
var passed = 0; var failed = 0;
var summary = new JsonArray();
JsonObject Point(double x, double y) => new() { ["x"] = x, ["y"] = y };
JsonObject Click(double x = 50, double y = 36) => new() { ["do"] = "click", ["at"] = Point(x, y) };
JsonObject Log(string message) => new() { ["do"] = "log", ["message"] = message };
JsonObject Fail(string message = "must not execute") => new() { ["do"] = "fail", ["message"] = message };
JsonObject Assert(string role, string name, string text) => new() { ["do"] = "assert", ["locator_hint"] = new JsonObject { ["role"] = role, ["name"] = name, ["text"] = text } };
JsonObject T(bool absent = false, string? onMissing = null)
{
    var value = new JsonObject { ["path"] = absent ? missingPath : patternPath, ["min_similarity"] = 1, ["random_inset_px"] = 0 };
    if (onMissing != null) value["on_missing"] = onMissing;
    return value;
}
JsonObject TemplateClick(bool absent = false, string? onMissing = null) => new() { ["do"] = "click", ["template"] = T(absent, onMissing) };
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> body)
{
    if (options.TryGetValue("--only", out var selected) && !name.Contains(selected, StringComparison.OrdinalIgnoreCase)) return;
    try { await body(); passed++; summary.Add((JsonNode)new JsonObject { ["name"] = name, ["status"] = "passed" }); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; summary.Add((JsonNode)new JsonObject { ["name"] = name, ["status"] = "failed", ["error"] = error.Message }); Console.Error.WriteLine($"FAIL {name}: {error}"); }
    await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), new JsonObject { ["passed"] = passed, ["failed"] = failed, ["cases"] = summary.DeepClone() }.ToJsonString());
}
async Task<Run> Play(string name, JsonArray steps, int expected = 0, string policy = "all", JsonObject? functions = null, JsonObject? jitter = null, bool remove = false)
{
    var scenario = new JsonObject
    {
        ["version"] = 1, ["name"] = "control-" + name,
        ["browser"] = new JsonObject { ["initial_url"] = fixture.Url + (remove ? "?mode=remove" : ""),
            ["window"] = new JsonObject { ["foreground"] = false, ["content"] = new JsonObject { ["width"] = 800, ["height"] = 600 } },
            ["display"] = new JsonObject { ["expected_dpr"] = 1, ["browser_zoom"] = 100, ["zoom_check"] = "strict" } },
        ["playback"] = new JsonObject { ["seed"] = "42", ["timeouts"] = new JsonObject { ["action_ms"] = 3000 },
            ["artifacts"] = new JsonObject { ["template_screenshots"] = policy }, ["jitter"] = jitter?.DeepClone() ?? new JsonObject { ["enabled"] = false } },
        ["steps"] = steps.DeepClone()
    };
    if (functions != null) scenario["functions"] = functions.DeepClone();
    var path = Path.Combine(root, name + ".crer.yaml");
    await File.WriteAllTextAsync(path, scenario.ToJsonString(), new UTF8Encoding(false));
    var start = new ProcessStartInfo(executable) { WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "play", path, "--chrome", chrome, "--mute-audio" }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start the published CLI.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(deadline.Token);
    }
    catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
    var output = await stdout; var errors = await stderr;
    await File.WriteAllTextAsync(Path.Combine(root, name + ".stdout.log"), output); await File.WriteAllTextAsync(Path.Combine(root, name + ".stderr.log"), errors);
    Check(process.ExitCode == expected, $"{name}: expected exit {expected}, got {process.ExitCode}: {errors}");
    JsonObject? result = null;
    foreach (Match match in Regex.Matches(output, @"(?m)^\{").Cast<Match>().Reverse())
        try { result = JsonNode.Parse(output[match.Index..])!.AsObject(); break; } catch (System.Text.Json.JsonException) { }
    Check(result != null, "CLI output contains no trailing JSON result");
    Check(result!["code"]!.GetValue<int>() == expected, "JSON and process exit codes differ");
    var runDirectory = Path.GetFullPath(result["runDir"]!.GetValue<string>());
    var cleanup = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "cleanup.json")))!;
    Check(cleanup["chromeExited"]!.GetValue<bool>() && cleanup["pendingCdpRequests"]!.GetValue<int>() == 0 && cleanup["errors"]!.AsArray().Count == 0, "owned Chrome/CDP cleanup failed");
    Check(!cleanup["profileRetained"]!.GetValue<bool>(), "temporary profile was retained without keep-artifacts");
    var logs = File.ReadLines(Path.Combine(runDirectory, "steps.ndjson")).Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    var calls = File.ReadLines(Path.Combine(runDirectory, "cdp.ndjson")).Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)!.AsObject()).Where(call => call["method"]!.GetValue<string>() == "Input.dispatchMouseEvent").ToArray();
    summary.Add((JsonNode)new JsonObject { ["name"] = name, ["runDir"] = runDirectory, ["scenario"] = path, ["code"] = expected });
    return new(runDirectory, logs, calls, output);
}

await Test("scroll sends real CDP wheel events and changes the fixture scroll position in both directions", async () =>
{
    JsonObject WaitScroll(string text) => new() { ["do"] = "wait_for", ["state"] = "visible", ["locator_hint"] = new JsonObject { ["role"] = "status", ["name"] = "Scroll", ["text"] = text } };
    var run = await Play("scroll", new JsonArray(
        new JsonObject { ["do"] = "scroll", ["at"] = Point(350, 350), ["delta"] = Point(0, 175) }, WaitScroll("scrolled"), Assert("status", "Scroll", "scrolled"),
        new JsonObject { ["do"] = "scroll", ["at"] = Point(350, 350), ["delta"] = Point(0, -175) }, WaitScroll("top"), Assert("status", "Scroll", "top")));
    var wheel = run.Mouse.Where(call => Text(call["params"]!.AsObject(), "type") == "mouseWheel").ToArray();
    Check(wheel.Length == 2 && wheel[0]["params"]!["deltaY"]!.GetValue<double>() == 175 && wheel[1]["params"]!["deltaY"]!.GetValue<double>() == -175, "scroll did not preserve wheel direction and delta");
    Check(wheel.All(call => call["params"]!["x"]!.GetValue<double>() == 350 && call["params"]!["y"]!.GetValue<double>() == 350), "wheel target coordinates changed");
});
await Test("functions preserve numeric/string arguments; repeat zero/three; typed equals and IANA weekdays", async () =>
{
    var functions = new JsonObject { ["tap"] = new JsonObject { ["params"] = new JsonArray("x", "times", "label"), ["steps"] = new JsonArray(Log("${label}:${times}"),
        new JsonObject { ["do"] = "repeat", ["count"] = "${times}", ["steps"] = new JsonArray(new JsonObject { ["do"] = "click", ["at"] = new JsonObject { ["x"] = "${x}", ["y"] = 36 } }) }) } };
    var weekday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")).DayOfWeek.ToString()[..3].ToLowerInvariant();
    var run = await Play("functions-equals-weekday", new JsonArray(
        new JsonObject { ["do"] = "repeat", ["count"] = 0, ["steps"] = new JsonArray(Fail()) },
        new JsonObject { ["do"] = "call", ["function"] = "tap", ["args"] = new JsonObject { ["x"] = 50, ["times"] = 3, ["label"] = "numeric" } },
        new JsonObject { ["do"] = "if", ["equals"] = new JsonObject { ["left"] = "3", ["right"] = 3 }, ["then"] = new JsonArray(Fail()), ["else"] = new JsonArray(Log("typed-false")) },
        new JsonObject { ["do"] = "if", ["equals"] = new JsonObject { ["left"] = 3, ["right"] = 3 }, ["then"] = new JsonArray(Assert("status", "Count", "3")), ["else"] = new JsonArray(Fail()) },
        new JsonObject { ["do"] = "if", ["weekdays"] = new JsonArray(weekday), ["time_zone"] = "Asia/Tokyo", ["then"] = new JsonArray(Log("weekday-true")), ["else"] = new JsonArray(Fail()) }), functions: functions);
    Check(run.Steps.Any(step => Text(step, "message") == "numeric:3") && run.Steps.Any(step => Text(step, "index") == "1.tap.1.2.0"), "argument expansion or nested indexes differ");
    Check(run.Steps.Single(step => Text(step, "index") == "2")["matched"]!.GetValue<bool>() == false, "string/number equality was coerced");
});
await Test("indirect function recursion stops with a recorded call path", async () =>
{
    var run = await Play("recursive", new JsonArray(new JsonObject { ["do"] = "call", ["function"] = "one" }, Fail("unreachable")), 4,
        functions: new JsonObject { ["one"] = new JsonArray(new JsonObject { ["do"] = "call", ["function"] = "two" }), ["two"] = new JsonArray(new JsonObject { ["do"] = "call", ["function"] = "one" }) });
    Check(run.Steps.Any(step => Text(step, "error").Contains("one -> two -> one", StringComparison.Ordinal)), "recursive call path missing");
    Check(run.Steps.All(step => Text(step, "message") != "unreachable"), "recursive failure continued");
});
await Test("template if reuses only the first child's match and false branch remains a normal condition", async () =>
{
    var run = await Play("template-if-cache", new JsonArray(
        new JsonObject { ["do"] = "if", ["template"] = T(), ["then"] = new JsonArray(TemplateClick(), TemplateClick()), ["else"] = new JsonArray(Fail()) },
        new JsonObject { ["do"] = "if", ["template"] = T(true), ["then"] = new JsonArray(Fail()), ["else"] = new JsonArray(Log("absent-else")) }, Assert("status", "Count", "2")));
    Check(Regex.Matches(run.Stdout, @"\(reused\)").Count == 1, "template cache leaked to a sibling or was not reused");
    Check(run.Steps.Single(step => Text(step, "index") == "1")["matched"]!.GetValue<bool>() == false, "absent template condition failed");
    foreach (var name in new[] { "template-0.png", "template-0.0.png", "template-0.1.png", "template-1.png" }) Check(File.Exists(Path.Combine(run.Directory, name)), "all screenshot policy omitted " + name);
});
await Test("template click.then executes on match and skips missing images without executing children", async () =>
{
    var matched = TemplateClick(); matched["then"] = new JsonArray(Click());
    var absent = TemplateClick(true); absent["then"] = new JsonArray(Fail()); absent["delay_ms"] = 10;
    var run = await Play("click-then", new JsonArray(matched, absent, Assert("status", "Count", "2")));
    Check(Text(run.Steps.Single(step => Text(step, "index") == "1"), "status") == "skipped", "click.then miss was not skipped");
    Check(run.Steps.All(step => Text(step, "index") != "1.0"), "missing template click executed children");
});
await Test("static template iteration visits two rectangles and expands numeric match coordinates", async () =>
{
    var run = await Play("foreach-bindings", new JsonArray(new JsonObject { ["do"] = "for_each_template", ["template"] = T(), ["max_matches"] = 2,
        ["steps"] = new JsonArray(new JsonObject { ["do"] = "click", ["at"] = new JsonObject { ["x"] = "${match_center_x}", ["y"] = "${match_center_y}" } }, Log("match:${match_left},${match_top}:${match_width}x${match_height}")) }, Assert("status", "Count", "2")));
    var loop = run.Steps.Single(step => Text(step, "index") == "0"); Check(loop["matches"]!.GetValue<int>() == 2, "did not find both exact patterns");
    Check(run.Steps.Any(step => Text(step, "message") == "match:80,100:16x16") && run.Steps.Any(step => Text(step, "message") == "match:160,100:16x16"), "match arguments changed type or position");
});
await Test("break exits only the closest template iteration through nested conditions", async () =>
{
    var inner = new JsonObject { ["do"] = "for_each_template", ["template"] = T(), ["max_matches"] = 2, ["steps"] = new JsonArray(Click(),
        new JsonObject { ["do"] = "if", ["equals"] = new JsonObject { ["left"] = 1, ["right"] = 1 }, ["then"] = new JsonArray(new JsonObject { ["do"] = "break" }) }, Fail()) };
    var outer = new JsonObject { ["do"] = "for_each_template", ["template"] = T(), ["max_matches"] = 2, ["steps"] = new JsonArray(inner, Click()) };
    var run = await Play("nested-break", new JsonArray(outer, Assert("status", "Count", "4")));
    Check(run.Steps.Count(step => Text(step, "control") == "break") == 2 && run.Steps.Any(step => Text(step, "index") == "0.1.1"), "break escaped its closest iteration");
});
await Test("repeat_until hidden removes both images with one-child cache and no success screenshots in failure-only mode", async () =>
{
    var repeat = new JsonObject { ["do"] = "repeat_until", ["template"] = T(), ["state"] = "hidden", ["max_attempts"] = 2, ["on_limit"] = "fail", ["steps"] = new JsonArray(TemplateClick()) };
    var run = await Play("until-hidden", new JsonArray(repeat, Assert("status", "Count", "2")), policy: "failure-only", remove: true);
    Check(run.Steps.Single(step => Text(step, "index") == "0")["attempts"]!.GetValue<int>() == 2, "repeat_until attempted wrong count");
    Check(Regex.Matches(run.Stdout, @"\(reused\)").Count == 2, "repeat_until failed to reuse its first child cache");
    Check(Directory.GetFiles(run.Directory, "template-*.png").Length == 0 && Directory.GetFiles(run.Directory, "failure-*.png").Length == 0, "failure-only wrote successful template images");
});
await Test("repeat_until visible succeeds before attempting its body", async () =>
{
    var run = await Play("until-visible", new JsonArray(new JsonObject { ["do"] = "repeat_until", ["template"] = T(), ["state"] = "visible", ["max_attempts"] = 1, ["on_limit"] = "fail", ["steps"] = new JsonArray(Fail()) }, Assert("status", "Count", "0")));
    Check(run.Steps.Single(step => Text(step, "index") == "0")["attempts"]!.GetValue<int>() == 0, "visible template executed body");
});
await Test("repeat_until on_limit continue logs skipped after exactly two bodies and three screenshots", async () =>
{
    var run = await Play("until-continue", new JsonArray(new JsonObject { ["do"] = "repeat_until", ["template"] = T(true), ["state"] = "visible", ["max_attempts"] = 2, ["on_limit"] = "continue", ["steps"] = new JsonArray(Log("attempt")) }, Assert("status", "Count", "0")));
    Check(run.Steps.Count(step => Text(step, "message") == "attempt") == 2, "on_limit body attempts differ");
    Check(Text(run.Steps.Single(step => Text(step, "index") == "0"), "status") == "skipped", "on_limit continue did not skip");
    Check(Directory.GetFiles(run.Directory, "template-0.attempt-*.png").Length == 3, "limit did not retain all observed states");
});
await Test("repeat_until failure retains its last observed PNG without recapturing a different state", async () =>
{
    var run = await Play("until-failure", new JsonArray(new JsonObject { ["do"] = "repeat_until", ["template"] = T(), ["state"] = "hidden", ["max_attempts"] = 1, ["on_limit"] = "fail", ["steps"] = new JsonArray(TemplateClick()) }), 4);
    Check(File.ReadAllBytes(Path.Combine(run.Directory, "failure-0.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(run.Directory, "template-0.attempt-1.png"))), "failure PNG differs from last template observation");
});
await Test("template on_missing skip continues, fail aborts, and failure-only retains the failed image", async () =>
{
    var skipped = await Play("missing-skip", new JsonArray(TemplateClick(true, "skip"), Click(), Assert("status", "Count", "1")), policy: "failure-only");
    Check(Text(skipped.Steps[0], "status") == "skipped" && Directory.GetFiles(skipped.Directory, "*.png").Length == 0, "missing skip changed policy");
    var failure = await Play("missing-fail", new JsonArray(TemplateClick(true, "fail"), Fail("unreachable")), 4, "failure-only");
    Check(File.Exists(Path.Combine(failure.Directory, "failure-0.png")) && Directory.GetFiles(failure.Directory, "template-*.png").Length == 0, "failure-only lost failure image or retained template images");
    Check(failure.Steps.Length == 1 && Text(failure.Steps[0], "kind") == "template", "template failure continued or changed kind");
});
await Test("same-seed real CDP coordinates match fixed legacy vectors, step override and drag displacement", async () =>
{
    var jitter = new JsonObject { ["enabled"] = true, ["distribution"] = "uniform", ["radius_px"] = 3, ["min_distance_from_edge_px"] = 0, ["out_of_bounds"] = "fail" };
    var click = Click(); click["count"] = 3; click["jitter"] = new JsonObject { ["enabled"] = false };
    var doubleClick = new JsonObject { ["do"] = "double_click", ["at"] = Point(50, 36), ["jitter"] = new JsonObject { ["enabled"] = true, ["distribution"] = "uniform", ["radius_px"] = 1, ["min_distance_from_edge_px"] = 0, ["out_of_bounds"] = "fail" } };
    var steps = new JsonArray(click, new JsonObject { ["do"] = "mouse_move", ["at"] = Point(100, 200) }, doubleClick,
        new JsonObject { ["do"] = "drag", ["from"] = Point(320, 180), ["to"] = Point(400, 240) }, Assert("status", "Count", "5"), Assert("status", "Moves", "10"));
    var first = await Play("seed-first", steps, jitter: jitter); var second = await Play("seed-second", steps, jitter: jitter);
    Check(JsonNode.DeepEquals(new JsonArray(first.Mouse.Select(value => value["params"]!.DeepClone()).ToArray()), new JsonArray(second.Mouse.Select(value => value["params"]!.DeepClone()).ToArray())), "identical seed changed CDP coordinate sequence");
    var numbers = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-prng-vectors.json")))!["numbers"]!.AsArray().Single(vector => vector!["seed"]!.GetValue<string>() == "42")!["values"]!.AsArray();
    (double X, double Y) Expected(double x, double y, double radius, int consumed)
    {
        var theta = 2 * Math.PI * numbers[consumed]!.GetValue<double>(); var r = Math.Sqrt(numbers[consumed + 1]!.GetValue<double>()) * radius;
        return (x + Math.Cos(theta) * r, y + Math.Sin(theta) * r);
    }
    void At(JsonNode point, (double X, double Y) expected)
    { Check(Math.Abs(point["x"]!.GetValue<double>() - expected.X) <= 1e-12 && Math.Abs(point["y"]!.GetValue<double>() - expected.Y) <= 1e-12, "effective coordinate differs from frozen legacy sequence"); }
    At(first.Steps.Single(step => Text(step, "index") == "0")["at"]!, (50, 36));
    At(first.Steps.Single(step => Text(step, "index") == "1")["at"]!, Expected(100, 200, 3, 0));
    At(first.Steps.Single(step => Text(step, "index") == "2")["at"]!, Expected(50, 36, 1, 2));
    var down = first.Mouse.Last(call => Text(call["params"]!.AsObject(), "type") == "mousePressed")["params"]!;
    var up = first.Mouse.Last(call => Text(call["params"]!.AsObject(), "type") == "mouseReleased")["params"]!;
    At(down, Expected(320, 180, 3, 4));
    Check(Math.Abs(up["x"]!.GetValue<double>() - down["x"]!.GetValue<double>() - 80) < 1e-12 && Math.Abs(up["y"]!.GetValue<double>() - down["y"]!.GetValue<double>() - 60) < 1e-12, "drag endpoints did not share one jitter offset");
});

Console.WriteLine($"{passed} passed, {failed} failed; evidence: {root}");
return failed == 0 ? 0 : 1;

static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
sealed record Run(string Directory, JsonObject[] Steps, JsonObject[] Mouse, string Stdout);
