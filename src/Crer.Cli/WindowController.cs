using System.Diagnostics;
using System.Text.Json.Nodes;
using Crer.Windows;

namespace Crer;

internal sealed class WindowController : IDisposable
{
    private readonly IWindowInput native;
    private readonly nint original;
    private readonly TimeSpan topmostTimeout, topmostRetryInterval;
    private TimeSpan startupTimeout;
    private Task<Exception>? startupDisconnected;
    private string startupTimeoutScope = "window_control";
    private readonly Stopwatch startupWatch = new();
    private readonly Stopwatch startupFocusWatch = new();
    private static readonly TimeSpan StartupFocusRetryInterval = TimeSpan.FromMilliseconds(250);
    private readonly JsonArray startupAttempts = new();
    private string startupStatus = "not_requested";
    private bool startupFocusRequested, disposed;
    private TopmostResult? lastTopmostResult;
    private Timer? monitor;
    private CancellationTokenSource? monitorCancellation;
    private readonly object gate = new();
    private int pid;
    private bool requireForeground, topmost, focusedOnce, configured, shared, everForeground;
    private bool foregroundOwned;
    private nint foregroundObserved;
    private string mode = "always", focusPolicy = "once", directory = "";
    private int attempts, topmostStatus, foregroundStatus, startupFocusAttempts;
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
        this.topmostTimeout = topmostTimeout ?? TimeSpan.FromSeconds(60);
        this.topmostRetryInterval = topmostRetryInterval ?? TimeSpan.FromMilliseconds(50);
        if (this.topmostTimeout <= TimeSpan.Zero && this.topmostTimeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(topmostTimeout));
        if (this.topmostRetryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(topmostRetryInterval));
        startupTimeout = this.topmostTimeout;
        this.native = native;
        original = native.GetForegroundWindow();
    }

    public async Task Configure(int processId, JsonObject window, CliOptions options, ScenarioLogger log, string runDirectory, bool reuse, string focus,
        CancellationToken cancellation = default, TimeSpan? startupTimeout = null, Task<Exception>? browserDisconnected = null)
    {
        if (startupTimeout is { } requested && requested < TimeSpan.Zero && requested != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
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
            startupFocusAttempts = 0;
            startupFocusWatch.Reset();
            this.startupTimeout = startupTimeout ?? topmostTimeout;
            startupDisconnected = browserDisconnected;
            startupTimeoutScope = startupTimeout.HasValue ? "browser_startup_remaining" : "window_control";
            startupWatch.Restart();
        }
        try
        {
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                ThrowIfStartupFailed();
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
                    topmost = true;
                    ApplyTopmost(recordStartup: true, cancellation: cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    ThrowIfStartupFailed();
                    if (!shared || !focusedOnce)
                    {
                        startupFocusRequested = true;
                        Focus();
                        startupFocusAttempts++;
                        startupFocusWatch.Restart();
                    }
                    cancellation.ThrowIfCancellationRequested();
                    ThrowIfStartupFailed();
                    startupStatus = topmostStatus == 0 ? "succeeded" : mode == "once" ? "retrying" : "failed";
                    SaveStartupDiagnostic();
                    if (topmostStatus != 0 && mode != "once")
                        throw new CrerException(3, $"could not make CfT topmost ({TopmostFailure()})");
                }
                else
                {
                    if (topmost) { clearTopmostStatus = native.SetProcessTopmostDetailed((uint)pid, false, cancellation, TimeSpan.FromSeconds(1)).Status; topmost = false; }
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
                ThrowIfStartupFailed();
                configured = true;
                startupWatch.Stop();
                WriteDiagnostic();
                if (requireForeground && mode == "always" || alpha < 255)
                {
                    var lifetime = new CancellationTokenSource();
                    monitorCancellation = lifetime;
                    monitor = new Timer(_ => Tick(lifetime.Token), null, 250, 250);
                }
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
                ThrowIfStartupFailed();
                if (topmostStatus == 0) { startupStatus = "succeeded"; return; }
                var remaining = startupTimeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : startupTimeout - startupWatch.Elapsed;
                delay = startupTimeout == Timeout.InfiniteTimeSpan ? topmostRetryInterval
                    : remaining <= TimeSpan.Zero ? TimeSpan.Zero
                    : remaining < topmostRetryInterval ? remaining : topmostRetryInterval;
            }
            // 待機中は gate を解放し、起動全体・worker・利用者からの取消しを直ちに受け付ける。
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                ThrowIfStartupFailed();
                // 起動直後の拒否・HWND未検出だけを再試行し、一度取得できた前景は取り直さない。
                if (startupFocusRequested && foregroundStatus != 0 && startupFocusWatch.Elapsed >= StartupFocusRetryInterval)
                {
                    Focus(logFailure: false);
                    startupFocusAttempts++;
                    startupFocusWatch.Restart();
                    cancellation.ThrowIfCancellationRequested();
                    ThrowIfStartupFailed();
                }
                ApplyTopmost(recordStartup: true, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                ThrowIfStartupFailed();
                SaveStartupDiagnostic();
            }
        }
    }

    private void ThrowIfStartupFailed()
    {
        // 無期限でも、所有CfTの切断後に存在しないウィンドウを探し続けない。
        if (startupDisconnected?.IsCompletedSuccessfully == true)
        {
            startupStatus = "failed";
            throw new CrerException(3, $"CfT disconnected during initial window setup: {startupDisconnected.GetAwaiter().GetResult().Message}");
        }
        // 局所予算は once の初期最前面化だけに適用し、他のモードは起動全体の取消しに従う。
        if (!requireForeground || mode != "once") return;
        if (startupTimeout == Timeout.InfiniteTimeSpan || startupWatch.Elapsed < startupTimeout) return;
        startupStatus = "timeout";
        throw new CrerException(3, $"could not make CfT topmost within {startupTimeout.TotalMilliseconds:0.###} ms ({TopmostFailure()})");
    }

    private void ApplyTopmost(bool recordStartup = false, CancellationToken cancellation = default, TimeSpan? confirmationTimeout = null)
    {
        var budget = confirmationTimeout ?? TimeSpan.FromSeconds(1);
        if (recordStartup && mode == "once" && startupTimeout != Timeout.InfiniteTimeSpan)
        {
            var remaining = startupTimeout - startupWatch.Elapsed;
            if (remaining < budget) budget = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        lastTopmostResult = native.SetProcessTopmostDetailed((uint)pid, true, cancellation, budget);
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
        ["foregroundPermissionGranted"] = result.ForegroundPermissionGranted, ["foregroundPermissionError"] = result.ForegroundPermissionError,
        ["recoveryAttempted"] = result.RecoveryAttempted,
        ["recoveryInitialStyle"] = result.RecoveryInitialStyle is { } initialStyle ? $"0x{initialStyle:x}" : null,
        ["recoveryResetSucceeded"] = result.RecoveryResetSucceeded, ["recoveryResetError"] = result.RecoveryResetError,
        ["recoveryReapplySucceeded"] = result.RecoveryReapplySucceeded, ["recoveryReapplyError"] = result.RecoveryReapplyError,
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

    private void Focus(bool logFailure = true)
    {
        foregroundStatus = native.ForegroundProcessWindow((uint)pid);
        foregroundObserved = native.GetForegroundWindow();
        foregroundOwned = native.IsProcessForeground((uint)pid);
        // 要求が拒否されても、起動時の自動前景化ですでに対象が前景の場合を区別する。
        if (foregroundStatus == 0 || foregroundOwned) { focusedOnce = true; everForeground = true; }
        else if (logFailure) logger.Warn($"Warning: Windows did not grant CfT foreground focus (Win32 status {foregroundStatus})");
    }

    public void BeforeStep(CancellationToken cancellation = default)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!requireForeground || mode != "always") return;
            ApplyTopmost(cancellation: cancellation);
            if (!shared || focusPolicy == "before-step") Focus();
            WriteDiagnostic();
            if (topmostStatus != 0) throw new CrerException(3, $"could not keep CfT topmost ({TopmostFailure()})");
        }
    }

    private void Tick(CancellationToken cancellation)
    {
        lock (gate)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (requireForeground && mode == "always")
                {
                    ApplyTopmost(cancellation: cancellation, confirmationTimeout: TimeSpan.FromMilliseconds(250));
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
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { logger.Warn($"Warning: could not maintain CfT window: {error.Message}"); }
        }
    }

    public void Pause()
    {
        var old = Interlocked.Exchange(ref monitor, null);
        if (old == null) return;
        var cancellation = Interlocked.Exchange(ref monitorCancellation, null);
        try
        {
            // 監視中の属性反映待ちも先に止め、Timer の排出を無応答 UI の復帰に依存させない。
            cancellation?.Cancel();
            using var stopped = new ManualResetEvent(false);
            if (old.Dispose(stopped)) stopped.WaitOne();
        }
        finally { cancellation?.Dispose(); }
    }

    public void WriteDiagnostic()
    {
        lock (gate)
        {
            if (directory.Length == 0) return;
            var record = new JsonObject { ["before"] = $"0x{original:x}", ["requested"] = requireForeground, ["mode"] = mode,
                ["pid"] = pid, ["topmostStatus"] = topmostStatus, ["foregroundStatus"] = foregroundStatus, ["topmostAttempts"] = attempts, ["sharedSession"] = shared,
                ["foregroundAcquired"] = everForeground, ["foregroundOwned"] = foregroundOwned, ["foregroundObserved"] = $"0x{foregroundObserved:x}",
                ["topmostStartup"] = new JsonObject { ["status"] = startupStatus,
                    ["timeoutMs"] = startupTimeout == Timeout.InfiniteTimeSpan ? null : startupTimeout.TotalMilliseconds,
                    ["timeoutMode"] = startupTimeout == Timeout.InfiniteTimeSpan ? "infinite" : "finite", ["timeoutScope"] = startupTimeoutScope,
                    ["retryIntervalMs"] = topmostRetryInterval.TotalMilliseconds, ["elapsedMs"] = startupWatch.Elapsed.TotalMilliseconds,
                    ["focusRequested"] = startupFocusRequested, ["focusAttempts"] = startupFocusAttempts, ["attempts"] = startupAttempts.DeepClone() } };
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
                if (topmost) clearTopmostStatus = native.SetProcessTopmostDetailed((uint)pid, false, CancellationToken.None, TimeSpan.FromSeconds(1)).Status;
                if (opacityUsed) native.SetProcessOpacity((uint)pid, 255);
                if (everForeground) { restoreStatus = native.RestoreForegroundWindow(original); restorePerformed = true; }
                WriteDiagnostic();
            }
            finally { native.Dispose(); }
        }
    }
}
