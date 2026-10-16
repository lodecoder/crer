using System.Text.Json.Nodes;
using Crer.Windows;

namespace Crer;

internal sealed class WindowController : IDisposable
{
    private readonly WindowsInput native;
    private readonly nint original;
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

    public WindowController()
    {
        if (!OperatingSystem.IsWindows()) throw new CrerException(3, "playback requires Windows");
        native = new WindowsInput();
        original = native.GetForegroundWindow();
    }

    public Task Configure(int processId, JsonObject window, CliOptions options, ScenarioLogger log, string runDirectory, bool reuse, string focus)
    {
        Pause();
        lock (gate)
        {
            pid = processId; logger = log; directory = runDirectory; shared = reuse; focusPolicy = focus;
            requireForeground = window["foreground"]?.GetValue<bool>() == true;
            mode = Json.Text(window["foreground_mode"], "always");
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
                topmostStatus = native.SetProcessTopmostOnly((uint)pid, true); topmost = true; attempts++; everForeground = true;
                if (!shared || !focusedOnce) Focus();
                if (topmostStatus != 0 && mode == "once") throw new CrerException(3, $"could not make CfT topmost (Win32 status {topmostStatus})");
                if (topmostStatus != 0) logger.Warn($"Warning: initial CfT topmost request failed (Win32 status {topmostStatus}); retrying before steps");
            }
            else
            {
                if (topmost) { clearTopmostStatus = native.SetProcessTopmostOnly((uint)pid, false); topmost = false; }
                // A user may have switched applications while CfT was starting.
                // Restore only focus taken by this owned process.
                if (!configured)
                {
                    restorePerformed = native.IsProcessForeground((uint)pid);
                    restoreStatus = restorePerformed ? native.RestoreForegroundWindow(original) : 0;
                }
            }
            configured = true;
            WriteDiagnostic();
            if (requireForeground && mode == "always" || alpha < 255) monitor = new Timer(_ => Tick(), null, 250, 250);
        }
        return Task.CompletedTask;
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
        if (foregroundStatus != 0) logger.Warn($"Warning: CfT is topmost, but Windows did not grant foreground focus (Win32 status {foregroundStatus})");
    }

    public void BeforeStep()
    {
        lock (gate)
        {
            if (!requireForeground || mode != "always") return;
            topmostStatus = native.SetProcessTopmostOnly((uint)pid, true); attempts++;
            if (!shared || focusPolicy == "before-step") Focus();
            WriteDiagnostic();
            if (topmostStatus != 0) throw new CrerException(3, $"could not keep CfT topmost (Win32 status {topmostStatus})");
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
                    topmostStatus = native.SetProcessTopmostOnly((uint)pid, true); attempts++;
                    if (topmostStatus != 0 && topmostStatus != lastTopmostWarning) logger.Warn($"Warning: could not keep CfT topmost (Win32 status {topmostStatus})");
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
                ["topmostStatus"] = topmostStatus, ["foregroundStatus"] = foregroundStatus, ["topmostAttempts"] = attempts, ["sharedSession"] = shared };
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
            try
            {
                if (topmost) clearTopmostStatus = native.SetProcessTopmostOnly((uint)pid, false);
                if (opacityUsed) native.SetProcessOpacity((uint)pid, 255);
                if (everForeground) { restoreStatus = native.RestoreForegroundWindow(original); restorePerformed = true; }
                WriteDiagnostic();
            }
            finally { native.Dispose(); }
        }
    }
}
