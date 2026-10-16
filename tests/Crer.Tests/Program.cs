using System.Text.Json.Nodes;
using Crer;

var passed = 0;
var failed = 0;
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Reject(string yaml, string diagnostic)
{
    try { Scenario.Parse(yaml); }
    catch (CrerException e)
    {
        Check(e.Code == 2, $"expected YAML error code 2, got {e.Code}");
        Check(e.Message.Contains(diagnostic, StringComparison.Ordinal), $"expected '{diagnostic}' in '{e.Message}'");
        return;
    }
    throw new Exception("invalid scenario was accepted");
}
const string prefix = "version: 1\nname: Test\nbrowser: { initial_url: 'http://127.0.0.1:8080/' }\n";
const string minimal = prefix + "steps: []\n";

Test("minimal v1 scenario retains defaults and data", () =>
{
    var scenario = Scenario.Parse(minimal);
    Check(scenario.Name == "Test", "scenario name changed");
    Check(scenario.Steps.Count == 0 && scenario.Playback.Count == 0 && scenario.Window.Count == 0 && scenario.Display.Count == 0,
        "optional mappings did not default to empty");
    Check(scenario.Browser["initial_url"]!.GetValue<string>() == "http://127.0.0.1:8080/", "URL changed");
});
Test("flow mappings, quoted scalar types, scientific numbers, and aliases", () =>
{
    var scenario = Scenario.Parse(prefix + """
        playback: { seed: "18446744073709551615", jitter: { enabled: false } }
        steps:
          - { do: click, at: &point { x: 1.25e2, y: -2 }, count: 2, hold_ms: 5, delay_ms: 0 }
          - { do: click, at: *point, jitter: { enabled: false } }
          - { do: wait_for, state: visible, locator_hint: { role: button, name: 'true', text: "1" } }
          - { do: assert, url: 'http://127.0.0.1:8080/*', state: complete }
          - { do: screenshot, name: result.png }
          - { do: sleep, ms: 0 }
        """);
    Check(scenario.Steps.Count == 6, "step count changed");
    Check(Json.Number(scenario.Steps[0]!["at"]!["x"]) == 125, "scientific number changed");
    Check(scenario.Playback["seed"]!.GetValue<string>() == ulong.MaxValue.ToString(), "quoted seed lost its type");
    Check(Json.Number(scenario.Steps[1]!["at"]!["x"]) == 125, "YAML alias was not resolved");
});
Test("window, display, deadlines and failure defaults", () =>
{
    var scenario = Scenario.Parse("""
        version: 1
        name: Window
        browser:
          chrome: cft
          profile: ephemeral
          initial_url: https://example.test/
          window:
            bounds: { left: -10, top: 0, width: 1024, height: 768 }
            content: { width: 800, height: 600 }
            viewport: { width: 800, height: 600 }
          display: { expected_dpr: 1, browser_zoom: 1, zoom_check: strict }
        playback:
          timeouts: { navigation_ms: 0, action_ms: 1000 }
          artifacts: { template_screenshots: failure-only }
          step_delay_ms: 0
          on_failure: { default: abort, assertion: abort }
        steps: []
        """);
    Check(Json.Number(scenario.Window["bounds"]!["left"]) == -10, "window bounds changed");
    Check(Json.Number(scenario.Display["expected_dpr"]) == 1, "DPR changed");
});
Test("omitted jitter matches existing runtime's disabled default", () =>
    Scenario.Parse(prefix + "steps: [{ do: click, at: { x: 5, y: 5 } }]"));
Test("explicit YAML scalar tags override quotation and implicit resolution", () =>
{
    var scenario = Scenario.Parse(minimal.Replace("version: 1", "version: !!int '0x1'")
        .Replace("name: Test", "name: ! true"));
    Check(scenario.Name == "true", "non-specific string tag did not preserve text");
    Reject(minimal.Replace("name: Test", "name: !!bool invalid"), "invalid YAML bool scalar");
    Reject(prefix + "steps: !custom []", "unsupported YAML collection tag");
});
Test("implicit scalar resolution matches the locked Deno YAML parser", () =>
{
    foreach (var text in new[] { "_42", "42_", "0o52", "1e2_3", "-.1", "yes", "on", "1:20" })
        Check(Scenario.Parse(minimal.Replace("name: Test", "name: " + text)).Name == text, $"{text} was not a string");
    var scenario = Scenario.Parse(prefix + "steps: [{ do: click, at: { x: 012, y: 0b10 } }]");
    Check(Json.Number(scenario.Steps[0]!["at"]!["x"]) == 10, "legacy octal became decimal");
    Check(Json.Number(scenario.Steps[0]!["at"]!["y"]) == 2, "binary scalar changed");
    Reject(minimal.Replace("version: 1", "version: !!int '1'"), "invalid YAML int scalar");
    Reject(minimal.Replace("name: Test", "name: 2026-10-15"), "YAML timestamp");
    Check(Scenario.Parse(minimal.Replace("name: Test", "name: '2026-10-15'")).Name == "2026-10-15", "quoted date changed");
});
Test("merge aliases preserve explicit override and sequence priority", () =>
{
    var scenario = Scenario.Parse(prefix + """
        steps:
          - &first { do: click, at: { x: 1, y: 2 }, hold_ms: 5 }
          - &second { do: click, at: { x: 3, y: 4 }, hold_ms: 6 }
          - { <<: [*first, *second], hold_ms: 7 }
        """);
    Check(Json.Number(scenario.Steps[2]!["at"]!["x"]) == 1, "merge sequence precedence changed");
    Check(Json.Number(scenario.Steps[2]!["hold_ms"]) == 7, "explicit merge override was lost");
    Reject(prefix + "steps: [{ do: sleep, ms: 0, '<<': {} }]", "steps[0].<<");
});
Test("unknown keys rejected at each supported mapping level", () =>
{
    var cases = new (string yaml, string diagnostic)[]
    {
        (minimal + "unknown: true", "scenario.unknown"),
        (minimal.Replace("initial_url:", "unknown: true, initial_url:"), "browser.unknown"),
        (prefix + "playback: { unknown: true }\nsteps: []", "playback.unknown"),
        (prefix + "steps: [{ do: click, at: { x: 1, y: 2, z: 3 } }]", "steps[0].at.z"),
        (prefix + "steps: [{ do: assert, locator_hint: { text: ok, text_re: ok } }]", "steps[0].locator_hint.text_re"),
        (prefix + "steps: [{ do: sleep, ms: 1, delay_ms: 2 }]", "steps[0].delay_ms"),
        (prefix + "playback: { timeouts: { worker_ms: 100 } }\nsteps: []", "playback.timeouts.worker_ms"),
        (prefix + "playback: { artifacts: { surprise: true } }\nsteps: []", "playback.artifacts.surprise"),
    };
    foreach (var (yaml, diagnostic) in cases) Reject(yaml, diagnostic);
});
Test("previous milestone exclusions are now schema-compatible", () =>
{
    var cases = new string[]
    {
        prefix + "steps: [{ do: text, value: hello }]",
        prefix + "functions: {}\nsteps: []",
        prefix + "playback: { speed: 1 }\nsteps: []",
        prefix + "playback: { on_failure: { default: continue } }\nsteps: []",
        prefix + "steps: [{ do: click, at: { x: 1, y: 2 }, jitter: { enabled: false, radius_px: 0 } }]",
        minimal.Replace("initial_url:", "profile: 'persistent:tmp', initial_url:"),
        minimal.Replace("initial_url:", "display: { zoom_check: off }, initial_url:"),
    };
    foreach (var yaml in cases) Scenario.Parse(yaml);
    Reject(prefix + "steps: [{ do: assert, state: complete, title: example }]", "title");
    Reject(prefix + "playback: { jitter: { enabled: true } }\nsteps: []", "playback.jitter");
});
Test("invalid scalar types and values rejected", () =>
{
    var cases = new (string yaml, string diagnostic)[]
    {
        (minimal.Replace("version: 1", "version: '1'"), "version: 1"),
        (minimal.Replace("name: Test", "name: true"), "version: 1"),
        (minimal.Replace("http://127.0.0.1:8080/", "file:///tmp/fixture.html"), "http or https"),
        (prefix + "playback: { seed: 123 }\nsteps: []", "uint64 decimal string"),
        (prefix + "playback: { seed: '18446744073709551616' }\nsteps: []", "uint64"),
        (prefix + "playback: { timeouts: { action_ms: -1 } }\nsteps: []", "non-negative"),
        (prefix + "steps: [{ do: click, at: { x: '1', y: 2 } }]", "finite number"),
        (prefix + "steps: [{ do: click, at: { x: 1, y: 2 }, count: 1.5 }]", "positive integer"),
        (prefix + "steps: [{ do: click, at: { x: 1, y: 2 }, hold_ms: -1 }]", "non-negative"),
        (prefix + "steps: [{ do: wait_for, state: visible }]", "requires locator_hint"),
        (prefix + "steps: [{ do: assert, state: visible, locator_hint: { text: ok } }]", "not supported for assert"),
        (prefix + "steps: [{ do: screenshot, name: ../outside.png }]", "safe artifact filename"),
        (prefix + "steps: [{ do: sleep, ms: .inf }]", "finite"),
        (prefix + "steps: [{ do: assert, locator_hint: { text: 1 } }]", "must be a string"),
        (prefix + "steps: [{ do: wait_for, url: null }]", "steps[0].url"),
        (prefix + "steps: [{ do: click, at: { x: 1, y: 2 }, jitter: { enabled: 'false' } }]", "jitter"),
    };
    foreach (var (yaml, diagnostic) in cases) Reject(yaml, diagnostic);
});
Test("malformed YAML, repeated keys, document streams, cycles and tags fail as YAML errors", () =>
{
    Reject(prefix + "steps: [", "invalid YAML");
    Reject(prefix + "steps: []\nsteps: []", "YAML");
    Reject(minimal + "---\n" + minimal, "exactly one YAML document");
    Reject(prefix + "steps: &cycle [*cycle]", "recursive");
    Reject(prefix + "steps: [{ do: sleep, ms: !custom 1 }]", "unsupported YAML scalar tag");
});
Test("JSON output remains usable with reflection disabled", () =>
{
    var scenario = Scenario.Parse(minimal);
    var serialized = scenario.Root.ToJsonString(Json.Indented);
    Check(serialized.Contains("initial_url", StringComparison.Ordinal), "scenario JSON output is incomplete");
});

void RejectCli(string[] arguments, string diagnostic)
{
    try { CliOptions.Parse(arguments); }
    catch (CrerException e)
    {
        Check(e.Code == 2, $"expected CLI error code 2, got {e.Code}");
        Check(e.Message.Contains(diagnostic, StringComparison.Ordinal), $"expected '{diagnostic}' in '{e.Message}'");
        return;
    }
    throw new Exception("invalid CLI arguments were accepted");
}
Test("CLI help, validate, and file-before-options calling convention", () =>
{
    Check(CliOptions.Parse([]).Command == "help", "missing command did not show help");
    Check(CliOptions.Parse(["help"]).Command == "help", "help command was not recognized");
    var validated = CliOptions.Parse(["validate", "directory with spaces/scenario.crer.yaml"]);
    Check(validated.File == "directory with spaces/scenario.crer.yaml", "scenario path changed");
    RejectCli(["play"], "path is required");
    RejectCli(["play", "--chrome", "chrome.exe", "scenario.crer.yaml"], "option");
    RejectCli(["play", "one.yaml", "two.yaml"], "option");
    RejectCli(["validate", "one.yaml", "--chrome", "chrome.exe"], "unknown option");
    Check(CliOptions.Parse(["record", "one.ndjson"]).Command == "record", "record command missing");
});
Test("CLI options preserve values independent of option ordering", () =>
{
    var options = CliOptions.Parse(["play", "relative/scenario.yaml", "--keep-artifacts", "--seed",
        "18446744073709551615", "--mute-audio", "--chrome", "C:/Chrome for Testing/chrome.exe",
        "--position", "-123,456", "--step-delay-ms", "1.25e2"]);
    Check(options.File == "relative/scenario.yaml" && options.Chrome == "C:/Chrome for Testing/chrome.exe", "path arguments changed");
    Check(options.KeepArtifacts && options.MuteAudio, "boolean switches changed");
    Check(options.Seed == ulong.MaxValue && options.StepDelay == 125, "numeric option values changed");
    Check(Json.Number(options.Position!["left"]) == -123 && Json.Number(options.Position["top"]) == 456,
        "position changed");
});
Test("CLI duplicate options and missing values fail", () =>
{
    RejectCli(["play", "one.yaml", "--keep-artifacts", "--keep-artifacts"], "specified more than once");
    RejectCli(["play", "one.yaml", "--chrome", "a.exe", "--chrome", "b.exe"], "specified more than once");
    RejectCli(["play", "one.yaml", "--seed", "1", "--seed", "2"], "specified more than once");
    foreach (var option in new[] { "--chrome", "--seed", "--step-delay-ms", "--position" })
    {
        RejectCli(["play", "one.yaml", option], "requires a value");
        RejectCli(["play", "one.yaml", option, "--keep-artifacts"], "requires a value");
    }
});
Test("CLI command-specific options and value requirements fail explicitly", () =>
{
    foreach (var option in new[] { "--profile-dir", "--template-screenshots" })
        RejectCli(["play", "one.yaml", option], "requires a value");
    RejectCli(["play", "one.yaml", "--unknown"], "unknown option");
    RejectCli(["play", "one.yaml", "--plan-window-opacity-override", "0.5"], "unknown option");
    Check(CliOptions.Parse(["play", "one.yaml", "--ignore-viewport-mismatch"]).IgnoreViewportMismatch, "ignore flag missing");
});
Test("CLI malformed numeric values fail", () =>
{
    foreach (var seed in new[] { "-1", "+1", "1.5", "18446744073709551616", "", " 1", "0x10" })
        RejectCli(["play", "one.yaml", "--seed", seed], "uint64 decimal string");
    foreach (var delay in new[] { "-1", "NaN", "Infinity", "1,000", "abc" })
        RejectCli(["play", "one.yaml", "--step-delay-ms", delay], "non-negative number");
    foreach (var position in new[] { "1", "a,2" }) RejectCli(["play", "one.yaml", "--position", position], "must be x,y");
    RejectCli(["play", "one.yaml", "--position", "1.5,2"], "integer left,top");
    Check(Json.Number(CliOptions.Parse(["play", "one.yaml", "--position", "1,2,3"]).Position!["top"]) == 2,
        "original pointOption ignores extra comma-separated values");
});
Test("CLI Number conversion preserves decimal, radix, and exponent forms", () =>
{
    foreach (var (text, expected) in new[] { ("", 0d), ("  ", 0d), ("0x10", 16d), ("0b10", 2d), ("0o10", 8d), ("1e2", 100d) })
        Check(CliOptions.Parse(["play", "one.yaml", "--step-delay-ms", text]).StepDelay == expected, $"Number({text}) changed");
    var option = CliOptions.Parse(["play", "one.yaml", "--position", "1e2,0x10"]);
    Check(Json.Number(option.Position!["left"]) == 100 && Json.Number(option.Position["top"]) == 16, "coordinate Number conversion changed");
    RejectCli(["play", "one.yaml", "--step-delay-ms", "-0x10"], "non-negative number");
});

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
