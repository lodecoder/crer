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
