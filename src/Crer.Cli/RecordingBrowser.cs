using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Crer.Windows;

namespace Crer;

public static partial class Recorder
{
    public static async Task<int> Run(CliOptions options, string chrome, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new CrerException(3, "record requires Windows");
        if (options.File is null) throw new CrerException(2, "raw recording path is required immediately after command");
        var output = options.File;
        var size = options.Value("--content-size") is { } sizeOption ? CliOptions.Point(sizeOption, "--content-size") : (860d, 560d);
        var content = new NormalizePoint(size.Item1, size.Item2);
        var position = options.Position is { } p ? new NormalizeWindowBounds(Json.Number(p["left"]), Json.Number(p["top"])) : null;
        var runDir = Path.GetFullPath(RunArtifacts.CreateRunPath(options.Value("--name") ?? "record", options));
        Directory.CreateDirectory(runDir);
        var configuredProfile = options.ProfileDir;
        var profile = configuredProfile is null ? Path.Combine(runDir, "profile") : Profiles.PersistentDirectory(configuredProfile);
        Directory.CreateDirectory(profile);
        using var profileLock = Profiles.Acquire(profile);
        Profiles.Prepare(profile);
        using var input = new WindowsInput();
        await using var browser = new RecordingBrowser(chrome, profile);
        browser.Start(options.Value("--url") ?? "about:blank");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var monitorLifetime = new CancellationTokenSource();
        var duration = options.Value("--duration-ms");
        if (duration is not null && CliOptions.TryNumber(duration, out var milliseconds) && milliseconds > 0)
        {
            if (milliseconds > int.MaxValue) Console.Error.WriteLine("Warning: --duration-ms exceeds 2147483647; timeout duration was set to 1.");
            stop.CancelAfter(TimeSpan.FromMilliseconds(DurationDelayMilliseconds(milliseconds)));
        }
        var stopFile = options.Value("--stop-file");
        var terminal = stopFile is null && duration is null && !Console.IsInputRedirected;
        if (terminal) Console.Error.WriteLine("Recording. Press Enter or Ctrl+C to stop.");
        var monitor = Monitor();
        async Task Monitor()
        {
            try
            {
                while (!monitorLifetime.IsCancellationRequested && !stop.IsCancellationRequested)
                {
                    if (!browser.IsAlive) { stop.Cancel(); break; }
                    if (stopFile is not null)
                    {
                        try
                        {
                            _ = File.GetAttributes(stopFile);
                            stop.Cancel();
                            break;
                        }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { Console.Error.WriteLine($"Warning: could not check stop file: {error.Message}"); }
                    }
                    if (terminal && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Enter) { stop.Cancel(); break; }
                    }
                    await Task.Delay(100, monitorLifetime.Token);
                }
            }
            catch (OperationCanceledException) when (monitorLifetime.IsCancellationRequested) { }
        }
        try
        {
            await browser.SetupPage(content, position, token);
            await RecordRaw(input, (uint)browser.ProcessId, output, new RecordRawOptions
            {
                Viewport = browser.Viewport,
                MarkerClick = duration is null ? browser.MarkerClick : null,
                ReadViewport = browser.ReadViewport,
                IsTargetAlive = () => browser.IsAlive,
                WindowBounds = browser.WindowBounds,
                RequestedContent = content,
                ProfileDir = configuredProfile is null ? null : profile,
            }, stop.Token);
            return 0;
        }
        finally
        {
            monitorLifetime.Cancel();
            await monitor;
        }
    }

    // Deno's timer uses a signed 32-bit delay and warns/uses 1 ms on overflow.
    // Duration 0 remains the existing explicit no-timer option, handled by the caller.
    public static double DurationDelayMilliseconds(double duration) => duration > int.MaxValue ? 1 : Math.Truncate(duration);
}

/// <summary>The recorder owns a dedicated CfT process/profile and its browser-scoped CDP socket.</summary>
internal sealed class RecordingBrowser(string chrome, string profile) : IAsyncDisposable
{
    private Process? process;
    private Cdp? cdp;
    private string? session;
    private int windowId;
    private int port;
    private string initialUrl = "about:blank";
    private Task? stdout, stderr;
    public NormalizePoint Viewport { get; private set; } = new(0, 0);
    public NormalizeWindowBounds? WindowBounds { get; private set; }
    public int ProcessId => process?.Id ?? throw new InvalidOperationException("recording browser was not started");
    public bool IsAlive => process is not null && !process.HasExited;

    public void Start(string url)
    {
        initialUrl = url;
        var path = Path.GetFullPath(chrome);
        if (!File.Exists(path)) throw new CrerException(3, $"Chrome for Testing does not exist: {path}");
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[]
        {
            $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={profile}",
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-infobars", "--disable-save-password-bubble",
            "--force-device-scale-factor=1", "--disable-backgrounding-occluded-windows",
            "--disable-features=Translate,TranslateUI,PasswordManagerOnboarding", "--app=data:text/html,",
        }) start.ArgumentList.Add(argument);
        ChromeEnvironment.Apply(start);
        try
        {
            lock (BackgroundProcessLauncher.CreationGate)
                process = Process.Start(start) ?? throw new CrerException(3, "CfT process could not start");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        { throw new CrerException(3, $"CfT process could not start: {error.Message}"); }
        stdout = Drain(process.StandardOutput);
        stderr = Drain(process.StandardError);
    }

    private static async Task Drain(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) != 0) { } }
        catch (IOException) { }
    }

    public async Task SetupPage(NormalizePoint content, NormalizeWindowBounds? position, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var endpoint = $"http://127.0.0.1:{port}";
        JsonArray? targets = null;
        string? browserUrl = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            token.ThrowIfCancellationRequested();
            if (!IsAlive) throw new CrerException(3, "CfT exited during recording startup");
            try
            {
                var version = JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/version", token))!.AsObject();
                browserUrl = Json.Text(version["webSocketDebuggerUrl"]);
                if (!Uri.TryCreate(browserUrl, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || uri.Host != "127.0.0.1"
                    || uri.Port != port || !uri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    throw new CrerException(3, "CfT returned an unexpected recording CDP endpoint");
                targets = JsonNode.Parse(await http.GetStringAsync(endpoint + "/json/list", token))!.AsArray();
                if (targets.OfType<JsonObject>().Any(candidate => Json.Text(candidate["type"]) == "page")) break;
            }
            catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !token.IsCancellationRequested) { }
            await Task.Delay(50, token);
        }
        if (targets is null || browserUrl is null) throw new CrerException(3, "CfT recording startup timed out waiting for its dedicated CDP endpoint");
        if (!IsAlive) throw new CrerException(3, "CfT exited during recording startup");
        var target = targets.OfType<JsonObject>().FirstOrDefault(candidate => Json.Text(candidate["type"]) == "page")
            ?? throw new CrerException(3, "CfT did not expose a recording page target");
        cdp = new Cdp();
        await cdp.Open(browserUrl, token);
        session = Json.Text((await BrowserCall("Target.attachToTarget", new() { ["targetId"] = Json.Text(target["id"]), ["flatten"] = true }, token))["sessionId"]);
        windowId = (int)Json.Number((await BrowserCall("Browser.getWindowForTarget", new() { ["targetId"] = Json.Text(target["id"]) }, token))["windowId"]);
        var bounds = (await BrowserCall("Browser.getWindowBounds", new() { ["windowId"] = windowId }, token))["bounds"]!;
        await SetBounds(new() { ["windowState"] = "normal", ["width"] = bounds["width"]!.DeepClone(), ["height"] = bounds["height"]!.DeepClone() }, token);
        if (position is not null) await SetBounds(new() { ["left"] = position.Left, ["top"] = position.Top }, token);
        await PageCall("Page.enable", new(), token);
        await PageCall("Runtime.enable", new(), token);
        // サイトが初期サイズを参照する前に、空文書で実ウィンドウと renderer の寸法を揃える。
        await SynchronizeContent(content, token);
        var navigation = await PageCall("Page.navigate", new() { ["url"] = initialUrl }, token, 10000);
        if (Json.Text(navigation["errorText"]) is { Length: > 0 } navigationError)
            throw new CrerException(3, $"recording navigation failed: {navigationError}");
        watch.Restart();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var state = await Evaluate("({url:location.href,state:document.readyState})", token);
                if ((initialUrl == "data:text/html," || Json.Text(state?["url"]) != "data:text/html,")
                    && Json.Text(state?["state"]) is "interactive" or "complete") break;
            }
            catch (CrerException error) when (ChangingDocument(error)) { }
            if (watch.Elapsed >= TimeSpan.FromSeconds(10)) throw new CrerException(3, "CfT page did not finish loading");
            await Task.Delay(50, token);
        }
        Viewport = await SynchronizeContent(content, token);
        if (Viewport != content) Console.Error.WriteLine($"Note: page scrollbar reduced the CSS viewport to {Viewport.X}x{Viewport.Y}; recording that effective coordinate space.");
        var currentBounds = (await BrowserCall("Browser.getWindowBounds", new() { ["windowId"] = windowId }, token))["bounds"]!;
        WindowBounds = new(Json.Number(currentBounds["left"]), Json.Number(currentBounds["top"]));
        await Evaluate(MarkerScript, token);
    }

    public async Task<NormalizePoint> ReadViewport(CancellationToken token)
        => (await ReadViewportMeasurement(token)).Viewport;

    private async Task<(NormalizePoint Viewport, NormalizePoint Content, NormalizePoint Visual)> ReadViewportMeasurement(CancellationToken token)
    {
        var metricsTask = PageCall("Page.getLayoutMetrics", new(), token);
        var displayTask = Evaluate("({dpr:devicePixelRatio,scale:visualViewport?.scale ?? 1,innerWidth,innerHeight,width:visualViewport?.width ?? innerWidth,height:visualViewport?.height ?? innerHeight})", token);
        await Task.WhenAll(metricsTask, displayTask);
        var display = await displayTask;
        var dpr = Json.Number(display?["dpr"]);
        var scale = Json.Number(display?["scale"]);
        if (dpr != 1 || scale != 1)
            throw new CrerException(3, $"recording requires 100% browser zoom (DPR 1, visual scale 1); got DPR {dpr}, visual scale {scale}");
        var viewport = (await metricsTask)["cssVisualViewport"];
        var width = Json.Number(viewport?["clientWidth"]);
        var height = Json.Number(viewport?["clientHeight"]);
        if (width <= 0 || height <= 0) throw new CrerException(3, "CfT recording viewport was unavailable");
        return (new(width, height), new(Json.Number(display?["innerWidth"]), Json.Number(display?["innerHeight"])),
            new(Json.Number(display?["width"]), Json.Number(display?["height"])));
    }

    internal async Task<NormalizePoint> SynchronizeContent(NormalizePoint content, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        NormalizePoint? previous = null;
        var stable = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await SetContent(content, deadline.Token);
                await Task.Delay(100, deadline.Token);
                try
                {
                    var measured = await ReadViewportMeasurement(deadline.Token);
                    // スクロールバーによる縮小だけを許可し、起動時の古い巨大 viewport を受理しない。
                    var aligned = measured.Content == content && measured.Viewport == measured.Visual
                        && measured.Viewport.X <= content.X && measured.Viewport.Y <= content.Y;
                    if (aligned)
                    {
                        if (previous == measured.Viewport)
                        {
                            if (stable.Elapsed >= TimeSpan.FromSeconds(1)) return measured.Viewport;
                        }
                        else { previous = measured.Viewport; stable.Restart(); }
                    }
                    else { previous = null; stable.Restart(); }
                    // 背面の renderer で遅延した resize を compositor の読取同期でも進める。
                    if (!aligned) await PageCall("Page.captureScreenshot", new() { ["format"] = "png" }, deadline.Token);
                }
                catch (CrerException error) when (ChangingDocument(error)) { previous = null; stable.Restart(); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new CrerException(3, $"CfT recording content size did not synchronize to {content.X}x{content.Y} before calibration"); }
    }

    private static bool ChangingDocument(CrerException error) => error.Code == 4 &&
        (error.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
         || error.Message.Contains("Cannot find default execution context", StringComparison.OrdinalIgnoreCase)
         || error.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase));

    public async Task<NormalizePoint?> MarkerClick(CancellationToken token)
    {
        var value = await Evaluate("globalThis.__crerCalibrationPoint", token);
        return value is JsonObject point && point["x"] is not null && point["y"] is not null
            ? new(Json.Number(point["x"]), Json.Number(point["y"])) : null;
    }

    private Task<JsonObject> SetBounds(JsonObject bounds, CancellationToken token) => BrowserCall("Browser.setWindowBounds", new() { ["windowId"] = windowId, ["bounds"] = bounds }, token);
    private Task<JsonObject> SetContent(NormalizePoint content, CancellationToken token) => BrowserCall("Browser.setContentsSize", new() { ["windowId"] = windowId, ["width"] = content.X, ["height"] = content.Y }, token);
    private Task<JsonObject> BrowserCall(string method, JsonObject parameters, CancellationToken token) => cdp!.Call(method, parameters, token: token, timeoutMs: 2000);
    private Task<JsonObject> PageCall(string method, JsonObject parameters, CancellationToken token, int timeout = 2000) => cdp!.Call(method, parameters, session, token, timeout);
    private async Task<JsonNode?> Evaluate(string expression, CancellationToken token)
    {
        var result = await PageCall("Runtime.evaluate", new() { ["expression"] = expression, ["returnByValue"] = true }, token);
        if (result["exceptionDetails"] is not null) throw new CrerException(3, "recording page evaluation failed");
        return result["result"]?["value"]?.DeepClone();
    }

    public async ValueTask DisposeAsync()
    {
        if (cdp is not null)
        {
            if (IsAlive) try { await cdp.Call("Browser.close", timeoutMs: 1000); } catch (CrerException) { }
            await cdp.DisposeAsync();
        }
        if (process is not null)
        {
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    try { process.Kill(true); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
                }
            }
            if (stdout is not null) await stdout.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (stderr is not null) await stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            process.Dispose();
        }
    }

    private const string MarkerScript = """
        (() => {
          const id = "__crer_record_calibration_marker__";
          document.getElementById(id)?.remove();
          globalThis.__crerCalibrationPoint = undefined;
          const marker = document.createElement("div");
          marker.id = id;
          marker.setAttribute("aria-hidden", "true");
          marker.style.cssText = "all:initial;display:block!important;position:fixed!important;left:0!important;top:0!important;width:64px!important;height:64px!important;margin:0!important;padding:0!important;border:0!important;background:#ff00ff!important;z-index:2147483647!important;pointer-events:auto!important;cursor:crosshair!important;";
          marker.addEventListener("pointerup", (event) => {
            globalThis.__crerCalibrationPoint = { x: event.clientX, y: event.clientY };
            marker.remove();
          }, { once: true });
          document.documentElement.append(marker);
        })()
        """;
}
