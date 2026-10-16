using System.Text.Json.Nodes;
using Crer;
using Crer.Windows;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}");
}
async Task Reject(Func<Task> action, string diagnostic)
{
    try { await action(); }
    catch (CrerException error) when (error.Code == 3 && error.Message.Contains(diagnostic, StringComparison.Ordinal)) { return; }
    throw new Exception($"Expected native environment failure containing '{diagnostic}'");
}
async Task Temp(Func<string, Task> action)
{
    var directory = Path.Combine(Path.GetTempPath(), $"crer-record-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try { await action(Path.Combine(directory, "raw.ndjson")); }
    finally { Directory.Delete(directory, true); }
}
RecordRawOptions Options() => new() { PollInterval = TimeSpan.FromMilliseconds(1), Output = new StringWriter(), Error = new StringWriter() };
InputEvent Event(uint kind, uint data = 0, int x = 100, int y = 200, ulong qpc = 1) => new() { Kind = kind, Data = data, X = x, Y = y, Qpc = qpc };
JsonObject Metadata(string path) => JsonNode.Parse(File.ReadAllText(path + ".meta.json"))!.AsObject();
JsonArray Events(string path) => new(File.ReadAllLines(path).Where(line => line.Length != 0).Select(line => JsonNode.Parse(line)).ToArray());

await Test("QPC uint64 and input data survive NDJSON", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop, [Event(7, uint.MaxValue, -500, 400, ulong.MaxValue)]);
    await Recorder.RecordRaw(fake, 42, path, Options(), stop.Token);
    var input = Events(path)[0]!;
    Equal(input["qpc"]!.GetValue<string>(), "18446744073709551615");
    Equal(input["data"]!.GetValue<uint>(), uint.MaxValue);
    Equal(input["x"]!.GetValue<int>(), -500);
    Equal(fake.StartPid, 42u);
    Equal(fake.StopCalls, 1);
    Equal(Metadata(path)["qpc_frequency_hz"]!.GetValue<string>(), "10000000");
}));
await Test("failed native start never stops an unowned hook", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop) { StartStatus = 183 };
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options(), stop.Token), "Raw Input start failed: 183");
    Equal(fake.StartCalls, 1);
    Equal(fake.StopCalls, 0);
}));
await Test("pre-cancelled recording writes a valid empty raw file and metadata then stops", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    var fake = new FakeRecorder(stop);
    await Recorder.RecordRaw(fake, 42, path, Options(), stop.Token);
    Equal(File.ReadAllText(path), "");
    Equal(Metadata(path)["incomplete"], null);
    Equal(fake.StopCalls, 1);
}));
await Test("recording preserves append behavior without overwriting earlier packets", () => Temp(async path =>
{
    const string first = "{\"qpc\":\"0\",\"x\":0,\"y\":0,\"kind\":1,\"data\":0}\n";
    File.WriteAllText(path, first);
    using var stop = new CancellationTokenSource();
    await Recorder.RecordRaw(new FakeRecorder(stop, [Event(9, 'a')]), 42, path, Options(), stop.Token);
    Equal(File.ReadAllText(path).StartsWith(first, StringComparison.Ordinal), true);
    Equal(Events(path).Count, 2);
}));
await Test("raw metadata retains measured viewport, explicit scale, requested content, profile and position", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var options = Options() with { Viewport = new(1315, 1200), RequestedContent = new(1330, 1200), WindowBounds = new(-500, 20), ProfileDir = ".crer/profiles/demo" };
    await Recorder.RecordRaw(new FakeRecorder(stop), 42, path, options, stop.Token);
    var metadata = Metadata(path);
    Equal(JsonNode.DeepEquals(metadata["css_viewport"], JsonNode.Parse("""{"x":1315,"y":1200}""")), true);
    Equal(JsonNode.DeepEquals(metadata["screen_pixels_per_css_pixel"], JsonNode.Parse("""{"x":1,"y":1}""")), true);
    Equal(JsonNode.DeepEquals(metadata["requested_content"], JsonNode.Parse("""{"width":1330,"height":1200}""")), true);
    Equal(JsonNode.DeepEquals(metadata["window_bounds"], JsonNode.Parse("""{"left":-500,"top":20}""")), true);
    Equal(metadata["profile_dir"]!.GetValue<string>(), ".crer/profiles/demo");
}));
await Test("marker consumes pre-calibration packets and retains the following interactions", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop, [Event(1), Event(2), Event(3), Event(2, x: 300, y: 400), Event(3, x: 300, y: 400)]);
    var options = Options() with { Viewport = new(800, 600), MarkerClick = _ => Task.FromResult<NormalizePoint?>(new(4, 8)) };
    await Recorder.RecordRaw(fake, 42, path, options, stop.Token);
    Equal(Events(path).Count, 2);
    Equal(JsonNode.DeepEquals(Metadata(path)["marker_calibration"], JsonNode.Parse("""
        {"screenClick":{"x":100,"y":200},"cssPoint":{"x":4,"y":8}}
        """)), true);
    Equal(options.Error.ToString()!.Contains("Calibration complete", StringComparison.Ordinal), true);
    Equal(options.Output.ToString()!.Contains("click: { x: 204, y: 208 }", StringComparison.Ordinal), true);
}));
await Test("unclicked marker fails and stops the hook", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop, [Event(2), Event(3)]);
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options() with
    {
        Viewport = new(800, 600), MarkerClick = _ => Task.FromResult<NormalizePoint?>(null),
    }, stop.Token), "recording calibration marker was not clicked");
    Equal(Events(path).Count, 0);
    Equal(fake.StopCalls, 1);
}));
await Test("CDP marker read failure preserves calibration error and hook cleanup", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop, [Event(3)]);
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options() with
    {
        Viewport = new(800, 600), MarkerClick = _ => throw new CrerException(3, "CDP disconnected"),
    }, stop.Token), "recording calibration marker was not clicked");
    Equal(fake.StopCalls, 1);
}));
await Test("viewport changes are attached to packets and logged without changing initial metadata", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var options = Options() with { Viewport = new(800, 600), ReadViewport = _ => Task.FromResult(new NormalizePoint(780, 590)) };
    await Recorder.RecordRaw(new FakeRecorder(stop, [Event(9, 'a')]), 42, path, options, stop.Token);
    Equal(JsonNode.DeepEquals(Events(path)[0]!["css_viewport"], JsonNode.Parse("""{"x":780,"y":590}""")), true);
    Equal(JsonNode.DeepEquals(Metadata(path)["css_viewport"], JsonNode.Parse("""{"x":800,"y":600}""")), true);
    Equal(options.Error.ToString()!.Contains("viewport changed from 800x600 to 780x590", StringComparison.Ordinal), true);
}));
await Test("drag packets never emit a click progress message", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var options = Options();
    await Recorder.RecordRaw(new FakeRecorder(stop, [Event(2), Event(1, x: 110), Event(3, x: 110)]), 42, path, options, stop.Token);
    Equal(options.Output.ToString(), "");
}));
await Test("click without calibration reports screen coordinates", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var options = Options();
    await Recorder.RecordRaw(new FakeRecorder(stop, [Event(2), Event(3)]), 42, path, options, stop.Token);
    Equal(options.Output.ToString()!.Contains("click screen_px: { x: 100, y: 200 }", StringComparison.Ordinal), true);
}));
await Test("overflow writes incomplete sidecar and retains primary failure when Stop also fails", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop) { Running = false, LastError = 111, StopStatus = 111 };
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options(), stop.Token), "Raw Input recorder failed: 111");
    Equal(Metadata(path)["incomplete"]!.GetValue<bool>(), true);
    Equal(Metadata(path)["recording_error"]!.GetValue<string>(), "Raw Input recorder failed: 111");
    Equal(fake.StopCalls, 1);
}));
await Test("unexpected zero-status hook termination is a failure", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop) { Running = false };
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options(), stop.Token), "Raw Input recorder stopped unexpectedly");
    Equal(Metadata(path)["incomplete"]!.GetValue<bool>(), true);
    Equal(fake.StopCalls, 1);
}));
await Test("late Stop timeout marks the completed sidecar incomplete", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop) { StopStatus = 1460 };
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options(), stop.Token), "Raw Input stop failed: 1460");
    Equal(Metadata(path)["incomplete"]!.GetValue<bool>(), true);
    Equal(Metadata(path)["recording_error"]!.GetValue<string>(), "Raw Input stop failed: 1460");
    Equal(fake.StopCalls, 1);
}));
await Test("viewport read failure preserves incomplete metadata and closes the hook", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop);
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options() with
    {
        ReadViewport = _ => throw new CrerException(3, "recording requires 100% browser zoom"),
    }, stop.Token), "recording requires 100% browser zoom");
    Equal(Metadata(path)["incomplete"]!.GetValue<bool>(), true);
    Equal(fake.StopCalls, 1);
}));
await Test("unavailable content bounds warn and omit invalid dimensions", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var options = Options();
    await Recorder.RecordRaw(new FakeRecorder(stop) { Bounds = new(1169, default) }, 42, path, options, stop.Token);
    Equal(Metadata(path)["content_rect_screen_px"], null);
    Equal(options.Error.ToString()!.Contains("Win32 status 1169", StringComparison.Ordinal), true);
}));
await Test("closing target browser stops recording without an incomplete marker", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop);
    await Recorder.RecordRaw(fake, 42, path, Options() with { IsTargetAlive = () => false }, stop.Token);
    Equal(File.ReadAllText(path), "");
    Equal(Metadata(path)["incomplete"], null);
    Equal(fake.StopCalls, 1);
}));
await Test("recorded packets normalize and validate with physical CSS calibration", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop, [Event(2, qpc: 1), Event(3, qpc: 2), Event(2, x: 300, y: 400, qpc: 3), Event(3, x: 300, y: 400, qpc: 4)]);
    await Recorder.RecordRaw(fake, 42, path, Options() with
    {
        Viewport = new(800, 600), MarkerClick = _ => Task.FromResult<NormalizePoint?>(new(4, 8)), RequestedContent = new(800, 600),
    }, stop.Token);
    var normalized = Normalize.FromFile(path, new() { Url = "http://127.0.0.1:8080/", Name = "record fixture" });
    Equal(normalized.Warnings.Count, 0);
    var scenario = Scenario.Parse(Normalize.ToYaml(normalized.Scenario));
    Equal(Json.Number(scenario.Steps[0]!["at"]!["x"]), 204);
    Equal(Json.Number(scenario.Steps[0]!["at"]!["y"]), 208);
}));
await Test("duration timer retains Deno truncation and signed 32-bit overflow behavior", () =>
{
    Equal(Recorder.DurationDelayMilliseconds(0.5), 0d);
    Equal(Recorder.DurationDelayMilliseconds(1234.9), 1234d);
    Equal(Recorder.DurationDelayMilliseconds(int.MaxValue), (double)int.MaxValue);
    Equal(Recorder.DurationDelayMilliseconds((double)int.MaxValue + 1), 1d);
    Equal(Recorder.DurationDelayMilliseconds(double.MaxValue), 1d);
    return Task.CompletedTask;
});
await Test("browser exit during a viewport read is a normal stop", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop);
    var alive = true;
    await Recorder.RecordRaw(fake, 42, path, Options() with
    {
        IsTargetAlive = () => alive,
        ReadViewport = _ => { alive = false; throw new CrerException(3, "CDP connection closed"); },
    }, stop.Token);
    Equal(Metadata(path)["incomplete"], null);
    Equal(fake.StopCalls, 1);
}));
await Test("CDP disconnection with a live browser remains an incomplete failure", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop);
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options() with
    {
        IsTargetAlive = () => true,
        ReadViewport = _ => throw new CrerException(3, "CDP connection closed"),
    }, stop.Token), "CDP connection closed");
    Equal(Metadata(path)["incomplete"]!.GetValue<bool>(), true);
    Equal(fake.StopCalls, 1);
}));
await Test("cancellation during a viewport read flushes metadata and stops the hook", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop);
    await Recorder.RecordRaw(fake, 42, path, Options() with
    {
        ReadViewport = token => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new NormalizePoint(800, 600)); },
    }, stop.Token);
    Equal(Metadata(path)["incomplete"], null);
    Equal(fake.StopCalls, 1);
}));
await Test("user closure before marker calibration still fails explicitly", () => Temp(async path =>
{
    using var stop = new CancellationTokenSource();
    var fake = new FakeRecorder(stop);
    await Reject(() => Recorder.RecordRaw(fake, 42, path, Options() with
    {
        IsTargetAlive = () => false,
        Viewport = new(800, 600), MarkerClick = _ => Task.FromResult<NormalizePoint?>(null),
    }, stop.Token), "recording calibration marker was not clicked");
    Equal(fake.StopCalls, 1);
}));

Console.WriteLine($"{passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeRecorder(CancellationTokenSource stop, params InputEvent[][] batches) : IInputRecorder
{
    private readonly Queue<InputEvent[]> queue = new(batches);
    public ulong QpcFrequencyHz => 10_000_000;
    public int StartStatus { get; init; }
    public bool Running { get; init; } = true;
    public bool IsRunning => Running;
    public int LastError { get; init; }
    public int StopStatus { get; init; }
    public int StartCalls { get; private set; }
    public uint StartPid { get; private set; }
    public int StopCalls { get; private set; }
    public ContentBounds Bounds { get; init; } = new(0, new() { X = 0, Y = 0, Width = 800, Height = 600 });
    public int Start(uint pid) { StartCalls++; StartPid = pid; return StartStatus; }
    public int Stop() { StopCalls++; return StopStatus; }
    public int Read(Span<InputEvent> target)
    {
        if (queue.TryDequeue(out var events)) { events.CopyTo(target); return events.Length; }
        stop.Cancel();
        return 0;
    }
    public ContentBounds GetContentRect() => Bounds;
}
