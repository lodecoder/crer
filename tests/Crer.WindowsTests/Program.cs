using System.Diagnostics;
using Crer.Windows;

static void Equal(int expected, int actual, string label)
{
    if (expected != actual) throw new Exception($"{label}: expected {expected}, got {actual}");
}

static void Disposed(Action action, string label)
{
    try { action(); }
    catch (ObjectDisposedException) { return; }
    throw new Exception($"{label}: expected ObjectDisposedException");
}

static async Task WaitForStop(IInputRecorder input, string label)
{
    var timer = Stopwatch.StartNew();
    while (input.IsRunning && timer.ElapsedMilliseconds < 1000)
        await Task.Delay(5);
    Equal(0, input.IsRunning ? 1 : 0, label);
}

if (!OperatingSystem.IsWindows())
{
    try { using var unsupported = new WindowsInput(); }
    catch (PlatformNotSupportedException)
    {
        Console.WriteLine("Windows input platform guard test passed; Windows API tests skipped.");
        return;
    }
    throw new Exception("WindowsInput must reject non-Windows platforms");
}

using var recorder = new WindowsInput();
await TopmostResponsivenessTests.Run();
IInputRecorder input = recorder;
if (input.QpcFrequencyHz == 0) throw new Exception("QPC frequency must be positive");
Equal(87, recorder.SetProcessOpacity(0, 128), "opacity rejects missing process");
Equal(87, recorder.SetProcessOpacity((uint)Environment.ProcessId, 256), "opacity rejects invalid alpha");
Equal(87, recorder.SetProcessTopmostOnly(0, true), "topmost rejects missing process");
var invalidProcess = recorder.SetProcessTopmostDetailed(0, true);
if (invalidProcess.Status != 87 || invalidProcess.FailureStage != "invalid_process" || invalidProcess.Window != 0
    || invalidProcess.SetWindowPosSucceeded is not null || invalidProcess.ExtendedStyle is not null)
    throw new Exception("invalid process diagnostics must not report an attempted Windows API call");
var invalidWindow = InputBridge.ApplyTopmost(new nint(-123), true);
if (invalidWindow.FailureStage != "set_window_pos" || invalidWindow.SetWindowPosSucceeded != false
    || invalidWindow.SetWindowPosError == 0 || invalidWindow.Status != invalidWindow.SetWindowPosError
    || invalidWindow.ExtendedStyle is not null || invalidWindow.StyleReadError != 0)
    throw new Exception("invalid HWND must preserve the real SetWindowPos failure instead of reporting a style mismatch");
if (invalidWindow.RecoveryAttempted || invalidWindow.RecoveryResetSucceeded is not null || invalidWindow.RecoveryReapplySucceeded is not null)
    throw new Exception("無効な HWND の API 失敗時には最前面属性の回復処理を行わない");
Equal(87, recorder.ForegroundProcessWindow(0), "foreground rejects missing process");
Equal(87, recorder.RestoreForegroundWindow(0), "restore rejects missing window");
InputRegressionTests.CapsLock();
if (Environment.GetEnvironmentVariable("CRER_TEST_WINDOWS") == "1"
    || Environment.GetEnvironmentVariable("CRER_TEST_WINDOWS_FOREGROUND") == "1") await ForegroundRequestTests.Run();
if (Environment.GetEnvironmentVariable("CRER_TEST_WINDOWS") == "1")
{
    TopmostTests.Run();
    InputRegressionTests.ContentWindows();
}
if (Environment.GetEnvironmentVariable("CRER_TEST_CHROME") is { Length: > 0 } chrome) await BrowserInputRegressionTests.Run(chrome);

// Win32 RAWMOUSE has two padding bytes before the button union (offset 4).
var packet = new byte[24];
packet[4] = 1;
InputBridge.TestMousePacket(packet, -300, 500);
packet[4] = 2;
InputBridge.TestMousePacket(packet, -300, 500);
var events = new InputEvent[1];
Equal(0, input.Read(Span<InputEvent>.Empty), "empty read preserves queued events");
foreach (var kind in new[] { 2, 3 })
{
    Equal(1, input.Read(events), "mouse button read");
    Equal(kind, (int)events[0].Kind, "mouse button kind");
    Equal(-300, events[0].X, "physical x");
    Equal(500, events[0].Y, "physical y");
}
Equal(0, input.Read(events), "no duplicate click event");
packet[4] = 0;
packet[5] = 4; // RI_MOUSE_WHEEL
packet[6] = 0x88;
packet[7] = 0xff; // -120, encoded as unsigned 16 bits for normalize
InputBridge.TestMousePacket(packet, 1, 2);
Equal(1, input.Read(events), "wheel read");
Equal(6, (int)events[0].Kind, "wheel kind");
Equal(65416, (int)events[0].Data, "wheel delta encoding");

for (var session = 0; session < 2; session++)
{
    Equal(0, input.Start((uint)Environment.ProcessId), "start");
    Equal(183, input.Start((uint)Environment.ProcessId), "duplicate start");
    using (var second = new WindowsInput())
    {
        Equal(183, second.Start((uint)Environment.ProcessId), "duplicate start across instances");
        Equal(0, second.Stop(), "non-owner stop succeeds without stopping owner");
        Equal(1, input.IsRunning ? 1 : 0, "owner still running after non-owner stop");
    }
    await Task.Delay(25);
    Equal(1, input.IsRunning ? 1 : 0, "running after disposing non-owner");
    Equal(1168, input.GetContentRect().Status, "missing browser window");
    Equal(0, input.Stop(), "stop");
}

Equal(0, input.Start((uint)Environment.ProcessId), "末尾回収の記録開始");
await Task.Delay(25);
Array.Clear(packet);
packet[4] = 1;
InputBridge.TestMousePacket(packet, 100, 200);
packet[4] = 2;
InputBridge.TestMousePacket(packet, 100, 200);
Equal(0, input.Stop(), "未読入力を残した記録停止");
var stoppedEvents = new InputEvent[8];
Equal(2, input.Read(stoppedEvents), "停止後も未読クリックを回収できる");
Equal(2, (int)stoppedEvents[0].Kind, "停止後のmousedown");
Equal(3, (int)stoppedEvents[1].Kind, "停止後のmouseup");
Equal(0, input.Read(stoppedEvents), "停止後のキューが空になる");
Equal(1168, input.GetContentRect().Status, "停止後もbounds取得APIを使用できる");

Equal(0, input.Start((uint)Environment.ProcessId), "overflow start");
await Task.Delay(25);
for (var index = 0; index <= 8192; index++) InputBridge.TestPush();
await WaitForStop(input, "overflow stopped");
Equal(111, input.LastError, "overflow error");
Equal(111, input.Stop(), "overflow stop status");

InputBridge.TestFailHookInitialization(1234);
Equal(0, input.Start((uint)Environment.ProcessId), "hook failure start");
await WaitForStop(input, "hook failure stopped");
Equal(1234, input.LastError, "hook failure error");
Equal(1234, input.Stop(), "hook failure stop status");

Equal(0, input.Start((uint)Environment.ProcessId), "timeout start");
await Task.Delay(25);
InputBridge.TestForceStopTimeout();
Equal(1460, input.Stop(), "stop timeout");
Equal(1, input.IsRunning ? 1 : 0, "running after injected timeout");
Equal(0, input.Stop(), "cleanup after timeout");

var disposing = new WindowsInput();
Equal(0, disposing.Start((uint)Environment.ProcessId), "dispose start");
await Task.Delay(25);
InputBridge.TestForceStopTimeout();
disposing.Dispose();
Equal(1, disposing.IsRunning ? 1 : 0, "dispose timeout permits shutdown retry");
disposing.Dispose();
disposing.Dispose();
Disposed(() => disposing.Start((uint)Environment.ProcessId), "disposed recorder start");
Disposed(() => disposing.GetForegroundWindow(), "disposed window API");
Equal(0, input.IsRunning ? 1 : 0, "dispose stops recording");

using (var predecessor = new WindowsInput())
using (var successor = new WindowsInput())
{
    Equal(0, predecessor.Start((uint)Environment.ProcessId), "previous owner start");
    await Task.Delay(25);
    for (var index = 0; index <= 8192; index++) InputBridge.TestPush();
    await WaitForStop(predecessor, "previous owner overflow stopped");
    Equal(111, predecessor.LastError, "previous owner overflow error");
    var timer = Stopwatch.StartNew();
    int status;
    do
    {
        status = successor.Start((uint)Environment.ProcessId);
        if (status == 183) await Task.Delay(5);
    } while (status == 183 && timer.ElapsedMilliseconds < 1000);
    Equal(0, status, "next owner starts after automatic stop");
    predecessor.Dispose();
    await Task.Delay(25);
    Equal(1, successor.IsRunning ? 1 : 0, "previous owner dispose preserves current recording");
    Equal(0, successor.LastError, "next owner clears old overflow error");
    Equal(0, successor.Stop(), "next owner cleanup");
}

Console.WriteLine("Windows recorder lifecycle and direct API tests passed.");
