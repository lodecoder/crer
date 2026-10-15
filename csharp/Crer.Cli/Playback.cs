using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Crer;

public sealed class Playback
{
    private Cdp? cdp;
    private Process? process;
    private string? session;
    private string runDir = "";
    private string absoluteRunDir = "";
    private string profile = "";
    private readonly HashSet<string> requests = [];
    private readonly object networkLock = new();
    private long lastNetworkActivity = Environment.TickCount64;
    private double width, height;
    private JsonObject? pressed;
    private readonly List<string> cleanupErrors = [];
    private Task<string>? chromeStderr;
    private JsonObject metadata = new();
    private CancellationToken token;

    public static Task<JsonObject> Run(Scenario scenario, CliOptions options, string chrome, CancellationToken token)
        => new Playback().Execute(scenario, options, chrome, token);

    private async Task<JsonObject> Call(string method, JsonObject? p = null, bool page = true, CancellationToken? cancellation = null, int timeout = 10000)
    {
        var result = await cdp!.Call(method, p, page ? session : null, cancellation ?? token, timeout);
        if (method.StartsWith("Browser.", StringComparison.Ordinal) || method == "Input.dispatchMouseEvent")
        {
            var trace = new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["method"] = method, ["params"] = p?.DeepClone(), ["result"] = result.DeepClone() };
            File.AppendAllText(Path.Combine(absoluteRunDir, "cdp.ndjson"), trace.ToJsonString() + "\n");
        }
        return result;
    }

    private async Task<JsonNode?> Evaluate(string expression, CancellationToken? cancellation = null)
    {
        var reply = await Call("Runtime.evaluate", new() { ["expression"] = expression, ["returnByValue"] = true }, cancellation: cancellation);
        if (reply["exceptionDetails"] != null) throw new CrerException(4, "page state evaluation failed");
        return reply["result"]?["value"]?.DeepClone();
    }

    private async Task<JsonObject> Execute(Scenario s, CliOptions options, string chrome, CancellationToken cancellation)
    {
        token = cancellation;
        var safeName = Regex.Replace(s.Name, "[^A-Za-z0-9._-]", "_");
        safeName = safeName[..Math.Min(safeName.Length, 64)];
        runDir = $".crer/runs/{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{safeName}-{Guid.NewGuid()}";
        absoluteRunDir = Path.GetFullPath(runDir);
        profile = Path.Combine(absoluteRunDir, "profile");
        Directory.CreateDirectory(absoluteRunDir);
        var failures = new JsonArray();
        var code = 0;
        var seed = options.Seed?.ToString(CultureInfo.InvariantCulture) ?? Json.Text(s.Playback["seed"], RandomSeed());
        metadata = new() { ["scenario"] = s.Name, ["seed"] = seed, ["startedAt"] = DateTime.UtcNow.ToString("O"),
            ["implementation"] = "csharp", ["templateScreenshots"] = Json.Text(s.Playback["artifacts"]?["template_screenshots"], "all"),
            ["scenarioPath"] = Path.GetFullPath(options.File!), ["profile"] = profile };
        Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        File.WriteAllText(Path.Combine(absoluteRunDir, "steps.ndjson"), "");
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var navigationMs = Json.Number(s.Playback["timeouts"]?["navigation_ms"], 10000);
                startup.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(navigationMs, uint.MaxValue - 1d)));
                token = startup.Token;
                try { await Launch(s, options, chrome); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { throw new CrerException(3, "CfT startup timed out"); }
                catch (CrerException e) when (e.Code != 3)
                { throw new CrerException(3, $"CfT setup failed: {e.Message}"); }
                catch
                {
                    if (cdp != null && session != null) try { await Capture("launch-failure", CancellationToken.None); } catch { }
                    throw;
                }
                finally { token = cancellation; }
            }
            var index = 0;
            foreach (var entry in s.Steps)
            {
                token.ThrowIfCancellationRequested();
                var step = entry!.AsObject();
                var record = new JsonObject { ["index"] = index.ToString(CultureInfo.InvariantCulture), ["do"] = Json.Text(step["do"]), ["startedAt"] = DateTime.UtcNow.ToString("O") };
                try
                {
                    await Action(step, s, options);
                    record["status"] = "ok";
                    if (step["at"] != null) { record["at"] = step["at"]!.DeepClone(); record["jitterOffset"] = new JsonObject { ["x"] = 0, ["y"] = 0 }; }
                }
                catch (Exception e)
                {
                    record["status"] = "failed";
                    record["error"] = e.Message;
                    record["kind"] = e is OperationCanceledException ? "interrupted" : e is CrerException { Code: 3 } ? "environment"
                        : e.Message.Contains("timed out", StringComparison.Ordinal) ? "timeout"
                        : Json.Text(step["do"]) is "wait_for" or "assert" ? "assertion"
                        : e.Message.Contains("bounds", StringComparison.Ordinal) ? "jitter_bounds" : "action";
                    await ReleaseInput();
                    try { await Capture($"failure-{index}", CancellationToken.None); } catch (Exception capture) { record["captureError"] = capture.Message; }
                    throw;
                }
                finally
                {
                    try { record["url"] = (await Evaluate("location.href", CancellationToken.None))?.GetValue<string>(); } catch { }
                    record["completedAt"] = DateTime.UtcNow.ToString("O");
                    File.AppendAllText(Path.Combine(absoluteRunDir, "steps.ndjson"), record.ToJsonString() + "\n");
                }
                index++;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { code = 5; failures.Add((JsonNode?)JsonValue.Create("interrupted by user")); }
        catch (CrerException e) { code = e.Code; failures.Add((JsonNode?)JsonValue.Create(e.Message)); }
        catch (Exception e) { code = 3; failures.Add((JsonNode?)JsonValue.Create(e.Message)); }
        finally
        {
            await Cleanup(options.KeepArtifacts);
            if (cleanupErrors.Count > 0)
            {
                if (code == 0) code = 3;
                foreach (var e in cleanupErrors) failures.Add((JsonNode?)JsonValue.Create(e));
            }
            metadata["completedAt"] = DateTime.UtcNow.ToString("O");
            metadata["code"] = code;
            metadata["failures"] = failures.DeepClone();
            Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        }
        foreach (var failure in failures) Console.Error.WriteLine(Json.Text(failure));
        return new() { ["code"] = code, ["failures"] = failures, ["runDir"] = runDir };
    }

    private static string RandomSeed() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)).ToString(CultureInfo.InvariantCulture);

    private async Task Launch(Scenario s, CliOptions options, string configuredChrome)
    {
        if (!OperatingSystem.IsWindows()) throw new CrerException(3, "C# playback requires Windows");
        var chrome = Path.GetFullPath(configuredChrome);
        if (!File.Exists(chrome)) throw new CrerException(3, $"Chrome for Testing does not exist: {chrome}");
        // Never discover Chrome or reuse an existing profile/debugging endpoint.
        Directory.CreateDirectory(Path.Combine(profile, "Default"));
        File.WriteAllText(Path.Combine(profile, "Default", "Preferences"), "{\"translate\":{\"enabled\":false},\"credentials_enable_service\":false,\"profile\":{\"password_manager_enabled\":false,\"password_manager_leak_detection\":false}}");
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={profile}",
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-infobars", "--disable-save-password-bubble", "--force-device-scale-factor=1",
            "--disable-features=Translate,TranslateUI,PasswordManagerOnboarding", $"--app={Json.Text(s.Browser["initial_url"])}" }) start.ArgumentList.Add(argument);
        if (options.MuteAudio) start.ArgumentList.Add("--mute-audio");
        var layers = (Environment.GetEnvironmentVariable("__COMPAT_LAYER") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !new[] { "DPIUNAWARE", "GDIDPISCALING", "HIGHDPIAWARE" }.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment["__COMPAT_LAYER"] = layers.Any(x => x != "~") ? string.Join(' ', layers) : "";
        metadata["chrome"] = chrome;
        using (var file = File.OpenRead(chrome)) metadata["chromeSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        process = Process.Start(start) ?? throw new CrerException(3, "CfT process could not start");
        chromeStderr = ReadTail(process.StandardError);
        _ = ReadTail(process.StandardOutput);
        metadata["chromePid"] = process.Id;
        Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        var navigationMs = Json.Number(s.Playback["timeouts"]?["navigation_ms"], 10000);
        var watch = Stopwatch.StartNew();
        var activePort = Path.Combine(profile, "DevToolsActivePort");
        string[]? portLines = null;
        while (watch.Elapsed.TotalMilliseconds < navigationMs)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new CrerException(3, $"CfT exited during startup ({process.ExitCode})");
            try { var lines = File.ReadAllLines(activePort); if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)) { portLines = lines; break; } }
            catch (IOException) { }
            await Task.Delay(50, token);
        }
        if (portLines == null) throw new CrerException(3, "CfT startup timed out waiting for its profile DevToolsActivePort");
        var endpoint = $"http://127.0.0.1:{portLines[0]}";
        cdp = new Cdp();
        await cdp.Open($"ws://127.0.0.1:{portLines[0]}{portLines[1]}", token);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var protocol = JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/protocol", token))!;
        foreach (var required in new[] { "Input.dispatchMouseEvent", "Target.attachToTarget", "Browser.getWindowForTarget", "Browser.setWindowBounds", "Browser.setContentsSize", "Page.captureScreenshot" })
        {
            var parts = required.Split('.');
            if (!protocol["domains"]!.AsArray().Any(d => Json.Text(d?["domain"]) == parts[0] && d?["commands"] is JsonArray commands && commands.Any(c => Json.Text(c?["name"]) == parts[1])))
                throw new CrerException(3, $"CfT does not support required CDP command {required}");
        }
        JsonObject? target = null;
        while (watch.Elapsed.TotalMilliseconds < navigationMs)
        {
            var targets = await Call("Target.getTargets", page: false);
            Json.Write(Path.Combine(absoluteRunDir, "targets.json"), targets);
            var pages = targets["targetInfos"]!.AsArray().OfType<JsonObject>()
                .Where(x => Json.Text(x["type"]) == "page" && !Json.Text(x["url"]).StartsWith("chrome", StringComparison.OrdinalIgnoreCase)).ToArray();
            target = pages.FirstOrDefault(x => Json.Text(x["url"]) == Json.Text(s.Browser["initial_url"])) ?? pages.FirstOrDefault();
            if (target != null) break;
            await Task.Delay(50, token);
        }
        if (target == null) throw new CrerException(3, "CfT did not expose a page target");
        var targetId = Json.Text(target["targetId"]);
        var attached = await Call("Target.attachToTarget", new() { ["targetId"] = targetId, ["flatten"] = true }, false);
        session = Json.Text(attached["sessionId"]);
        var window = await Call("Browser.getWindowForTarget", new() { ["targetId"] = targetId }, false);
        var windowId = window["windowId"]!.GetValue<int>();
        var bounds = s.Window["bounds"]?.DeepClone().AsObject() ?? new();
        if (options.Position != null) foreach (var kv in options.Position) bounds[kv.Key] = kv.Value?.DeepClone();
        if (bounds.Count > 0)
        {
            var current = await Call("Browser.getWindowBounds", new() { ["windowId"] = windowId }, false);
            var normalize = current["bounds"]!.DeepClone().AsObject();
            normalize["windowState"] = "normal";
            await Call("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = normalize }, false);
            await Call("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = bounds }, false);
        }
        await Call("Page.enable");
        await Call("Runtime.enable");
        cdp.Event += OnNetworkEvent;
        await Call("Network.enable");
        // A target can advertise its pending URL while its execution context is still about:blank.
        // Wait for the initial document to commit before accepting readyState or sizing its renderer.
        while (Json.Text(await Evaluate("location.href")) == "about:blank") await Task.Delay(50, token);
        if (s.Window["content"] is JsonObject content)
        {
            // CfT creates/resizes its app HWND asynchronously. Match the existing runtime's retry.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Call("Browser.setContentsSize", new() { ["windowId"] = windowId, ["width"] = content["width"]!.DeepClone(), ["height"] = content["height"]!.DeepClone() }, false);
                await Task.Delay(100, token);
                var measured = await ReadDisplay();
                Json.Write(Path.Combine(absoluteRunDir, "sizing.json"), new JsonObject { ["actual"] = measured.DeepClone(), ["url"] = await Evaluate("location.href"), ["bounds"] = await Call("Browser.getWindowBounds", new() { ["windowId"] = windowId }, false) });
                if (Json.Number(measured["width"]) == Json.Number(content["width"]) && Json.Number(measured["height"]) == Json.Number(content["height"])) break;
            }
        }
        var expected = s.Window["viewport"] ?? s.Window["content"];
        JsonObject? actual = null;
        string? previous = null;
        long stableSince = Environment.TickCount64;
        watch.Restart();
        while (watch.Elapsed.TotalMilliseconds < Math.Max(1000, navigationMs))
        {
            actual = await ReadDisplay();
            var signature = actual.ToJsonString();
            if (signature != previous) { stableSince = Environment.TickCount64; previous = signature; }
            var ready = Json.Text(await Evaluate("document.readyState")) == "complete";
            var matches = expected == null || (Json.Number(actual["width"]) == Json.Number(expected["width"]) && Json.Number(actual["height"]) == Json.Number(expected["height"]));
            if (ready && matches && Environment.TickCount64 - stableSince >= 1000) break;
            await Task.Delay(100, token);
        }
        actual ??= await ReadDisplay();
        Json.Write(Path.Combine(absoluteRunDir, "display.json"), new JsonObject { ["actual"] = actual.DeepClone(), ["expectedViewport"] = expected?.DeepClone(), ["display"] = s.Display.DeepClone(), ["ignoreViewportMismatch"] = false });
        width = Json.Number(actual["width"]); height = Json.Number(actual["height"]);
        if (width <= 0 || height <= 0) throw new CrerException(3, "CfT viewport was unavailable");
        if (Json.Text(await Evaluate("document.readyState")) != "complete") throw new CrerException(3, "CfT initial page loading timed out");
        if (expected != null && (width != Json.Number(expected["width"]) || height != Json.Number(expected["height"])))
            throw new CrerException(3, $"Viewport mismatch: expected {expected}, got {width}x{height}");
        if (Json.Number(actual["dpr"]) != Json.Number(s.Display["expected_dpr"], 1) || Json.Number(actual["scale"]) != 1 || Json.Number(s.Display["browser_zoom"], 100) != 100)
            throw new CrerException(3, $"DPR / browser zoom mismatch: {actual.ToJsonString()}; this milestone requires DPR 1 and zoom 100");
        metadata["cdpEndpoint"] = endpoint;
        Json.Write(Path.Combine(absoluteRunDir, "launch.json"), new JsonObject { ["stage"] = "ready", ["pid"] = process.Id, ["targetId"] = targetId, ["sessionId"] = session });
    }

    private void OnNetworkEvent(string method, JsonObject p, string? id)
    {
        if (id != session) return;
        lock (networkLock)
        {
            if (method == "Network.requestWillBeSent") { requests.Add(Json.Text(p["requestId"])); lastNetworkActivity = Environment.TickCount64; }
            if (method is "Network.loadingFinished" or "Network.loadingFailed") { requests.Remove(Json.Text(p["requestId"])); lastNetworkActivity = Environment.TickCount64; }
        }
    }

    private async Task<JsonObject> ReadDisplay()
    {
        var metrics = await Call("Page.getLayoutMetrics");
        var display = (await Evaluate("({dpr:devicePixelRatio,scale:visualViewport?.scale ?? 1})"))!.AsObject();
        display["width"] = metrics["cssVisualViewport"]?["clientWidth"]?.DeepClone();
        display["height"] = metrics["cssVisualViewport"]?["clientHeight"]?.DeepClone();
        return display;
    }

    private async Task Action(JsonObject step, Scenario s, CliOptions options)
    {
        var action = Json.Text(step["do"]);
        var delay = Json.Number(step["delay_ms"]);
        var stepDelay = options.StepDelay ?? Json.Number(s.Playback["step_delay_ms"]);
        switch (action)
        {
            case "click":
                var x = Json.Number(step["at"]?["x"]); var y = Json.Number(step["at"]?["y"]);
                if (x < 0 || y < 0 || x > width || y > height) throw new CrerException(4, "jitter bounds failure: click is outside viewport");
                var count = Json.Number(step["count"], 1);
                for (double i = 0; i < count; i++)
                {
                    await Call("Input.dispatchMouseEvent", new() { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y });
                    // Mark before sending: an interrupted response may still have delivered mousePressed.
                    pressed = new() { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y, ["button"] = "left", ["buttons"] = 0, ["clickCount"] = 1 };
                    try
                    {
                        await Call("Input.dispatchMouseEvent", new() { ["type"] = "mousePressed", ["x"] = x, ["y"] = y, ["button"] = "left", ["buttons"] = 1, ["clickCount"] = 1 });
                        await Delay(Json.Number(step["hold_ms"]));
                        await Call("Input.dispatchMouseEvent", pressed.DeepClone().AsObject());
                        pressed = null;
                    }
                    finally { await ReleaseInput(); }
                    await Delay(delay); await Delay(stepDelay);
                }
                return;
            case "wait_for":
                var timeout = Json.Number(step["timeout_ms"], Json.Number(s.Playback["timeouts"]?["action_ms"], 10000));
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalMilliseconds < timeout)
                {
                    if (await Condition(step, false)) goto success;
                    await Task.Delay(100, token);
                }
                throw new CrerException(4, "wait_for timed out");
            case "assert":
                if (!await Condition(step, true)) throw new CrerException(4, "assert URL / ready state / locator failed");
                break;
            case "screenshot": await Capture(Json.Text(step["name"]), token); break;
            case "sleep": await Delay(Json.Number(step["ms"])); break;
            default: throw new CrerException(2, $"unsupported step: {action}");
        }
        success:
        await Delay(delay); await Delay(stepDelay);
    }

    private async Task Delay(double ms)
    {
        // Task.Delay has a bounded timer range; chunk without narrowing valid YAML numbers.
        while (ms > 0)
        {
            var part = Math.Min(ms, 86400000);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
            var delay = Task.Delay(TimeSpan.FromMilliseconds(part), waiting.Token);
            if (cdp != null && await Task.WhenAny(delay, cdp.Disconnected) == cdp.Disconnected)
            {
                waiting.Cancel();
                token.ThrowIfCancellationRequested();
                throw new CrerException(3, $"CDP connection lost during wait: {(await cdp.Disconnected).Message}");
            }
            await delay;
            ms -= part;
        }
        token.ThrowIfCancellationRequested();
    }

    private async Task<bool> Condition(JsonObject step, bool assertion)
    {
        var hint = step["locator_hint"]?.ToJsonString() ?? "{}";
        var state = (await Evaluate("(() => { const hint=" + hint + "; const elements=Array.from(document.querySelectorAll('[role]')); const found=!hint.role&&!hint.name&&!hint.text||elements.some(element=>{const role=element.getAttribute('role'); const name=element.getAttribute('aria-label')||element.textContent?.trim()||''; const text=element.textContent?.trim()||''; return (!hint.role||role===hint.role)&&(!hint.name||name===hint.name)&&(!hint.text||text===hint.text)&&!!(element.getClientRects().length&&getComputedStyle(element).visibility!=='hidden');}); return {url:location.href,state:document.readyState,found};})()"))!.AsObject();
        if (step["url"] != null)
        {
            // Deno replaces only the first '*' in this glob.
            var pattern = Regex.Escape(Json.Text(step["url"]));
            var star = pattern.IndexOf("\\*", StringComparison.Ordinal);
            if (star >= 0) pattern = pattern[..star] + ".*" + pattern[(star + 2)..];
            if (!Regex.IsMatch(Json.Text(state["url"]), "^" + pattern + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        }
        var desired = Json.Text(step["state"]);
        var current = Json.Text(state["state"]);
        var stateOk = assertion ? desired == "" || desired == current : desired == "visible" || desired == "" || current == "complete";
        if (!assertion && desired == "network_idle") lock (networkLock) stateOk = current == "complete" && requests.Count == 0 && Environment.TickCount64 - lastNetworkActivity >= 500;
        return stateOk && state["found"]!.GetValue<bool>();
    }

    private async Task Capture(string name, CancellationToken cancellation)
    {
        var result = await Call("Page.captureScreenshot", new() { ["format"] = "png" }, cancellation: cancellation, timeout: 3000);
        File.WriteAllBytes(Path.Combine(absoluteRunDir, name + ".png"), Convert.FromBase64String(Json.Text(result["data"])));
    }

    private async Task ReleaseInput()
    {
        if (pressed == null || cdp == null) return;
        try { await Call("Input.dispatchMouseEvent", pressed.DeepClone().AsObject(), cancellation: CancellationToken.None, timeout: 1500); }
        catch (Exception e) { cleanupErrors.Add($"input release failed: {e.Message}"); }
        finally { pressed = null; }
    }

    private async Task Cleanup(bool keep)
    {
        await ReleaseInput();
        if (cdp != null)
        {
            try { await Call("Browser.close", page: false, cancellation: CancellationToken.None, timeout: 1500); } catch { }
            cdp.Event -= OnNetworkEvent;
            await cdp.DisposeAsync();
        }
        var stopped = true;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    using var deadline = new CancellationTokenSource(3000);
                    try { await process.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { }
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                }
                stopped = process.HasExited;
            }
            catch (Exception e) { stopped = false; cleanupErrors.Add($"CfT cleanup failed: {e.Message}"); }
            finally { process.Dispose(); }
        }
        if (chromeStderr != null) try { File.WriteAllText(Path.Combine(absoluteRunDir, "chrome-stderr.log"), await chromeStderr.WaitAsync(TimeSpan.FromSeconds(2))); } catch { }
        if (!keep && stopped && Directory.Exists(profile))
        {
            var deleted = false;
            for (var i = 0; i < 20 && !deleted; i++)
            {
                try { Directory.Delete(profile, recursive: true); deleted = true; }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
            if (!deleted) cleanupErrors.Add("temporary profile could not be deleted");
        }
        Json.Write(Path.Combine(absoluteRunDir, "cleanup.json"), new JsonObject { ["chromeExited"] = stopped, ["profileRetained"] = Directory.Exists(profile), ["keepArtifacts"] = keep, ["pendingCdpRequests"] = cdp?.PendingCount ?? 0,
            ["errors"] = new JsonArray(cleanupErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) });
    }

    private static async Task<string> ReadTail(StreamReader reader)
    {
        var tail = ""; var buffer = new char[4096]; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0) { tail += new string(buffer, 0, n); if (tail.Length > 65536) tail = tail[^65536..]; }
        return tail;
    }
}
