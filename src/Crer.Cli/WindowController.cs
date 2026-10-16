using System.Diagnostics;
using System.Text.Json.Nodes;
using Crer.Windows;

namespace Crer;

internal sealed class WindowController : IDisposable
{
    private readonly IWindowInput native;
    private readonly nint original;
    private readonly TimeSpan topmostTimeout, topmostRetryInterval;
    private readonly Stopwatch startupWatch = new();
    private readonly JsonArray startupAttempts = new();
    private string startupStatus = "not_requested";
    private bool startupFocusRequested, disposed;
    private TopmostResult? lastTopmostResult;
    private Timer? monitor;
    private readonly object gate = new();
    private int pid;
    private bool requireForeground, topmost, focusedOnce, configured, shared, everForeground;
    private string mode = "always", focusPolicy = "once", directory = "";
    private int attempts, topmostStatus, foregroundStatus;
    private int? lastTopmostWarning, lastOpacityWarning, restoreStatus, clearTopmostStatus;
    private uint alpha = 255;
    private bool opacityUsed;
    private bool restorePerformed;
    private ScenarioLogger logger = new("crer");

    public WindowController() : this(CreateInput()) { }

    private static IWindowInput CreateInput()
    {
        if (!OperatingSystem.IsWindows()) throw new CrerException(3, "playback requires Windows");
        return new WindowsInput();
    }

    internal WindowController(IWindowInput native, TimeSpan? topmostTimeout = null, TimeSpan? topmostRetryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        this.topmostTimeout = topmostTimeout ?? TimeSpan.FromSeconds(2);
        this.topmostRetryInterval = topmostRetryInterval ?? TimeSpan.FromMilliseconds(50);
        if (this.topmostTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(topmostTimeout));
        if (this.topmostRetryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(topmostRetryInterval));
        this.native = native;
        original = native.GetForegroundWindow();
    }

    public async Task Configure(int processId, JsonObject window, CliOptions options, ScenarioLogger log, string runDirectory, bool reuse, string focus,
        CancellationToken cancellation = default)
    {
        Pause();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pid = processId; logger = log; directory = runDirectory; shared = reuse; focusPolicy = focus;
            requireForeground = window["foreground"]?.GetValue<bool>() == true;
            mode = Json.Text(window["foreground_mode"], "always");
            startupAttempts.Clear(); lastTopmostResult = null;
            startupStatus = requireForeground ? "pending" : "not_requested";
            startupFocusRequested = false;
            startupWatch.Restart();
        }
        try
        {
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                var opacity = options.OpacityOverride ?? Json.Number(window["opacity"], 1);
                alpha = OpacityAlpha(opacity);
                if (alpha != 255 || opacityUsed)
                {
                    var status = native.SetProcessOpacity((uint)pid, alpha);
                    if (status != 0) throw new CrerException(3, $"could not set CfT opacity (Win32 status {status})");
                    opacityUsed = true;
                }
                if (requireForeground)
                {
                    topmost = true; everForeground = true;
                    startupWatch.Restart();
                    ApplyTopmost(recordStartup: true);
                    cancellation.ThrowIfCancellationRequested();
                    if (!shared || !focusedOnce) { startupFocusRequested = true; Focus(); }
                    startupStatus = topmostStatus == 0 ? "succeeded" : mode == "once" ? "retrying" : "failed";
                    SaveStartupDiagnostic();
                    if (topmostStatus != 0 && mode != "once")
                        logger.Warn($"Warning: initial CfT topmost request failed ({TopmostFailure()}); retrying before steps");
                }
                else
                {
                    if (topmost) { clearTopmostStatus = native.SetProcessTopmostDetailed((uint)pid, false).Status; topmost = false; }
                    // 起動中に利用者が別のアプリへ切り替えた場合、自分のプロセスが取得した前景だけを戻す。
                    if (!configured)
                    {
                        restorePerformed = native.IsProcessForeground((uint)pid);
                        restoreStatus = restorePerformed ? native.RestoreForegroundWindow(original) : 0;
                    }
                }
            }
            // once は初期設定の成功後に再設定しない。起動時の一時的失敗だけを期限内で再試行する。
            if (requireForeground && mode == "once") await RetryStartupTopmost(cancellation);
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                configured = true;
                startupWatch.Stop();
                WriteDiagnostic();
                if (requireForeground && mode == "always" || alpha < 255) monitor = new Timer(_ => Tick(), null, 250, 250);
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate) { startupStatus = "cancelled"; startupWatch.Stop(); SaveStartupDiagnostic(); }
            throw;
        }
        catch
        {
            lock (gate)
            {
                if (startupStatus is "pending" or "retrying") startupStatus = "failed";
                startupWatch.Stop(); SaveStartupDiagnostic();
            }
            throw;
        }
    }

    private async Task RetryStartupTopmost(CancellationToken cancellation)
    {
        while (true)
        {
            TimeSpan delay;
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (topmostStatus == 0) { startupStatus = "succeeded"; return; }
                var remaining = topmostTimeout - startupWatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    startupStatus = "timeout";
                    throw new CrerException(3, $"could not make CfT topmost within {topmostTimeout.TotalMilliseconds:0} ms ({TopmostFailure()})");
                }
                delay = remaining < topmostRetryInterval ? remaining : topmostRetryInterval;
            }
            // 待機中は gate を解放し、起動全体・worker・利用者からの取消しを直ちに受け付ける。
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (startupWatch.Elapsed >= topmostTimeout) continue;
                ApplyTopmost(recordStartup: true);
                SaveStartupDiagnostic();
            }
        }
    }

    private void ApplyTopmost(bool recordStartup = false)
    {
        lastTopmostResult = native.SetProcessTopmostDetailed((uint)pid, true);
        topmostStatus = lastTopmostResult.Status; attempts++;
        if (recordStartup)
        {
            var value = TopmostDiagnostic(lastTopmostResult);
            value["attempt"] = startupAttempts.Count + 1;
            value["elapsedMs"] = startupWatch.Elapsed.TotalMilliseconds;
            startupAttempts.Add((JsonNode)value);
        }
    }

    private string TopmostFailure() => lastTopmostResult is { } result
        ? $"stage: {result.FailureStage}, status: {result.Status}, SetWindowPos error: {result.SetWindowPosError}, style read error: {result.StyleReadError}"
        : $"status: {topmostStatus}";

    private static JsonObject TopmostDiagnostic(TopmostResult result) => new()
    {
        ["status"] = result.Status, ["failureStage"] = result.FailureStage, ["window"] = $"0x{result.Window:x}",
        ["setWindowPosSucceeded"] = result.SetWindowPosSucceeded, ["setWindowPosError"] = result.SetWindowPosError,
        ["extendedStyle"] = result.ExtendedStyle is { } style ? $"0x{style:x}" : null, ["styleReadError"] = result.StyleReadError,
    };

    private void SaveStartupDiagnostic()
    {
        // 診断の書き込み失敗で、元の設定失敗や取消しを置き換えない。
        try { WriteDiagnostic(); } catch { }
    }

    public static uint OpacityAlpha(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1) throw new CrerException(2, "browser.window.opacity must be a finite number from 0 to 1");
        return (uint)Math.Floor(opacity * 255 + .5);
    }

    private void Focus()
    {
        foregroundStatus = native.ForegroundProcessWindow((uint)pid);
        focusedOnce = true;
        if (foregroundStatus != 0) logger.Warn($"Warning: Windows did not grant CfT foreground focus (Win32 status {foregroundStatus})");
    }

    public void BeforeStep()
    {
        lock (gate)
        {
            if (!requireForeground || mode != "always") return;
            ApplyTopmost();
            if (!shared || focusPolicy == "before-step") Focus();
            WriteDiagnostic();
            if (topmostStatus != 0) throw new CrerException(3, $"could not keep CfT topmost ({TopmostFailure()})");
        }
    }

    private void Tick()
    {
        lock (gate)
        {
            try
            {
                if (requireForeground && mode == "always")
                {
                    ApplyTopmost();
                    if (topmostStatus != 0 && topmostStatus != lastTopmostWarning) logger.Warn($"Warning: could not keep CfT topmost ({TopmostFailure()})");
                    lastTopmostWarning = topmostStatus;
                }
                if (alpha < 255)
                {
                    var status = native.SetProcessOpacity((uint)pid, alpha);
                    if (status != 0 && status != lastOpacityWarning) logger.Warn($"Warning: could not maintain CfT opacity (Win32 status {status})");
                    lastOpacityWarning = status;
                }
            }
            catch (Exception error) { logger.Warn($"Warning: could not maintain CfT window: {error.Message}"); }
        }
    }

    public void Pause()
    {
        var old = Interlocked.Exchange(ref monitor, null);
        if (old == null) return;
        using var stopped = new ManualResetEvent(false);
        if (old.Dispose(stopped)) stopped.WaitOne();
    }

    public void WriteDiagnostic()
    {
        lock (gate)
        {
            if (directory.Length == 0) return;
            var record = new JsonObject { ["before"] = $"0x{original:x}", ["requested"] = requireForeground, ["mode"] = mode,
                ["pid"] = pid, ["topmostStatus"] = topmostStatus, ["foregroundStatus"] = foregroundStatus, ["topmostAttempts"] = attempts, ["sharedSession"] = shared,
                ["topmostStartup"] = new JsonObject { ["status"] = startupStatus, ["timeoutMs"] = topmostTimeout.TotalMilliseconds,
                    ["retryIntervalMs"] = topmostRetryInterval.TotalMilliseconds, ["elapsedMs"] = startupWatch.Elapsed.TotalMilliseconds,
                    ["focusRequested"] = startupFocusRequested, ["attempts"] = startupAttempts.DeepClone() } };
            if (lastTopmostResult != null) record["topmostLastAttempt"] = TopmostDiagnostic(lastTopmostResult);
            if (restoreStatus != null) record["restoreStatus"] = restoreStatus;
            record["restorePerformed"] = restorePerformed;
            if (clearTopmostStatus != null) record["clearTopmostStatus"] = clearTopmostStatus;
            Json.Write(Path.Combine(directory, "foreground.json"), record);
            Json.Write(Path.Combine(directory, "opacity.json"), new JsonObject { ["alpha"] = alpha, ["requested"] = alpha / 255d, ["applied"] = opacityUsed });
        }
    }

    public void Dispose()
    {
        Pause();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (topmost) clearTopmostStatus = native.SetProcessTopmostDetailed((uint)pid, false).Status;
                if (opacityUsed) native.SetProcessOpacity((uint)pid, 255);
                if (everForeground) { restoreStatus = native.RestoreForegroundWindow(original); restorePerformed = true; }
                WriteDiagnostic();
            }
            finally { native.Dispose(); }
        }
    }
}
