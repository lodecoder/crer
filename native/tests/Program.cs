using System.Diagnostics;

static void Equal(int expected, int actual, string label)
{
    if (expected != actual) throw new Exception($"{label}: expected {expected}, got {actual}");
}

static async Task WaitForStop(string label)
{
    var timer = Stopwatch.StartNew();
    while (InputBridge.TestIsRunning() != 0 && timer.ElapsedMilliseconds < 1000)
        await Task.Delay(5);
    Equal(0, InputBridge.TestIsRunning(), label);
}

Equal(2, (int)InputBridge.TestVersion(), "ABI version");
if (Environment.GetEnvironmentVariable("CRER_TEST_WINDOWS") == "1") TopmostTests.Run();

// Win32 RAWMOUSE has two padding bytes before the button union (offset 4).
var packet = new byte[24];
packet[4] = 1;
InputBridge.TestMousePacket(packet, -300, 500);
packet[4] = 2;
InputBridge.TestMousePacket(packet, -300, 500);
foreach (var kind in new[] { 2, 3 })
{
    if (!InputBridge.TestRead(out var input)) throw new Exception("missing mouse button event");
    Equal(kind, (int)input.Kind, "mouse button kind");
    Equal(-300, input.X, "physical x");
    Equal(500, input.Y, "physical y");
}
if (InputBridge.TestRead(out _)) throw new Exception("duplicate click event");
packet[4] = 0;
packet[5] = 4; // RI_MOUSE_WHEEL
packet[6] = 0x88;
packet[7] = 0xff; // -120, encoded as unsigned 16 bits for normalize
InputBridge.TestMousePacket(packet, 1, 2);
if (!InputBridge.TestRead(out var wheel)) throw new Exception("missing wheel event");
Equal(6, (int)wheel.Kind, "wheel kind");
Equal(65416, (int)wheel.Data, "wheel delta encoding");

for (var session = 0; session < 2; session++)
{
    Equal(0, InputBridge.TestStart((uint)Environment.ProcessId), "start");
    Equal(183, InputBridge.TestStart((uint)Environment.ProcessId), "duplicate start");
    await Task.Delay(25);
    Equal(1, InputBridge.TestIsRunning(), "running");
    Equal(0, InputBridge.TestStop(), "stop");
}

Equal(0, InputBridge.TestStart((uint)Environment.ProcessId), "overflow start");
await Task.Delay(25);
for (var index = 0; index <= 8192; index++) InputBridge.TestPush();
await WaitForStop("overflow stopped");
Equal(111, InputBridge.TestError(), "overflow error");
Equal(111, InputBridge.TestStop(), "overflow stop status");

InputBridge.TestFailHookInitialization(1234);
Equal(0, InputBridge.TestStart((uint)Environment.ProcessId), "hook failure start");
await WaitForStop("hook failure stopped");
Equal(1234, InputBridge.TestError(), "hook failure error");
Equal(1234, InputBridge.TestStop(), "hook failure stop status");

Equal(0, InputBridge.TestStart((uint)Environment.ProcessId), "timeout start");
await Task.Delay(25);
InputBridge.TestForceStopTimeout();
Equal(1460, InputBridge.TestStop(), "stop timeout");
Equal(1, InputBridge.TestIsRunning(), "running after injected timeout");
Equal(0, InputBridge.TestStop(), "cleanup after timeout");

Console.WriteLine("Native recorder lifecycle tests passed.");
