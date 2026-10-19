using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crer.Windows;

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
    private int networkRequests, networkResponses, networkFinished, networkFailed, documentRequests, documentResponses;
    private string? lastNetworkError;
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
    private TemplateMatcher? templates;
    private WindowController? windows;
    private readonly Stopwatch startupWatch = new();
    private int startupTimeoutMs = 60000;
    private JsonObject startupDiagnostic = new();
    private string startupStage = "initializing";
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
        lock (networkLock)
        {
            requests.Clear(); lastNetworkActivity = Environment.TickCount64;
            networkRequests = networkResponses = networkFinished = networkFailed = documentRequests = documentResponses = 0;
            lastNetworkError = null;
        }
        startupWatch.Restart();
        var startupMs = (int)Json.Number(s.Playback["timeouts"]?["startup_ms"], 60000);
        startupTimeoutMs = startupMs;
        startupDiagnostic = new() { ["status"] = "starting", ["reused"] = reused, ["timeoutMs"] = startupMs, ["stages"] = new JsonArray() };
        SetStartupStage("initializing");
        try
        {
            windows ??= new WindowController();
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                // 初期応答の待機には通常の CDP 操作の10秒ではなく、起動全体の期限を適用する。
                // 利用者やワーカーからの取消しは、遷移と文書の準備待ちを含む全段階に適用する。
                if (startupMs > 0) startup.CancelAfter(startupMs);
                token = startup.Token;
                try
                {
                    if (reused)
                    {
                        SetStartupStage("reusing_session");
                        windows?.Pause();
                        metadata["chrome"] = Path.GetFullPath(chrome); metadata["chromePid"] = process!.Id; metadata["cdpEndpoint"] = endpoint;
                        using var stream = File.OpenRead(chrome);
                        metadata["chromeSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
                        await ConfigurePage(s, options, retainSession);
                    }
                    else await Launch(s, options, chrome, retainSession);
                    beforeStep = _ => { windows!.BeforeStep(); return Task.CompletedTask; };
                    startupDiagnostic["status"] = "ready";
                    SetStartupStage("ready");
                }
                catch (Exception error)
                {
                    startupDiagnostic["status"] = "failed";
                    startupDiagnostic["errorType"] = error.GetType().Name;
                    if (error is CrerException diagnosticError) startupDiagnostic["errorCode"] = diagnosticError.Code;
                    WriteStartupDiagnostic();
                    // ここでは起動期限が切れている場合が多いため、キャプチャには別の短い期限を使う。
                    // 取消し時の診断取得によってブラウザーの後始末を妨げない。
                    if (cdp != null && session != null)
                        try
                        {
                            using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await Capture("launch-failure", capture.Token);
                        }
                        catch { }
                    if (error is OperationCanceledException && !cancellation.IsCancellationRequested)
                        throw new CrerException(3, $"CfT startup timed out (stage: {startupStage})");
                    if (error is CrerException setupError && setupError.Code != 3)
                        throw new CrerException(3, $"CfT setup failed: {setupError.Message}");
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

    private void SetStartupStage(string stage)
    {
        startupStage = stage;
        startupDiagnostic["stage"] = stage;
        startupDiagnostic["stages"]!.AsArray().Add((JsonNode)new JsonObject
        { ["stage"] = stage, ["elapsedMs"] = startupWatch.Elapsed.TotalMilliseconds });
        WriteStartupDiagnostic();
    }

    private void WriteStartupDiagnostic()
    {
        // 段階名と所要時間で停止箇所を特定し、ページ URL、JavaScript 式、ダイアログ文や入力値は記録しない。
        // 診断の入出力エラーで、元の起動失敗を置き換えたりブラウザーの後始末を妨げたりしない。
        try
        {
            startupDiagnostic["elapsedMs"] = startupWatch.Elapsed.TotalMilliseconds;
            lock (networkLock)
            {
                var network = new JsonObject { ["requests"] = networkRequests, ["responses"] = networkResponses,
                    ["finished"] = networkFinished, ["failed"] = networkFailed, ["pending"] = requests.Count,
                    ["documentRequests"] = documentRequests, ["documentResponses"] = documentResponses };
                if (lastNetworkError != null) network["lastError"] = lastNetworkError;
                startupDiagnostic["network"] = network;
            }
            Json.Write(Path.Combine(absoluteRunDir, "startup.json"), startupDiagnostic);
        }
        catch { }
    }

    private async Task Launch(Scenario s, CliOptions options, string configuredChrome, bool retainSession)
    {
        if (!OperatingSystem.IsWindows()) throw new CrerException(3, "C# playback requires Windows");
        var chrome = Path.GetFullPath(configuredChrome);
        if (!File.Exists(chrome)) throw new CrerException(3, $"Chrome for Testing does not exist: {chrome}");
        SetStartupStage("profile_setup");
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
            "--disable-backgrounding-occluded-windows",
            // サイトのスクリプトが viewport を参照する前に実ウィンドウを設定する。
            // 並列ブラウザーの起動中の不透明ウィンドウに覆われる前にも設定を済ませる。
            "--disable-features=Translate,TranslateUI,PasswordManagerOnboarding", "--app=data:text/html," }) start.ArgumentList.Add(argument);
        if (options.MuteAudio) start.ArgumentList.Add("--mute-audio");
        ChromeEnvironment.Apply(start);
        metadata["chrome"] = chrome;
        using (var file = File.OpenRead(chrome)) metadata["chromeSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        SetStartupStage("process_start");
        if (s.Window["foreground"]?.GetValue<bool>() == true)
        {
            lock (BackgroundProcessLauncher.CreationGate)
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
        SetStartupStage("cdp_endpoint");
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
        SetStartupStage("cdp_connection");
        await cdp.Open(websocket, token);
        SetStartupStage("cdp_protocol");
        var protocol = JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/protocol", token))!;
        foreach (var required in new[] { "Input.dispatchMouseEvent", "Input.dispatchKeyEvent", "Input.insertText", "Target.attachToTarget", "Browser.getWindowForTarget", "Browser.setWindowBounds", "Browser.setContentsSize", "Page.captureScreenshot" })
        {
            var parts = required.Split('.');
            if (!protocol["domains"]!.AsArray().Any(d => Json.Text(d?["domain"]) == parts[0] && d?["commands"] is JsonArray commands && commands.Any(c => Json.Text(c?["name"]) == parts[1])))
                throw new CrerException(3, $"CfT does not support required CDP command {required}");
        }
        JsonObject? target = null;
        watch.Restart();
        SetStartupStage("page_target");
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
        SetStartupStage("page_attachment");
        var attached = await Call("Target.attachToTarget", new() { ["targetId"] = targetId, ["flatten"] = true }, false);
        session = Json.Text(attached["sessionId"]);
        SetStartupStage("page_window");
        var window = await Call("Browser.getWindowForTarget", new() { ["targetId"] = targetId }, false);
        windowId = window["windowId"]!.GetValue<int>();
        cdp.Event += OnNetworkEvent;
        await ConfigurePage(s, options, retainSession, true);
    }

    private async Task ConfigurePage(Scenario s, CliOptions options, bool retainSession, bool enable = false)
    {
        lock (networkLock) { requests.Clear(); lastNetworkActivity = Environment.TickCount64; }
        var bounds = options.BoundsOverride?.DeepClone().AsObject() ?? s.Window["bounds"]?.DeepClone().AsObject() ?? new();
        if (options.Position != null) foreach (var kv in options.Position) bounds[kv.Key] = kv.Value?.DeepClone();
        if (bounds.Count > 0)
        {
            SetStartupStage("window_bounds");
            var current = await Call("Browser.getWindowBounds", new() { ["windowId"] = windowId }, false);
            var normalize = current["bounds"]!.DeepClone().AsObject();
            normalize["windowState"] = "normal";
            await Call("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = normalize }, false);
            await Call("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = bounds }, false);
        }
        if (enable)
        {
            SetStartupStage("page_domains");
            await Call("Page.enable");
            await Call("Runtime.enable");
            await Call("Network.enable");
        }
        if (s.Window["content"] is JsonObject content)
        {
            SetStartupStage("content_sizing");
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
        SetStartupStage("window_control");
        // 最前面化だけを短い別期限で打ち切らず、起動全体の残り時間を使う。
        var remainingStartup = startupTimeoutMs == 0 ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(Math.Max(0, startupTimeoutMs - startupWatch.Elapsed.TotalMilliseconds));
        await windows!.Configure(process!.Id, s.Window, options, logger, absoluteRunDir, retainSession, FocusPolicy, token, remainingStartup, cdp!.Disconnected);
        SetStartupStage("navigation");
        var navigation = await Call("Page.navigate", new() { ["url"] = s.Browser["initial_url"]!.DeepClone() }, timeout: Timeout.Infinite);
        if (navigation["errorText"] != null) throw new CrerException(3, $"Initial navigation failed: {Json.Text(navigation["errorText"])}");
        SetStartupStage("document_readiness");
        await WaitForInitialDocument();
        var expected = s.Window["viewport"] ?? s.Window["content"];
        var actual = await WaitForInitialViewport(expected, options.IgnoreViewportMismatch, s.Display);
        SetStartupStage("display_validation");
        var mode = Json.Text(s.Display["zoom_check"]);
        void Report(string message) { if (mode == "strict") throw new CrerException(3, message); logger.Warn(message); }
        if (s.Browser["display"] != null && mode != "off")
        {
            if (s.Display["expected_dpr"] != null && Json.Number(actual["dpr"]) != Json.Number(s.Display["expected_dpr"])) Report($"DPR mismatch: expected {Json.Number(s.Display["expected_dpr"])}, got {Json.Number(actual["dpr"])}");
            if (s.Display["browser_zoom"] != null && Json.Number(s.Display["browser_zoom"]) != 100) Report($"browser_zoom {Json.Number(s.Display["browser_zoom"])} is not supported; v1 strictly supports 100 only");
            if (Json.Number(actual["scale"]) != 1) Report($"Viewport scale mismatch: expected 1, got {Json.Number(actual["scale"])}");
        }
        metadata["cdpEndpoint"] = endpoint;
        Json.Write(Path.Combine(absoluteRunDir, "run.json"), metadata);
        Json.Write(Path.Combine(absoluteRunDir, "launch.json"), new JsonObject { ["stage"] = "ready", ["pid"] = process!.Id, ["targetId"] = targetId, ["sessionId"] = session });
    }

    private async Task<JsonObject> WaitForInitialViewport(JsonNode? expected, bool ignoreViewportMismatch, JsonObject display)
    {
        SetStartupStage("viewport_stability");
        JsonObject? actual = null;
        string? previous = null;
        long stableSince = Environment.TickCount64;
        var watch = Stopwatch.StartNew();
        var viewportTimeoutMs = expected != null && !ignoreViewportMismatch ? 30000 : 10000;
        while (true)
        {
            try
            {
                var measured = await ReadDisplay();
                // 測定後の文書を確認し、追加遷移の読み込みをviewportの短い期限で打ち切らない。
                if (Json.Text(await Evaluate("document.readyState")) != "loading")
                {
                    actual = measured;
                    var signature = actual.ToJsonString();
                    if (signature != previous) { stableSince = Environment.TickCount64; previous = signature; }
                    var matches = ignoreViewportMismatch || expected == null || (Json.Number(actual["width"]) == Json.Number(expected["width"]) && Json.Number(actual["height"]) == Json.Number(expected["height"]));
                    if (matches && Environment.TickCount64 - stableSince >= 1000 || watch.Elapsed.TotalMilliseconds >= viewportTimeoutMs)
                    {
                        Json.Write(Path.Combine(absoluteRunDir, "display.json"), new JsonObject { ["actual"] = actual.DeepClone(), ["expectedViewport"] = expected?.DeepClone(), ["display"] = display.DeepClone(), ["ignoreViewportMismatch"] = ignoreViewportMismatch });
                        width = Json.Number(actual["width"]); height = Json.Number(actual["height"]);
                        if (width <= 0 || height <= 0) throw new CrerException(3, "CfT viewport was unavailable");
                        // 診断保存中にも文書が切り替わるため、最後の確認も同じ準備待ちへ戻す。
                        if (Json.Text(await Evaluate("document.readyState")) != "loading") break;
                    }
                    else
                    {
                        await Task.Delay(100, token);
                        continue;
                    }
                }
            }
            catch (CrerException error) when (IsChangingDocument(error))
            {
                // 文書の切替中はサイズやreadyStateの評価先も置き換わる。
            }
            SetStartupStage("document_readiness");
            await WaitForInitialDocument();
            SetStartupStage("viewport_stability");
            actual = null; previous = null; stableSince = Environment.TickCount64;
            watch.Restart();
        }
        actual ??= await ReadDisplay();
        if (expected != null && (width != Json.Number(expected["width"]) || height != Json.Number(expected["height"])))
        {
            var message = $"Viewport mismatch: expected {Json.Number(expected["width"])}x{Json.Number(expected["height"])}, got {width}x{height}";
            if (!ignoreViewportMismatch) throw new CrerException(3, message);
            logger.Warn($"Warning: {message}; continuing because --ignore-viewport-mismatch was specified");
        }
        return actual;
    }

    private async Task WaitForInitialDocument()
    {
        // target の URL が遷移先を示していても、実行コンテキストは about:blank の場合がある。
        // 実際の文書に切り替わってから準備完了と判断する。初回・再利用時の遷移は、
        // viewport の検証とは独立して、外側の起動期限を共有する。
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var state = await Evaluate("({url:location.href,ready:document.readyState})");
                if (Json.Text(state?["url"]) is not ("about:blank" or "data:text/html,") && Json.Text(state?["ready"]) is "interactive" or "complete") return;
            }
            catch (CrerException error) when (IsChangingDocument(error))
            {
                // リダイレクトによって、ポーリングと評価の間に実行コンテキストが置き換わる場合がある。
            }
            await Task.Delay(50, token);
        }
    }

    private static bool IsChangingDocument(CrerException error) => error.Code == 4 &&
        (error.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
         error.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase));

    private void OnNetworkEvent(string method, JsonObject p, string? id)
    {
        if (id != session) return;
        lock (networkLock)
        {
            if (method == "Network.requestWillBeSent")
            {
                requests.Add(Json.Text(p["requestId"])); lastNetworkActivity = Environment.TickCount64;
                networkRequests++;
                if (Json.Text(p["type"]) == "Document") documentRequests++;
            }
            if (method == "Network.responseReceived")
            {
                networkResponses++;
                if (Json.Text(p["type"]) == "Document") documentResponses++;
            }
            if (method is "Network.loadingFinished" or "Network.loadingFailed") { requests.Remove(Json.Text(p["requestId"])); lastNetworkActivity = Environment.TickCount64; }
            if (method == "Network.loadingFinished") networkFinished++;
            if (method == "Network.loadingFailed") networkFailed++;
            if (method is "Network.requestWillBeSent" or "Network.responseReceived" or "Network.loadingFinished" or "Network.loadingFailed")
            {
                // URL、ヘッダー、本文を保存せず、通信の開始・応答・失敗だけを区別する。
                var trace = new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["method"] = method,
                    ["requestId"] = p["requestId"]?.DeepClone(), ["pending"] = requests.Count };
                if (p["type"] != null) trace["type"] = p["type"]!.DeepClone();
                if (method == "Network.responseReceived") trace["status"] = p["response"]?["status"]?.DeepClone();
                if (method == "Network.loadingFailed")
                {
                    var error = Json.Text(p["errorText"]);
                    if (error.Length <= 128 && Regex.IsMatch(error, "^net::ERR_[A-Z0-9_]+$", RegexOptions.CultureInvariant))
                        trace["errorText"] = lastNetworkError = error;
                    trace["canceled"] = p["canceled"]?.DeepClone();
                }
                File.AppendAllText(Path.Combine(absoluteRunDir, "network.ndjson"), trace.ToJsonString() + "\n");
            }
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
            if (templates != null)
            {
                try { await templates.DisposeAsync(); } catch (Exception e) { cleanupErrors.Add($"template matcher cleanup failed: {e.Message}"); }
                templates = null;
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
