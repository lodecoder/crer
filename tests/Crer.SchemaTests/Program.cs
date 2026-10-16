using System.Text.Json.Nodes;
using Crer;

var cases = new List<SchemaCase>();
const string prefix = "version: 1\nname: schema\nbrowser: { initial_url: 'https://example.test' }\n";
const string minimal = prefix + "steps: []\n";
void Accept(string name, string yaml, bool plan = false) => cases.Add(new(name, yaml, plan, true, ""));
void Reject(string name, string yaml, string diagnostic, bool plan = false) => cases.Add(new(name, yaml, plan, false, diagnostic));
void GoodStep(string name, string step, string extra = "") => Accept(name, prefix + extra + "\nsteps: [" + step + "]\n");
void BadStep(string name, string step, string diagnostic, string extra = "") => Reject(name, prefix + extra + "\nsteps: [" + step + "]\n", diagnostic);

// Every validating behavior in tests/yaml_test.ts at 50b50fd8 is represented below.
Accept("minimal scenario", minimal);
foreach (var timeout in new[] { "0", "1", "60000", "180000", "2147483647" })
    Accept("起動期限の有効値 " + timeout, prefix + "playback: { timeouts: { startup_ms: " + timeout + " } }\nsteps: []");
foreach (var timeout in new[] { "-1", "1.5", "2147483648", "'60000'", "true", "null", "'${timeout}'", "{}", "[]" })
    Reject("起動期限の無効値 " + timeout, prefix + "playback: { timeouts: { startup_ms: " + timeout + " } }\nsteps: []", "startup_ms must be an integer");
foreach (var timeout in new[] { ".inf", ".NaN" })
    Reject("起動期限の非有限値 " + timeout, prefix + "playback: { timeouts: { startup_ms: " + timeout + " } }\nsteps: []", "must be finite");
foreach (var url in new[] { "http:example.test", "http:/example.test", "http:///example.test", @"http:\example.test", " https://example.test ", "http://0x7f.1", "http://01.2.3.4", "http://4294967295", "http://x..y", "http://1..", "http://%31%32%37.0.0.1", "http://exa%6dple.test", "http://a{b}", "http://user@@example.test" })
    Accept("WHATWG http URL " + url, minimal.Replace("https://example.test", url));
foreach (var url in new[] { "http://999.999.999.999", "http://example.123", "http://[fe80::1%25eth0]", "http://1.2.3.256", "http://4294967296", "http://xn--" })
    Reject("invalid WHATWG http URL " + url, minimal.Replace("https://example.test", url), "valid http or https URL");
foreach (var blocked in new[] { "[]", "['/images/.*\\.(png|jpg)(\\?.*)?$', '^https://ads\\.']", "['[]','[^]','\\k','(?<$name>x)\\k<$name>','a{2,}']" })
    Accept("validates URL blocking regex lists: " + blocked, minimal.Replace("initial_url:", "block_urls: " + blocked + ", initial_url:"));
foreach (var blocked in new[] { "null", "ads", "{}", "[null]", "[42]", "['']", "['  ']", "['[']", "['valid','(']", "['(?i)a']", "['a{3,2}']", "['(?<a>x)(?<a>y)']" })
    Reject("rejects URL blocking regex: " + blocked, minimal.Replace("initial_url:", "block_urls: " + blocked + ", initial_url:"), "browser.block_urls");
GoodStep("validates key_chord keys", "{ do: key_chord, keys: [Control, A] }");
BadStep("key_chord requires two strings", "{ do: key_chord, keys: [Control] }", "requires at least two strings");
BadStep("key_chord requires modifiers", "{ do: key_chord, keys: [A, B] }", "must contain modifiers");
BadStep("unknown atomic step", "{ do: clik, at: { x: 1, y: 2 } }", "do is not supported");
BadStep("click needs coordinates", "{ do: click }", "requires at or template");
BadStep("scroll needs delta", "{ do: scroll, at: { x: 1, y: 2 } }", "delta is required");
BadStep("text requires string", "{ do: text }", "value must be a string");
GoodStep("validates unconditional fail steps", "{ do: fail, message: unexpected }");
foreach (var message in new[] { "", ", message: '   '", ", message: 3" })
    BadStep("fail message: " + message, "{ do: fail" + message + " }", "message must be a non-empty string");
BadStep("fail excludes delay", "{ do: fail, message: unexpected, delay_ms: 1 }", "delay_ms is not supported");
Reject("explicit failure has no continue policy", prefix + "playback: { on_failure: { explicit: continue } }\nsteps: []", "playback.on_failure.explicit is not supported");
Reject("rejects file URL", minimal.Replace("https://example.test", "file:///secret.txt"), "must use http or https");
Reject("rejects regular Chrome profile", minimal.Replace("initial_url:", "profile: Default, initial_url:"), "must be ephemeral or persistent");
BadStep("rejects unsafe screenshot name", "{ do: screenshot, name: ../../outside }", "safe artifact filename");
BadStep("rejects unknown atomic fields", "{ do: log, message: ok, typo: true }", "typo is not supported");
Reject("rejects invalid jitter settings", prefix + "playback: { jitter: { enabled: true, distribution: random, radius_px: -1, min_distance_from_edge_px: 0, out_of_bounds: fail } }\nsteps: []", "playback.jitter is invalid");
GoodStep("disabled jitter", "{ do: click, at: { x: 1, y: 2 }, jitter: { enabled: false } }", "playback: { jitter: { enabled: false } }");
GoodStep("disabled jitter preserves baseline ignored values", "{ do: click, at: { x: 1, y: 2 }, jitter: { enabled: false, radius_px: -1, distribution: arbitrary, out_of_bounds: arbitrary } }");
GoodStep("validates locator hint text", "{ do: assert, locator_hint: { role: status, text: complete } }");
BadStep("rejects nonstring locator", "{ do: assert, locator_hint: { text: 1 } }", "locator_hint.text must be a string");
GoodStep("validates click template matching options", "{ do: click, template: { path: templates/button.png, min_similarity: 0.8 } }", "playback: { template: { min_similarity: 0.8, random_inset_px: 2, on_missing: skip } }");
BadStep("template excludes at", "{ do: click, at: { x: 1, y: 2 }, template: { path: x.png } }", "cannot specify both at and template");
Reject("template policy values", prefix + "playback: { template: { on_missing: continue } }\nsteps: []", "playback.template.on_missing must be fail or skip");
BadStep("on_missing is nested", "{ do: click, template: { path: x.png }, on_missing: fail }", "on_missing must be nested under template");
GoodStep("template click children", "{ do: click, template: { path: x.png }, then: [{ do: log, message: clicked }] }");
BadStep("plain click children invalid", "{ do: click, then: [] }", "requires at or template");
foreach (var policy in new[] { "all", "failure-only" })
    Accept("validates template screenshot artifact policy " + policy, prefix + "playback: { artifacts: { template_screenshots: " + policy + " } }\nsteps: []");
Reject("invalid template screenshot policy", prefix + "playback: { artifacts: { template_screenshots: none } }\nsteps: []", "must be all or failure-only");
foreach (var foreground in new[] { "true", "false" }) foreach (var mode in new[] { "always", "once" })
    Accept("validates optional CfT foreground mode " + foreground + mode, minimal.Replace("initial_url:", "window: { foreground: " + foreground + ", foreground_mode: " + mode + " }, initial_url:"));
foreach (var mode in new[] { "true", "1", "null", "''", "sometimes" })
    Reject("invalid foreground mode " + mode, minimal.Replace("initial_url:", "window: { foreground_mode: " + mode + " }, initial_url:"), "foreground_mode must be always or once");
Reject("foreground is boolean", minimal.Replace("initial_url:", "window: { foreground: always }, initial_url:"), "foreground must be a boolean");
GoodStep("validates a post-operation delay", "{ do: click, at: { x: 1, y: 2 }, delay_ms: 1040 }");
BadStep("sleep cannot have delay", "{ do: sleep, ms: 10, delay_ms: 1 }", "delay_ms must be a non-negative number on a non-sleep step");
GoodStep("validates a click hold duration", "{ do: click, at: { x: 1, y: 2 }, hold_ms: 1000 }");
BadStep("double click hold invalid", "{ do: double_click, at: { x: 1, y: 2 }, hold_ms: 1 }", "hold_ms must be a non-negative number on a click step");
BadStep("negative click hold invalid", "{ do: click, at: { x: 1, y: 2 }, hold_ms: -1 }", "hold_ms must be a non-negative number on a click step");
GoodStep("validates console log steps", "{ do: log, message: loaded }");
BadStep("log requires string", "{ do: log }", "message must be a string for log");
GoodStep("validates repeat steps", "{ do: repeat, count: 3, steps: [{ do: log, message: retry }] }");
GoodStep("zero repeat valid", "{ do: repeat, count: 0, steps: [] }");
BadStep("negative repeat invalid", "{ do: repeat, count: -1, steps: [] }", "count must be a non-negative integer for repeat");
BadStep("repeat needs body", "{ do: repeat, count: 1 }", "steps must be a step array for repeat");
GoodStep("validates repeated click counts", "{ do: click, at: { x: 1, y: 2 }, count: 3 }");
BadStep("zero click count invalid", "{ do: click, at: { x: 1, y: 2 }, count: 0 }", "count must be a positive integer for click");
BadStep("navigate has no count", "{ do: navigate, url: https://example.test, count: 1 }", "count is supported only for repeat or click");
const string until = "{ do: repeat_until, template: { path: x.png, min_similarity: 0.9 }, state: visible, max_attempts: 3, on_limit: continue, steps: [{ do: log, message: retry }] }";
GoodStep("validates template repeat-until steps", until);
BadStep("repeat until invalid state", until.Replace("state: visible", "state: gone"), "state must be visible or hidden for repeat_until");
BadStep("repeat until invalid attempts", until.Replace("max_attempts: 3", "max_attempts: 0"), "max_attempts must be a positive integer for repeat_until");
BadStep("repeat until invalid limit", until.Replace("on_limit: continue", "on_limit: skip"), "on_limit must be fail or continue for repeat_until");
const string each = "{ do: for_each_template, template: { path: x.png }, max_matches: 10, steps: [{ do: click, at: { x: '${match_center_x}', y: '${match_center_y}' } }] }";
GoodStep("validates static template iteration steps", each);
BadStep("template iteration match bound", each.Replace("max_matches: 10", "max_matches: 0"), "max_matches must be an integer from 1 through 100");
BadStep("template iteration requires template", "{ do: for_each_template, max_matches: 1, steps: [] }", "template is required for for_each_template");
GoodStep("nested break allowed inside template iteration", "{ do: for_each_template, template: { path: x.png }, max_matches: 1, steps: [{ do: if, equals: { left: 1, right: 1 }, then: [{ do: break }] }] }");
BadStep("break outside template iteration", "{ do: break }", "break is supported only inside for_each_template.steps");
GoodStep("validates named function calls", "{ do: call, function: login }", "functions: { login: [{ do: log, message: start }] }");
BadStep("call needs named function", "{ do: call, function: missing }", "function must name a defined function for call");
Reject("function identifier", prefix + "functions: { 'not valid': [] }\nsteps: []", "functions.not valid must be an identifier");
const string fn = "functions: { search: { params: [query], steps: [{ do: text, value: '${query}' }] } }";
GoodStep("validates function call arguments", "{ do: call, function: search, args: { query: crer } }", fn);
BadStep("function args exact match", "{ do: call, function: search, args: {} }", "args must provide exactly the function parameters", fn);
Reject("function params unique", prefix + "functions: { search: { params: [query, query], steps: [] } }\nsteps: []", "params must be unique identifiers");
GoodStep("function numeric parameters", "{ do: call, function: retry, args: { count: 3, attempts: 3 } }", "functions: { retry: { params: [count, attempts], steps: [{ do: repeat, count: '${count}', steps: [] }, { do: repeat_until, template: { path: x.png }, state: visible, max_attempts: '${attempts}', on_limit: continue, steps: [] }] } }");
GoodStep("function all numeric parameter positions", "{ do: call, function: all, args: { value: 3 } }", "functions: { all: { params: [value], steps: [{ do: click, at: { x: '${value}', y: '${value}' }, count: '${value}', hold_ms: '${value}', delay_ms: '${value}', jitter: { enabled: true, distribution: uniform, radius_px: '${value}', min_distance_from_edge_px: '${value}', out_of_bounds: fail } }, { do: sleep, ms: '${value}' }, { do: for_each_template, template: { path: x.png, min_similarity: '${value}', random_inset_px: '${value}' }, max_matches: '${value}', steps: [] }] } }");
const string conditional = "{ do: if, template: { path: x.png }, then: [{ do: click, at: { x: 1, y: 2 } }], else: [{ do: log, message: missing }] }";
GoodStep("validates template conditional branches", conditional);
BadStep("if needs one condition", "{ do: if, then: [] }", "requires exactly one of template, weekdays, or equals");
BadStep("if needs then array", "{ do: if, template: { path: x.png } }", "then must be a step array for if");
BadStep("if else array", "{ do: if, template: { path: x.png }, then: [], else: {} }", "else must be a step array for if");
GoodStep("validates weekday conditional branches", "{ do: if, weekdays: [mon, wed, fri], time_zone: Asia/Tokyo, then: [] }");
BadStep("invalid weekdays", "{ do: if, weekdays: [monday], then: [] }", "weekdays must be a non-empty array");
foreach (var zone in new[] { "JST", "UTC", "Etc/UTC", "US/Eastern", "Tokyo Standard Time" })
    BadStep("invalid or noncanonical IANA zone " + zone, "{ do: if, weekdays: [mon], time_zone: '" + zone + "', then: [] }", "time_zone must be an IANA time zone string");
GoodStep("validates string equality conditional branches", "{ do: if, equals: { left: '${mode}', right: weekday }, then: [] }");
BadStep("equals strings or numbers", "{ do: if, equals: { left: a, right: true }, then: [] }", "equals requires string or finite-number");
BadStep("only one condition", "{ do: if, equals: { left: a, right: a }, weekdays: [mon], then: [] }", "requires exactly one");
const string planPrefix = "version: 1\nname: plan\nmax_parallel: 2\n";
const string planRun = "run: { serial: [{ scenario: one.crer.yaml }, { parallel: { fail_fast: true, jobs: [{ scenario: two.crer.yaml }] } }] }";
Accept("validates plan failure policies and nodes", planPrefix + "on_failure: { timeout: continue }\n" + planRun, true);
Reject("plan policy keys", planPrefix + "on_failure: { unknown: continue }\n" + planRun, "plan.on_failure.unknown is not supported", true);
Reject("plan node variants exclusive", planPrefix + "run: { scenario: one, serial: [] }", "exactly one", true);
foreach (var focus in new[] { "once", "before-step" })
    Accept("validates reusable plan browser sessions " + focus, planPrefix + "browser_session: { reuse: same-profile, focus: " + focus + " }\n" + planRun, true);
Reject("plan session reuse", planPrefix + "browser_session: { reuse: always }\n" + planRun, "reuse must be same-profile", true);
Reject("plan session focus", planPrefix + "browser_session: { reuse: same-profile, focus: scenario }\n" + planRun, "focus must be once or before-step", true);

// Additional boundary and parser regression cases from the migration goal.
foreach (var atom in new[] { "{ do: navigate, url: https://example.test }", "{ do: mouse_move, at: { x: -0.5, y: 2e2 } }", "{ do: drag, from: { x: 1, y: 2 }, to: { x: 3, y: 4 } }", "{ do: scroll, at: { x: 1, y: 2 }, delta: { x: -3, y: 4 } }", "{ do: text, value: '' }", "{ do: key, key: Enter }", "{ do: wait_for, state: network_idle }" })
    GoodStep("all atomic steps " + atom, atom);
foreach (var seed in new[] { "0", "18446744073709551615", new string('0', 100) + "42" })
    Accept("uint64 seed " + seed, prefix + "playback: { seed: '" + seed + "' }\nsteps: []");
foreach (var seed in new[] { "18446744073709551616", "-1", "1.5", "１２", "1e2" })
    Reject("invalid uint64 seed " + seed, prefix + "playback: { seed: '" + seed + "' }\nsteps: []", "playback.seed");
Reject("seed requires quoted string", prefix + "playback: { seed: 42 }\nsteps: []", "uint64 decimal string");
foreach (var property in new[] { "speed: 0", "step_delay_ms: -1", "timeouts: { navigation_ms: -1 }", "log_color: orange", "artifacts: { extra: true }" })
    Reject("invalid playback property " + property, prefix + "playback: { " + property + " }\nsteps: []", "playback.");
foreach (var window in new[] { "opacity: -0.1", "opacity: 1.1", "bounds: { left: 0.5, top: 0 }", "bounds: { left: 0, top: 0, width: 0 }", "content: { width: 0, height: 100 }", "viewport: { width: 10 }" })
    Reject("invalid window " + window, minimal.Replace("initial_url:", "window: { " + window + " }, initial_url:"), "browser.window.");
Accept("all browser and playback options", """
version: 1
name: 日本語
browser:
  chrome: chrome-for-testing@pinned
  profile: persistent:.crer/profiles/schema
  initial_url: https://example.test
  window: { bounds: { left: -10, top: 0, width: 1024, height: 768 }, foreground: false, foreground_mode: once, opacity: 0.5, content: { width: 800, height: 600 }, viewport: { width: 800, height: 600 } }
  display: { expected_dpr: 1.25, browser_zoom: 1, zoom_check: advisory }
playback:
  seed: '42'
  speed: 2
  step_delay_ms: 0
  log_color: cyan
  template: { min_similarity: 1, random_inset_px: 0, on_missing: fail }
  artifacts: { template_screenshots: all }
  jitter: { enabled: true, distribution: normal, radius_px: 5, min_distance_from_edge_px: 1, out_of_bounds: disable-for-step }
  timeouts: { navigation_ms: 0, action_ms: 100 }
  on_failure: { default: continue, navigation: abort, timeout: continue, action: continue, assertion: continue, jitter_bounds: abort, template: abort }
steps: []
""");
Accept("merge aliases explicit priority and scalar tags", """
version: !!int '0x1'
name: ! true
browser: { initial_url: https://example.test }
steps:
  - { do: click, at: &point { x: 012, y: 0b10 } }
  - { <<: [{ do: click, at: *point, count: 2 }, { count: 3 }], count: 1 }
""");
foreach (var scalar in new[] { ".inf", ".NaN", "2026-10-15", "!!bool invalid", "!!int '1'" })
    Reject("invalid scalar " + scalar, minimal.Replace("name: schema", "name: " + scalar), scalar.Contains("int") ? "invalid YAML int" : scalar.Contains("bool") ? "invalid YAML bool" : scalar.Contains("2026") ? "YAML timestamp" : "must be finite");
Reject("recursive aliases", prefix + "steps: &steps [ { do: repeat, count: 1, steps: *steps } ]", "YAML aliases must not be recursive");
Reject("unknown root key", minimal + "typo: true", "scenario.typo is not supported");
Reject("unknown nested point key", prefix + "steps: [{ do: click, at: { x: 1, y: 2, z: 3 } }]", "at.z is not supported");
Reject("unknown function numeric reference", prefix + "functions: { f: { params: [known], steps: [{ do: sleep, ms: '${missing}' }] } }\nsteps: []", "ms must be a non-negative number");
BadStep("match parameter cannot escape iteration", "{ do: click, at: { x: '${match_center_x}', y: 1 } }", "at.x must be a finite number");
Reject("plan parallel boolean", planPrefix + "run: { parallel: { fail_fast: 1, jobs: [] } }", "fail_fast must be a boolean", true);
Reject("plan worker timeout", planPrefix + "timeouts: { worker_ms: -1 }\n" + planRun, "worker_ms must be a non-negative number", true);
Reject("plan max parallel", planPrefix.Replace("max_parallel: 2", "max_parallel: 0") + planRun, "max_parallel must be a positive integer", true);
Reject("unknown plan node key", planPrefix + "run: { scenario: x, extra: true }", "extra is not supported", true);

if (args.Contains("--export-cases"))
{
    var exported = new JsonArray();
    foreach (var item in cases) exported.Add(new JsonObject { ["name"] = item.Name, ["yaml"] = item.Yaml, ["plan"] = item.Plan, ["accepted"] = item.Accepted });
    Console.WriteLine(exported.ToJsonString());
    return 0;
}
if (args.Contains("--probe"))
{
    var results = new JsonArray();
    foreach (var item in JsonNode.Parse(Console.In.ReadToEnd())!.AsArray())
    {
        try
        {
            var yaml = item!["yaml"]!.GetValue<string>();
            var value = item["plan"]!.GetValue<bool>() ? Scenario.ParsePlan(yaml) : Scenario.Parse(yaml).Root;
            results.Add(new JsonObject { ["accepted"] = true, ["value"] = value.DeepClone() });
        }
        catch (CrerException error) { results.Add(new JsonObject { ["accepted"] = false, ["code"] = error.Code, ["error"] = error.Message }); }
    }
    Console.WriteLine(results.ToJsonString());
    return 0;
}

var passed = 0;
var failed = 0;
foreach (var item in cases)
{
    try
    {
        Exception? error = null;
        try { if (item.Plan) Scenario.ParsePlan(item.Yaml); else Scenario.Parse(item.Yaml); }
        catch (CrerException exception) { error = exception; }
        if (item.Accepted && error is not null) throw error;
        if (!item.Accepted && error is null) throw new Exception("invalid YAML was accepted");
        if (error is CrerException crer && (crer.Code != 2 || !crer.Message.Contains(item.Diagnostic, StringComparison.Ordinal)))
            throw new Exception($"expected code 2 / '{item.Diagnostic}', got {crer.Code} / '{crer.Message}'");
        passed++;
        Console.WriteLine("PASS " + item.Name);
    }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {item.Name}: {error.Message}"); }
}
try
{
    var input = JsonNode.Parse("""{"x":"${count}","name":"prefix-${query}-${missing}","nested":["${query}","n=${small}","large=${large}","zero=${zero}"]}""");
    var arguments = new JsonObject { ["count"] = 3, ["query"] = "日本語", ["small"] = 1e-6, ["large"] = 1e21, ["zero"] = -0.0 };
    var expanded = Scenario.ExpandArguments(input, arguments)!;
    if (expanded["x"]!.GetValue<int>() != 3 || expanded["name"]!.GetValue<string>() != "prefix-日本語-${missing}"
        || expanded["nested"]![1]!.GetValue<string>() != "n=0.000001" || expanded["nested"]![2]!.GetValue<string>() != "large=1e+21"
        || expanded["nested"]![3]!.GetValue<string>() != "zero=0" || input!["x"]!.GetValue<string>() != "${count}")
        throw new Exception("argument expansion changed values, types, formatting, or source tree");
    passed++;
    Console.WriteLine("PASS argument expansion retains numeric type, formats strings, clones and leaves unknown references");
}
catch (Exception error) { failed++; Console.Error.WriteLine("FAIL argument expansion: " + error.Message); }
Console.WriteLine($"schema: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

internal sealed record SchemaCase(string Name, string Yaml, bool Plan, bool Accepted, string Diagnostic);
