using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Crer;

public sealed partial class Playback
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
    private readonly List<string> cleanupErrors = [];
    private Task<string>? chromeStderr;
    private Task<string>? chromeStdout;
    private BackgroundProcess? backgroundProcess;
    private JsonObject metadata = new();
    private CancellationToken token;
    private bool persistent;
    private FileStream? profileLock;
    private int windowId;
    private string targetId = "";
    private string endpoint = "";
    private UrlBlocker? blocker;
    private WindowController? windows;
    private ScenarioLogger logger = new("crer");
    public string FocusPolicy { get; set; } = "once";
    public CancellationToken UserCancellation { get; set; }

    public static Task<JsonObject> Run(Scenario scenario, CliOptions options, string chrome, CancellationToken token)
        => new Playback().RunScenario(scenario, options, chrome, token);

    public Task<JsonObject> RunScenario(Scenario scenario, CliOptions options, string chrome, CancellationToken cancellation, bool retainSession = false)
        => Execute(scenario, options, chrome, cancellation, retainSession);

    public async Task CloseSession()
    {
        await Cleanup(false);
        if (cleanupErrors.Count > 0) throw new CrerException(3, "browser session cleanup failed: " + string.Join("; ", cleanupErrors));
    }

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

    private async Task<JsonObject> Execute(Scenario s, CliOptions options, string chrome, CancellationToken cancellation, bool retainSession)
    {
        token = cancellation;
        cleanupErrors.Clear();
        logger = new(s.Name, Json.Text(s.Playback["log_color"], "default"));
        var reused = cdp != null && !cdp.Disconnected.IsCompleted && process is { HasExited: false };
        if (!reused && (cdp != null || process != null)) await CloseSession();
        runDir = $".crer/runs/{RunArtifacts.CreateRunId(s.Name)}";
        absoluteRunDir = Path.GetFullPath(runDir);
        var configuredProfile = Profiles.Configured(s, options);
        persistent = configuredProfile != null;
        var requestedProfile = persistent ? Profiles.PersistentDirectory(configuredProfile!) : Path.Combine(absoluteRunDir, "profile");
        if (reused && !string.Equals(profile, requestedProfile, StringComparison.OrdinalIgnoreCase)) throw new CrerException(3, "a browser session cannot be reused with a different profile");
        profile = requestedProfile;
        Directory.CreateDirectory(absoluteRunDir);
        var failures = new JsonArray();
        var code = 0;
        var seed = options.Seed?.ToString(CultureInfo.InvariantCulture) ?? Json.Text(s.Playback["seed"], RandomSeed());
        metadata = new() { ["scenario"] = s.Name, ["seed"] = seed, ["startedAt"] = DateTime.UtcNow.ToString("O"),
            ["implementation"] = "csharp", ["templateScreenshots"] = options.TemplateScreenshots ?? Json.Text(s.Playback["artifacts"]?["template_screenshots"], "all"),
            ["scenarioPath"] = Path.GetFullPath(options.File!), ["profile"] = profile };
        if (retainSession) metadata["browserSession"] = new JsonObject { ["reused"] = reused, ["profile"] = profile };
        if (options.BoundsOverride != null) metadata["windowBoundsOverride"] = options.BoundsOverride.DeepClone();
        if (options.OpacityOverride != null) metadata["windowOpacityOverride"] = options.OpacityOverride;
        Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        File.WriteAllText(Path.Combine(absoluteRunDir, "steps.ndjson"), "");
        try
        {
            windows ??= new WindowController(Diagnostics.InputDllPath(options));
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                // navigation_ms is accepted but not read by the fixed original runtime.
                // Endpoint/document/viewport setup below use its respective fixed deadlines.
                startup.CancelAfter(TimeSpan.FromSeconds(60));
                token = startup.Token;
                try
                {
                    if (reused)
                    {
                        windows?.Pause();
                        metadata["chrome"] = Path.GetFullPath(chrome); metadata["chromePid"] = process!.Id; metadata["cdpEndpoint"] = endpoint;
                        using var stream = File.OpenRead(chrome);
                        metadata["chromeSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
                        await ConfigurePage(s, options, true);
                    }
                    else await Launch(s, options, chrome);
                    await windows!.Configure(process!.Id, s.Window, options, logger, absoluteRunDir, retainSession, FocusPolicy);
                    beforeStep = _ => { blocker?.AssertHealthy(); windows.BeforeStep(); return Task.CompletedTask; };
                }
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
            code = await ExecuteSteps(s, options, failures);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var internalAbort = options.Command == "run" && !UserCancellation.IsCancellationRequested;
            code = internalAbort ? 4 : 5;
            failures.Add((JsonNode?)JsonValue.Create(internalAbort ? "plan execution aborted" : "interrupted by user"));
        }
        catch (CrerException e) { code = e.Code; failures.Add((JsonNode?)JsonValue.Create(e.Message)); }
        catch (Exception e) { code = 3; failures.Add((JsonNode?)JsonValue.Create(e.Message)); }
        finally
        {
            if (retainSession && persistent && code is 0 or 4 && cdp != null && !cdp.Disconnected.IsCompleted)
            {
                try
                {
                    windows?.WriteDiagnostic();
                    Json.Write(Path.Combine(absoluteRunDir, "cleanup.json"), new JsonObject { ["sessionRetained"] = true, ["chromeExited"] = false,
                        ["profileRetained"] = true, ["persistentProfile"] = true, ["pendingCdpRequests"] = cdp.PendingCount, ["errors"] = new JsonArray() });
                }
                catch (Exception e) { cleanupErrors.Add($"session diagnostics failed: {e.Message}"); await Cleanup(options.KeepArtifacts); }
            }
            else await Cleanup(options.KeepArtifacts);
            if (cleanupErrors.Count > 0)
            {
                if (code == 0) code = 3;
                foreach (var e in cleanupErrors) failures.Add((JsonNode?)JsonValue.Create(e));
            }
            metadata["completedAt"] = DateTime.UtcNow.ToString("O");
            metadata["code"] = code;
            metadata["failures"] = failures.DeepClone();
            try { Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata); }
            catch (Exception e)
            {
                if (code == 0) code = 3;
                failures.Add((JsonNode?)JsonValue.Create($"final run diagnostics failed: {e.Message}"));
            }
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
        if (persistent) profileLock = Profiles.Acquire(profile);
        Profiles.Prepare(profile);
        var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={profile}",
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-infobars", "--disable-save-password-bubble", "--force-device-scale-factor=1",
            "--disable-features=Translate,TranslateUI,PasswordManagerOnboarding", $"--app={(s.Browser["block_urls"] is JsonArray { Count: > 0 } ? "data:text/html," : Json.Text(s.Browser["initial_url"]))}" }) start.ArgumentList.Add(argument);
        if (options.MuteAudio) start.ArgumentList.Add("--mute-audio");
        ChromeEnvironment.Apply(start);
        metadata["chrome"] = chrome;
        using (var file = File.OpenRead(chrome)) metadata["chromeSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        if (s.Window["foreground"]?.GetValue<bool>() == true)
        {
            process = Process.Start(start) ?? throw new CrerException(3, "CfT process could not start");
            chromeStderr = ReadTail(process.StandardError);
            chromeStdout = ReadTail(process.StandardOutput);
            metadata["launchActivation"] = "foreground";
        }
        else
        {
            backgroundProcess = BackgroundProcessLauncher.Start(start);
            process = backgroundProcess.Process;
            chromeStderr = ReadTail(backgroundProcess.StandardError);
            chromeStdout = ReadTail(backgroundProcess.StandardOutput);
            metadata["launchActivation"] = "show-without-activation";
        }
        metadata["chromePid"] = process.Id;
        Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        var watch = Stopwatch.StartNew();
        endpoint = $"http://127.0.0.1:{port}";
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
        string? websocket = null;
        while (watch.Elapsed.TotalMilliseconds < 15000)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new CrerException(3, $"CfT exited during startup ({process.ExitCode})");
            try { websocket = Json.Text(JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/version", token))?["webSocketDebuggerUrl"]); if (websocket.Length > 0) break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) { }
            await Task.Delay(100, token);
        }
        if (websocket == null || process.HasExited) throw new CrerException(3, "CfT startup timed out waiting for its dedicated CDP endpoint");
        var websocketUri = new Uri(websocket);
        if (!websocketUri.IsLoopback || websocketUri.Port != port || !websocketUri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal)) throw new CrerException(3, "CfT returned an unexpected CDP endpoint");
        cdp = new Cdp();
        await cdp.Open(websocket, token);
        var protocol = JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/protocol", token))!;
        foreach (var required in new[] { "Input.dispatchMouseEvent", "Input.dispatchKeyEvent", "Input.insertText", "Target.attachToTarget", "Browser.getWindowForTarget", "Browser.setWindowBounds", "Browser.setContentsSize", "Page.captureScreenshot" })
        {
            var parts = required.Split('.');
            if (!protocol["domains"]!.AsArray().Any(d => Json.Text(d?["domain"]) == parts[0] && d?["commands"] is JsonArray commands && commands.Any(c => Json.Text(c?["name"]) == parts[1])))
                throw new CrerException(3, $"CfT does not support required CDP command {required}");
        }
        JsonObject? target = null;
        watch.Restart();
        while (watch.Elapsed.TotalMilliseconds < 10000)
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
        targetId = Json.Text(target["targetId"]);
        var attached = await Call("Target.attachToTarget", new() { ["targetId"] = targetId, ["flatten"] = true }, false);
        session = Json.Text(attached["sessionId"]);
        var window = await Call("Browser.getWindowForTarget", new() { ["targetId"] = targetId }, false);
        windowId = window["windowId"]!.GetValue<int>();
        cdp.Event += OnNetworkEvent;
        blocker = new UrlBlocker(cdp, session!);
        await ConfigurePage(s, options, s.Browser["block_urls"] is JsonArray { Count: > 0 }, true);
    }

    private async Task ConfigurePage(Scenario s, CliOptions options, bool navigate, bool enable = false)
    {
        lock (networkLock) { requests.Clear(); lastNetworkActivity = Environment.TickCount64; }
        var bounds = options.BoundsOverride?.DeepClone().AsObject() ?? s.Window["bounds"]?.DeepClone().AsObject() ?? new();
        if (options.Position != null) foreach (var kv in options.Position) bounds[kv.Key] = kv.Value?.DeepClone();
        if (bounds.Count > 0)
        {
            var current = await Call("Browser.getWindowBounds", new() { ["windowId"] = windowId }, false);
            var normalize = current["bounds"]!.DeepClone().AsObject();
            normalize["windowState"] = "normal";
            await Call("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = normalize }, false);
            await Call("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = bounds }, false);
        }
        if (enable)
        {
            await Call("Page.enable");
            await Call("Runtime.enable");
            await Call("Network.enable");
        }
        await blocker!.Configure(s.Browser["block_urls"] as JsonArray, token);
        if (navigate)
        {
            var navigation = await Call("Page.navigate", new() { ["url"] = s.Browser["initial_url"]!.DeepClone() });
            if (navigation["errorText"] != null) throw new CrerException(3, $"Initial navigation failed: {Json.Text(navigation["errorText"])}");
        }
        // A target can advertise its pending URL while its execution context is still about:blank.
        // Wait for the initial document to commit before accepting readyState or sizing its renderer.
        var watch = Stopwatch.StartNew();
        while (Json.Text(await Evaluate("location.href")) is "about:blank" or "data:text/html,")
        {
            if (watch.ElapsedMilliseconds >= 10000) throw new CrerException(3, "CfT initial document did not commit");
            await Task.Delay(50, token);
        }
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
                // Chromium can defer an occluded renderer's resize after the HWND changed.
                // A read-only capture synchronizes compositor/layout without foreground input.
                try { await Call("Page.captureScreenshot", new() { ["format"] = "png" }); } catch (CrerException e) when (e.Code == 4) { }
            }
        }
        var expected = s.Window["viewport"] ?? s.Window["content"];
        JsonObject? actual = null;
        string? previous = null;
        long stableSince = Environment.TickCount64;
        watch.Restart();
        while (watch.Elapsed.TotalMilliseconds < (expected != null && !options.IgnoreViewportMismatch ? 30000 : 10000))
        {
            actual = await ReadDisplay();
            var signature = actual.ToJsonString();
            if (signature != previous) { stableSince = Environment.TickCount64; previous = signature; }
            var ready = Json.Text(await Evaluate("document.readyState")) != "loading";
            var matches = options.IgnoreViewportMismatch || expected == null || (Json.Number(actual["width"]) == Json.Number(expected["width"]) && Json.Number(actual["height"]) == Json.Number(expected["height"]));
            if (ready && matches && Environment.TickCount64 - stableSince >= 1000) break;
            await Task.Delay(100, token);
        }
        actual ??= await ReadDisplay();
        Json.Write(Path.Combine(absoluteRunDir, "display.json"), new JsonObject { ["actual"] = actual.DeepClone(), ["expectedViewport"] = expected?.DeepClone(), ["display"] = s.Display.DeepClone(), ["ignoreViewportMismatch"] = options.IgnoreViewportMismatch });
        width = Json.Number(actual["width"]); height = Json.Number(actual["height"]);
        if (width <= 0 || height <= 0) throw new CrerException(3, "CfT viewport was unavailable");
        if (Json.Text(await Evaluate("document.readyState")) == "loading") throw new CrerException(3, "CfT initial page loading timed out");
        if (expected != null && (width != Json.Number(expected["width"]) || height != Json.Number(expected["height"])))
        {
            var message = $"Viewport mismatch: expected {Json.Number(expected["width"])}x{Json.Number(expected["height"])}, got {width}x{height}";
            if (!options.IgnoreViewportMismatch) throw new CrerException(3, message);
            logger.Warn($"Warning: {message}; continuing because --ignore-viewport-mismatch was specified");
        }
        var mode = Json.Text(s.Display["zoom_check"]);
        void Report(string message) { if (mode == "strict") throw new CrerException(3, message); logger.Warn(message); }
        if (s.Browser["display"] != null && mode != "off")
        {
            if (s.Display["expected_dpr"] != null && Json.Number(actual["dpr"]) != Json.Number(s.Display["expected_dpr"])) Report($"DPR mismatch: expected {Json.Number(s.Display["expected_dpr"])}, got {Json.Number(actual["dpr"])}");
            if (s.Display["browser_zoom"] != null && Json.Number(s.Display["browser_zoom"]) != 100) Report($"browser_zoom {Json.Number(s.Display["browser_zoom"])} is not supported; v1 strictly supports 100 only");
            if (Json.Number(actual["scale"]) != 1) Report($"Viewport scale mismatch: expected 1, got {Json.Number(actual["scale"])}");
        }
        blocker.AssertHealthy();
        metadata["cdpEndpoint"] = endpoint;
        Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        Json.Write(Path.Combine(absoluteRunDir, "launch.json"), new JsonObject { ["stage"] = "ready", ["pid"] = process!.Id, ["targetId"] = targetId, ["sessionId"] = session });
    }

    private void OnNetworkEvent(string method, JsonObject p, string? id)
    {
        if (id != session) return;
        lock (networkLock)
        {
            if (method == "Network.requestWillBeSent") { requests.Add(Json.Text(p["requestId"])); lastNetworkActivity = Environment.TickCount64; }
            if (method is "Network.loadingFinished" or "Network.loadingFailed") { requests.Remove(Json.Text(p["requestId"])); lastNetworkActivity = Environment.TickCount64; }
            if (method is "Network.requestWillBeSent" or "Network.loadingFinished" or "Network.loadingFailed")
                File.AppendAllText(Path.Combine(absoluteRunDir, "network.ndjson"), new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["method"] = method, ["requestId"] = p["requestId"]?.DeepClone(), ["pending"] = requests.Count }.ToJsonString() + "\n");
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

    private async Task Capture(string name, CancellationToken cancellation)
    {
        var result = await Call("Page.captureScreenshot", new() { ["format"] = "png" }, cancellation: cancellation, timeout: 3000);
        File.WriteAllBytes(Path.Combine(absoluteRunDir, name + ".png"), Convert.FromBase64String(Json.Text(result["data"])));
    }

    private async Task Cleanup(bool keep)
    {
        try
        {
            if (windows != null)
            {
                try { windows.Dispose(); } catch (Exception e) { cleanupErrors.Add($"window cleanup failed: {e.Message}"); }
                windows = null;
            }
            if (blocker != null)
            {
                try { await blocker.DisposeAsync(); } catch (Exception e) { cleanupErrors.Add($"URL blocker cleanup failed: {e.Message}"); }
                blocker = null;
            }
            if (cdp != null)
            {
                try { await Call("Browser.close", page: false, cancellation: CancellationToken.None, timeout: 1500); } catch { }
                cdp.Event -= OnNetworkEvent;
                try { await cdp.DisposeAsync(); } catch (Exception e) { cleanupErrors.Add($"CDP cleanup failed: {e.Message}"); }
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
                finally { if (backgroundProcess == null) process.Dispose(); }
            }
            if (chromeStderr != null) try { File.WriteAllText(Path.Combine(absoluteRunDir, "chrome-stderr.log"), await chromeStderr.WaitAsync(TimeSpan.FromSeconds(2))); } catch { }
            if (chromeStdout != null) try { await chromeStdout.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (backgroundProcess != null)
            {
                try { backgroundProcess.Dispose(); } catch (Exception e) { cleanupErrors.Add($"background process cleanup failed: {e.Message}"); }
                backgroundProcess = null;
            }
            if (!keep && !persistent && stopped && Directory.Exists(profile))
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
            foreach (var name in new[] { "cleanup.json", "shutdown.json" })
            {
                try
                {
                    Json.Write(Path.Combine(absoluteRunDir, name), new JsonObject { ["chromeExited"] = stopped, ["profileRetained"] = Directory.Exists(profile), ["keepArtifacts"] = keep, ["pendingCdpRequests"] = cdp?.PendingCount ?? 0,
                        ["persistentProfile"] = persistent, ["errors"] = new JsonArray(cleanupErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) });
                }
                catch (Exception e) { cleanupErrors.Add($"{name} could not be saved: {e.Message}"); }
            }
        }
        finally
        {
            try { backgroundProcess?.Dispose(); }
            catch (Exception e) { cleanupErrors.Add($"background process cleanup failed: {e.Message}"); }
            finally
            {
                try { profileLock?.Dispose(); }
                finally { profileLock = null; cdp = null; process = null; session = null; chromeStderr = null; chromeStdout = null; backgroundProcess = null; }
            }
        }
    }

    private static async Task<string> ReadTail(StreamReader reader)
    {
        var tail = ""; var buffer = new char[4096]; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0) { tail += new string(buffer, 0, n); if (tail.Length > 65536) tail = tail[^65536..]; }
        return tail;
    }
}
