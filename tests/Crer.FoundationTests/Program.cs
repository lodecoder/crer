using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Crer;

var passed = 0; var failed = 0;
void Check(bool condition, string message = "condition failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
void Reject(Action action, string message, int code = 2)
{
    try { action(); }
    catch (CrerException error) { Check(error.Code == code && error.Message.Contains(message), $"unexpected diagnostic: {error.Message}"); return; }
    throw new Exception("invalid input was accepted");
}
var root = Path.GetFullPath(Path.Combine(".crer", "foundation", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var date = DateTimeOffset.Parse("2026-09-13T04:15:23.123Z", CultureInfo.InvariantCulture);
var uuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
Test("run ID exact UTC prefix", () => Check(RunArtifacts.CreateRunId("daily-check", date, uuid) == "20260913T041523123Z-daily-check-550e8400-e29b-41d4-a716-446655440000"));
Test("run IDs sort in UTC millisecond order", () => Check(string.CompareOrdinal(RunArtifacts.CreateRunId("work", date, uuid), RunArtifacts.CreateRunId("work", date.AddMilliseconds(1), uuid)) < 0));
Test("run ID Japanese, NFKC and safe fallback", () =>
{
    Check(RunArtifacts.CreateRunId("日次/レポート:確認", date, uuid).Contains("-日次-レポート-確認-"));
    Check(RunArtifacts.CreateRunId("../", date, uuid).Contains("-run-"));
    Check(RunArtifacts.CreateRunId("ａｂｃ", date, uuid).Contains("-abc-"));
    Check(RunArtifacts.CreateRunId(new string('x', 200), date, uuid).Contains("-" + new string('x', 64) + "-"));
    var astral = string.Concat(Enumerable.Repeat("\U00010400", 65));
    Check(RunArtifacts.CreateRunId(astral, date, uuid).Split('-')[1].EnumerateRunes().Count() == 64, "truncate Unicode code points");
});
Test("logger per-line prefix and ANSI reset", () =>
{
    Check(ScenarioLogger.Format("login\nnext", "one\r\ntwo", "red", false) == "[login next] one\n[login next] two");
    Check(ScenarioLogger.Format("login", "one\ntwo", "red", true) == "\u001b[31m[login] one\u001b[39m\n\u001b[31m[login] two\u001b[39m");
    Check(ScenarioLogger.Format("b", "x", "default", true) == "[b] x");
});
Test("失敗JSONは日本語画像パスを読みやすく保ち、制御文字と引用符を正しく保存する", () =>
{
    var message = "0:template:Error: template match failed: templates\\日本語画像.png \"確認\"\r\n次の行\t詳細";
    var failure = new JsonObject { ["code"] = 4, ["failures"] = new JsonArray(message) };
    foreach (var (name, options) in new[] { ("整形なし", Json.Compact), ("整形あり", Json.Indented) })
    {
        var text = failure.ToJsonString(options);
        Check(text.Contains("日本語画像.png", StringComparison.Ordinal), $"{name}のJSONで日本語画像パスがエスケープされました");
        Check(JsonNode.DeepEquals(JsonNode.Parse(text), failure), $"{name}のJSONで引用符・バックスラッシュ・改行・タブが変化しました");
        if (!options.WriteIndented) Check(text.IndexOfAny(['\r', '\n', '\t']) < 0, "整形なしのJSONが複数行になったか、制御文字がそのまま出力されました");
    }
    var file = Path.Combine(root, "unicode-failure.json");
    Json.Write(file, failure);
    var written = File.ReadAllText(file);
    Check(written.Contains("日本語画像.png", StringComparison.Ordinal) && JsonNode.DeepEquals(JsonNode.Parse(written), failure),
        "失敗JSONのファイル出力で日本語画像パスまたは文字列の内容が変化しました");
});
Test("Chrome drops inherited DPI shims with all whitespace and retains unrelated settings", () =>
{
    Check(ChromeEnvironment.FilterCompatibilityLayer(null) == null);
    foreach (var value in new[] { "DPIUNAWARE", "GDIDPISCALING DPIUNAWARE", "~ HIGHDPIAWARE", "dpiunaware", "", "~\tDPIUNAWARE\r\nHIGHDPIAWARE" })
        Check(ChromeEnvironment.FilterCompatibilityLayer(value) == "");
    Check(ChromeEnvironment.FilterCompatibilityLayer("~ WIN7RTM DPIUNAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE") == "~ WIN7RTM DISABLEDXMAXIMIZEDWINDOWEDMODE");
    Check(ChromeEnvironment.FilterCompatibilityLayer("RUNASINVOKER") == "RUNASINVOKER");
    var start = new System.Diagnostics.ProcessStartInfo(); start.Environment["__COMPAT_LAYER"] = "~ HIGHDPIAWARE";
    ChromeEnvironment.Apply(start); Check(start.Environment["__COMPAT_LAYER"] == "");
});
Test("inspect shallow files and malformed optional diagnostic", () =>
{
    var directory = Path.Combine(root, "inspect"); Directory.CreateDirectory(Path.Combine(directory, "profile"));
    File.WriteAllText(Path.Combine(directory, "profile", "secret"), "not listed");
    File.WriteAllText(Path.Combine(directory, "run.json"), "{\"seed\":\"42\"}");
    File.WriteAllText(Path.Combine(directory, "foreground.json"), "{\"restoreStatus\":0}");
    File.WriteAllText(Path.Combine(directory, "display.json"), "invalid");
    File.WriteAllText(Path.Combine(directory, "steps.ndjson"), "{\"status\":\"ok\"}\n{\"status\":\"failed\"}\n");
    File.WriteAllBytes(Path.Combine(directory, "result.png"), [1, 2, 3]);
    var value = Diagnostics.Inspect(directory);
    Check(Json.Text(value["run"]?["seed"]) == "42"); Check(Json.Number(value["foreground"]?["restoreStatus"]) == 0);
    Check(!value.ContainsKey("display") && Json.Number(value["steps"]?["count"]) == 2 && Json.Number(value["steps"]?["failed"]) == 1);
    Check(value["screenshots"]!.AsArray().Count == 1 && Json.Text(value["screenshots"]![0]) == "result.png");
    Check(value["files"]!.AsArray().Count == 5 && !value.ToJsonString().Contains("secret"));
    File.AppendAllText(Path.Combine(directory, "steps.ndjson"), "malformed\n");
    Check(!Diagnostics.Inspect(directory).ContainsKey("steps"));
    Reject(() => Diagnostics.Inspect(Path.Combine(root, "missing")), "not a directory");
});
Test("NDJSON artifact keeps one object per line without text input", () =>
{
    var file = Path.Combine(root, "steps.ndjson");
    var entries = new[] { new JsonObject { ["index"] = "0", ["do"] = "text", ["status"] = "ok" }, new JsonObject { ["index"] = "1.0", ["do"] = "log", ["message"] = "a\nb" } };
    foreach (var entry in entries) File.AppendAllText(file, entry.ToJsonString() + "\n");
    var lines = File.ReadAllLines(file); Check(lines.Length == 2 && Json.Text(JsonNode.Parse(lines[1])?["message"]) == "a\nb");
    Check(!JsonNode.Parse(lines[0])!.AsObject().ContainsKey("value"));
});
Test("CLI full command-specific value/switch inventory", () =>
{
    var specifications = new Dictionary<string, (string[] values, string[] switches)>
    {
        ["doctor"] = (["--chrome"], []), ["inspect"] = ([], []), ["validate"] = ([], []),
        ["play"] = (["--chrome", "--seed", "--step-delay-ms", "--profile-dir", "--template-screenshots", "--position"], ["--keep-artifacts", "--ignore-viewport-mismatch", "--mute-audio"]),
        ["run"] = (["--chrome", "--profile-dir", "--plan-window-bounds-override", "--plan-window-opacity-override", "--template-screenshots"], ["--ignore-viewport-mismatch", "--mute-audio"]),
        ["record"] = (["--chrome", "--profile-dir", "--url", "--content-size", "--position", "--duration-ms", "--stop-file", "--name"], []),
        ["normalize"] = (["--output", "--url", "--name", "--client-origin", "--client-size", "--viewport"], []),
    };
    string Sample(string key) => key switch { "--seed" => "42", "--step-delay-ms" or "--duration-ms" => "0", "--template-screenshots" => "failure-only", "--position" or "--client-origin" => "-10,20", "--client-size" or "--viewport" or "--content-size" => "800,600", "--plan-window-bounds-override" => "10,20,800,600", "--plan-window-opacity-override" => "0", _ => "sample" };
    foreach (var (command, spec) in specifications)
    {
        var prefix = command == "doctor" ? new[] { command } : new[] { command, "test.yaml" };
        foreach (var option in spec.values)
        {
            Check(CliOptions.Parse([.. prefix, option, Sample(option)]).Value(option) == Sample(option));
            Reject(() => CliOptions.Parse([.. prefix, option]), "requires a value");
            Reject(() => CliOptions.Parse([.. prefix, option, "--anything"]), "requires a value");
            Reject(() => CliOptions.Parse([.. prefix, option, Sample(option), option, Sample(option)]), "more than once");
        }
        foreach (var option in spec.switches)
        {
            CliOptions.Parse([.. prefix, option]); Reject(() => CliOptions.Parse([.. prefix, option, option]), "more than once");
        }
        Reject(() => CliOptions.Parse([.. prefix, "--unknown"]), "unknown option");
    }
    Reject(() => CliOptions.Parse(["run", "a", "--max-parallel", "2"]), "unknown option");
    Reject(() => CliOptions.Parse(["browser", "install"]), "unknown command");
});
Test("external DLL options are rejected for every public command", () =>
{
    foreach (var command in new[] { "doctor", "inspect", "validate", "play", "run", "record", "normalize" })
    {
        var prefix = command == "doctor" ? new[] { command } : new[] { command, "test.yaml" };
        Reject(() => CliOptions.Parse([.. prefix, "--dll", "unused.dll"]), "unknown option");
    }
});
Test("run bounds replaces while position remains a separate play override", () =>
{
    var run = CliOptions.Parse(["run", "plan", "--plan-window-bounds-override", "0x10,-20,800,600", "--plan-window-opacity-override", "0"]);
    Check(run.BoundsOverride!.Count == 4 && Json.Number(run.BoundsOverride["left"]) == 16 && run.OpacityOverride == 0 && run.Position == null);
    Check(CliOptions.Parse(["run", "p", "--plan-window-bounds-override", "1,2"]).BoundsOverride!.Count == 2);
    foreach (var value in new[] { "1", "1,2,3", "1.5,2", "1,2,0,1", "1,2,1,-1" }) Reject(() => CliOptions.Parse(["run", "p", "--plan-window-bounds-override", value]), "--plan-window-bounds-override");
    foreach (var value in new[] { "", " ", "-0.1", "1.1", "NaN", "Infinity" }) Reject(() => CliOptions.Parse(["run", "p", "--plan-window-opacity-override", value]), "finite number");
    Check(CliOptions.Parse(["run", "p", "--plan-window-opacity-override", "1"]).OpacityOverride == 1);
});
Test("bare plan filenames use cwd and child scenarios resolve from the plan directory", () =>
{
    var directory = Path.Combine(root, "path-resolution"); Directory.CreateDirectory(directory);
    var child = Path.Combine(directory, "child.crer.yaml");
    File.WriteAllText(child, "version: 1\nname: path-test\nbrowser: { initial_url: 'https://example.com' }\nsteps: []\n");
    var original = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = directory;
        foreach (var (parent, configuredChild) in new[] { ("plan.crer.plan.yaml", "child.crer.yaml"), (Path.Combine(directory, "plan.crer.plan.yaml"), "child.crer.yaml"), ("plan.crer.plan.yaml", child) })
        {
            var playback = new PathPlayback();
            var plan = new JsonObject { ["version"] = 1, ["name"] = "paths", ["run"] = new JsonObject { ["scenario"] = configuredChild } };
            var outcome = PlanRunner.Run(plan, CliOptions.Parse(["run", parent]), "unused", default, () => playback).GetAwaiter().GetResult();
            Check(outcome.Code == 0 && playback.ScenarioPath == child, "child path did not resolve from the parent plan");
        }
        Check(Template.PathFor(null, "template.png") == Path.Combine(directory, "template.png"));
        Check(Template.PathFor(directory, child) == child, "absolute child path was prefixed twice");
    }
    finally { Environment.CurrentDirectory = original; }
});
var windowType = typeof(Playback).Assembly.GetType("Crer.WindowController")!;
object InvokeWindow(MethodInfo method, object? owner, params object?[] arguments)
{
    try { return method.Invoke(owner, arguments)!; }
    catch (TargetInvocationException error) when (error.InnerException != null)
    { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
}
Test("window opacity validates endpoints and rounds native alpha like JavaScript", () =>
{
    var opacity = windowType.GetMethod("OpacityAlpha")!;
    foreach (var (value, expected) in new[] { (0d, 0u), (.5, 128u), (1d, 255u), (127.5 / 255, 128u), (.1, 26u) })
        Check((uint)InvokeWindow(opacity, null, value) == expected, $"incorrect native alpha for {value}");
    foreach (var value in new[] { -.01, 1.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        Reject(() => InvokeWindow(opacity, null, value), "browser.window.opacity");
});
Test("default opaque windows configure and opacity rejects a missing process", () =>
{
    var directory = Path.Combine(root, "opacity"); Directory.CreateDirectory(directory);
    using var window = (IDisposable)Activator.CreateInstance(windowType)!;
    var configure = windowType.GetMethod("Configure")!;
    void Configure(JsonObject setting) => ((Task)InvokeWindow(configure, window, 0, setting, CliOptions.Parse(["play", "unused"]), new ScenarioLogger("test"), directory, false, "once", CancellationToken.None)).GetAwaiter().GetResult();
    Configure(new()); Configure(new() { ["opacity"] = 1 });
    Check(Json.Number(JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "opacity.json")))?["alpha"]) == 255);
    Reject(() => Configure(new() { ["opacity"] = .5 }), "could not set CfT opacity (Win32 status 87)", 3);
    window.Dispose();
});
Test("profiles within dedicated root, traversal and outside rejection", () =>
{
    var configured = $".crer/profiles/foundation-{Guid.NewGuid():N}";
    var path = Profiles.PersistentDirectory(configured);
    Check(Path.IsPathFullyQualified(path) && Directory.Exists(path));
    Check(Profiles.PersistentDirectory(path.ToUpperInvariant()).Equals(path, StringComparison.OrdinalIgnoreCase));
    Reject(() => Profiles.PersistentDirectory(root), "under .crer");
    Reject(() => Profiles.PersistentDirectory(".crer/profiles/../outside"), "must not contain ..");
    Reject(() => Profiles.PersistentDirectory(" "), "requires a directory");
    Check(!Profiles.Inside(path + "-sibling", path));
});
Test("profile lock blocks concurrent ownership and can be reacquired", () =>
{
    var directory = Profiles.PersistentDirectory($".crer/profiles/lock-{Guid.NewGuid():N}");
    using (var first = Profiles.Acquire(directory)) Reject(() => Profiles.Acquire(directory), "already in use", 3);
    using var second = Profiles.Acquire(directory); Check(second.CanWrite);
});
Test("privacy defaults preserve unrelated persistent preferences", () =>
{
    var directory = Path.Combine(root, "preferences"); Directory.CreateDirectory(Path.Combine(directory, "Default"));
    var path = Path.Combine(directory, "Default", "Preferences");
    File.WriteAllText(path, "{\"profile\":{\"keep\":123,\"password_manager_enabled\":true},\"translate\":{\"keep\":true},\"other\":\"saved\"}");
    Profiles.Prepare(directory); var value = JsonNode.Parse(File.ReadAllText(path))!;
    Check(Json.Text(value["other"]) == "saved" && Json.Number(value["profile"]?["keep"]) == 123 && value["translate"]!["keep"]!.GetValue<bool>());
    Check(!value["profile"]!["password_manager_enabled"]!.GetValue<bool>() && !value["profile"]!["password_manager_leak_detection"]!.GetValue<bool>() && !value["translate"]!["enabled"]!.GetValue<bool>() && !value["credentials_enable_service"]!.GetValue<bool>());
});
void DirectoryLink(string link, string target)
{
    if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var value in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(value);
    using var command = Process.Start(start)!;
    command.WaitForExit();
    Check(command.ExitCode == 0, "could not create owned test junction: " + command.StandardError.ReadToEnd());
}
Test("profile Default junction cannot modify external preferences", () =>
{
    var directory = Path.Combine(root, "default-link-profile"); var outside = Path.Combine(root, "default-link-outside");
    Directory.CreateDirectory(directory); Directory.CreateDirectory(outside);
    var path = Path.Combine(outside, "Preferences"); const string original = "{\"translate\":{\"enabled\":true}}";
    File.WriteAllText(path, original); DirectoryLink(Path.Combine(directory, "Default"), outside);
    Reject(() => Profiles.Prepare(directory), "outside its dedicated directory");
    Check(File.ReadAllText(path) == original, "external preferences were modified");
});
Test("profile Preferences and lock reparse paths reject external targets before opening", () =>
{
    foreach (var relative in new[] { Path.Combine("Default", "Preferences"), ".crer-profile.lock" })
    {
        var directory = Path.Combine(root, "nested-link-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "nested-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Default")); Directory.CreateDirectory(outside);
        DirectoryLink(Path.Combine(directory, relative), outside);
        if (relative == ".crer-profile.lock") Reject(() => Profiles.Acquire(directory), "outside its dedicated directory");
        else Reject(() => Profiles.Prepare(directory), "outside its dedicated directory");
        Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "external path was modified");
    }
});
Test("nested profile Cache junction cannot direct future browser writes outside the profile", () =>
{
    var directory = Path.Combine(root, "cache-link-profile"); var outside = Path.Combine(root, "cache-link-outside");
    Directory.CreateDirectory(Path.Combine(directory, "Default")); Directory.CreateDirectory(outside);
    DirectoryLink(Path.Combine(directory, "Default", "Cache"), outside);
    Reject(() => Profiles.Prepare(directory), "outside its dedicated directory");
    Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "external cache target was modified");
});
Test("diagnostic write failure still releases profile ownership during cleanup", () =>
{
    var directory = Path.Combine(root, "cleanup-profile"); Directory.CreateDirectory(directory);
    var unavailable = Path.Combine(root, "not-a-directory"); File.WriteAllText(unavailable, "owned test fixture");
    var playback = new Playback();
    void Set(string name, object value) => typeof(Playback).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(playback, value);
    Set("profileLock", Profiles.Acquire(directory)); Set("profile", directory); Set("persistent", true); Set("absoluteRunDir", unavailable);
    Reject(() => playback.CloseSession().GetAwaiter().GetResult(), "browser session cleanup failed", 3);
    using var nextOwner = Profiles.Acquire(directory); Check(nextOwner.CanWrite);
    Check(Directory.Exists(directory), "persistent profile was removed");
});
Test("doctor configured manifest, executable hash and CLI precedence", () =>
{
    var directory = Path.Combine(root, "doctor"); Directory.CreateDirectory(directory);
    var executable = Path.Combine(directory, "chrome.exe"); File.WriteAllBytes(executable, [1, 2, 3, 4]);
    var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)));
    var manifest = Path.Combine(directory, "manifest.json"); Json.Write(manifest, new JsonObject { ["chrome"] = executable, ["sha256"] = sha, ["requested"] = "pinned", ["installed"] = "version" });
    var previous = Environment.GetEnvironmentVariable("CRER_CHROME_MANIFEST"); var previousChrome = Environment.GetEnvironmentVariable("CRER_CHROME");
    var previousInput = Environment.GetEnvironmentVariable("CRER_INPUT_DLL");
    try
    {
        Environment.SetEnvironmentVariable("CRER_CHROME_MANIFEST", manifest); Environment.SetEnvironmentVariable("CRER_CHROME", "invalid-env");
        Environment.SetEnvironmentVariable("CRER_INPUT_DLL", "missing-dll");
        var result = Diagnostics.Doctor(CliOptions.Parse(["doctor", "--chrome", executable])).GetAwaiter().GetResult();
        Check(result["chrome"]!["isChromeForTesting"]!.GetValue<bool>() && Json.Text(result["chrome"]!["sha256"]) == sha);
        Check(!result.ContainsKey("ffi") && !result.ContainsKey("ffiExists"), "doctor retained external DLL diagnostics");
        Json.Write(manifest, new JsonObject { ["chrome"] = executable, ["sha256"] = "wrong" });
        Check(!Diagnostics.Chrome(executable).GetAwaiter().GetResult()["isChromeForTesting"]!.GetValue<bool>());
        Check(!Diagnostics.Chrome("missing-executable").GetAwaiter().GetResult()["exists"]!.GetValue<bool>());
        Check(Json.Text(Diagnostics.Chrome(null).GetAwaiter().GetResult()["path"]) == "not configured");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CRER_CHROME_MANIFEST", previous); Environment.SetEnvironmentVariable("CRER_CHROME", previousChrome);
        Environment.SetEnvironmentVariable("CRER_INPUT_DLL", previousInput);
    }
});
Console.WriteLine($"{passed} passed, {failed} failed; evidence: {root}");
return failed == 0 ? 0 : 1;

sealed class PathPlayback : IPlanPlayback
{
    public string FocusPolicy { get; set; } = "once";
    public string? ScenarioPath { get; private set; }
    public Task<JsonObject> RunScenario(Scenario scenario, CliOptions options, string chrome, CancellationToken token, bool retainSession)
    {
        ScenarioPath = options.File;
        return Task.FromResult(new JsonObject { ["code"] = 0, ["failures"] = new JsonArray() });
    }
    public Task CloseSession() => Task.CompletedTask;
}
