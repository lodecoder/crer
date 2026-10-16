using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Crer.Windows;
using Microsoft.Win32.SafeHandles;

if (args.Length >= 2 && args[0] == "--child")
{
    if (args[1] == "exit42") return 42;
    var encoding = Environment.GetEnvironmentVariable("CRER_TEST_ENCODING") == "utf16" ? Encoding.Unicode : Encoding.UTF8;
    Console.OutputEncoding = encoding; Console.InputEncoding = Encoding.UTF8;
    if (args[1] == "sleep") { Console.WriteLine("READY"); await Task.Delay(60000); return 0; }
    if (args[1] == "flood")
    {
        await Task.WhenAll(Console.Out.WriteAsync(new string('o', 131072) + "日本語-out"), Console.Error.WriteAsync(new string('e', 131072) + "日本語-err"));
        return 0;
    }
    var stdin = await Console.In.ReadToEndAsync();
    var startup = new Native.StartupInfo(); Native.GetStartupInfo(ref startup);
    var sentinel = Environment.GetEnvironmentVariable("CRER_TEST_HANDLE");
    var signalled = false; var invalidHandleException = false;
    if (long.TryParse(sentinel, out var rawHandle))
    {
        try { signalled = Native.GetHandleInformation((nint)rawHandle, out _) != 0 && Native.SetEvent((nint)rawHandle) != 0; }
        // Handle-verification environments raise STATUS_INVALID_HANDLE instead
        // of returning ERROR_INVALID_HANDLE. The parent still checks its real
        // sentinel event, so this exception cannot hide an inherited handle.
        catch (SEHException) { invalidHandleException = true; }
    }
    var capture = new JsonObject
    {
        ["arguments"] = new JsonArray(args.Skip(2).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["cwd"] = Environment.CurrentDirectory, ["value"] = Environment.GetEnvironmentVariable("CRER_BACKGROUND_VALUE"),
        ["empty"] = Environment.GetEnvironmentVariable("CRER_BACKGROUND_EMPTY"), ["unicodeName"] = Environment.GetEnvironmentVariable("CRER_日本語"),
        ["removed"] = Environment.GetEnvironmentVariable("CRER_BACKGROUND_REMOVED"), ["stdin"] = stdin,
        ["showWindow"] = startup.ShowWindow, ["startupFlags"] = startup.Flags, ["sentinelSignalled"] = signalled, ["invalidHandleException"] = invalidHandleException,
    };
    Console.WriteLine(capture.ToJsonString()); Console.Error.Write("owned-error-日本語");
    return 0;
}
if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows-only background launcher tests"); return 0; }
var root = Path.GetFullPath(Path.Combine(".crer", "background-launcher", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var executable = Environment.ProcessPath!;
var passed = 0; var failed = 0; var results = new JsonArray();
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; results.Add((JsonNode)new JsonObject { ["test"] = name, ["passed"] = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add((JsonNode)new JsonObject { ["test"] = name, ["passed"] = false, ["error"] = error.ToString() }); Console.Error.WriteLine($"FAIL {name}: {error}"); }
    await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), new JsonObject { ["passed"] = passed, ["failed"] = failed, ["cases"] = results.DeepClone() }.ToJsonString());
}
ProcessStartInfo Start(string mode = "echo", params string[] arguments)
{
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    info.ArgumentList.Add("--child"); info.ArgumentList.Add(mode);
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return info;
}
async Task<(JsonObject Value, string Error)> Capture(ProcessStartInfo info)
{
    using var child = BackgroundProcessLauncher.Start(info);
    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await child.Process.WaitForExitAsync(timeout.Token);
    var text = await output; var errors = await error;
    Check(child.Process.ExitCode == 0, "child returned " + child.Process.ExitCode + ": " + errors);
    return (JsonNode.Parse(text)!.AsObject(), errors);
}
await Test("Unicode empty quoted whitespace and trailing-backslash arguments round-trip exactly", async () =>
{
    string[] values = ["", "日本語😀", "one two", "a\tb", "line\nfeed", "plain", "\"", "a\"b", "C:\\path with spaces\\", "C:\\tail\\", "\\\\", "a\\\"b", "ends\\\\\\"];
    var (value, error) = await Capture(Start("echo", values));
    Check(value["arguments"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(values), "Windows quoting changed an argument");
    Check(error == "owned-error-日本語", "stderr Unicode was corrupted");
    Check(value["stdin"]!.GetValue<string>() == "", "stdin was not EOF");
    Check(value["showWindow"]!.GetValue<int>() == 4 && (value["startupFlags"]!.GetValue<uint>() & 0x181) == 0x181, "startup show state or stdio flags changed");
});
await Test("custom Unicode working directory and sorted environment preserve ProcessStartInfo values", async () =>
{
    var directory = Path.Combine(root, "作業 directory"); Directory.CreateDirectory(directory);
    var info = Start(); info.WorkingDirectory = directory;
    info.Environment["CRER_BACKGROUND_VALUE"] = "日本語 value=quoted\"\\";
    info.Environment["CRER_BACKGROUND_EMPTY"] = null;
    info.Environment["CRER_日本語"] = "環境値😀";
    info.Environment["CRER_BACKGROUND_REMOVED"] = "temporary"; info.Environment.Remove("CRER_BACKGROUND_REMOVED");
    var (value, _) = await Capture(info);
    Check(value["cwd"]!.GetValue<string>() == directory, "working directory changed");
    Check(value["value"]!.GetValue<string>() == info.Environment["CRER_BACKGROUND_VALUE"] && value["unicodeName"]!.GetValue<string>() == "環境値😀", "environment Unicode or equals changed");
    Check(value["removed"] == null, "removed environment variable was inherited");
    // Compare the empty-value corner against the BCL's real process path.
    info.RedirectStandardInput = true;
    Process referenceProcess;
    lock (BackgroundProcessLauncher.CreationGate) referenceProcess = Process.Start(info)!;
    using var reference = referenceProcess; reference.StandardInput.Close();
    var baseline = reference.StandardOutput.ReadToEndAsync(); var baselineError = reference.StandardError.ReadToEndAsync();
    await reference.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    var referenceValue = JsonNode.Parse(await baseline)!; _ = await baselineError;
    Check(JsonNode.DeepEquals(value["empty"], referenceValue["empty"]), "null environment value differs from Process.Start");
});
await Test("an empty environment block is double-NUL terminated", async () =>
{
    var info = Start(); info.Environment.Clear();
    var (value, _) = await Capture(info);
    Check(value["value"] == null && value["unicodeName"] == null && value["stdin"]!.GetValue<string>() == "", "empty environment acquired inherited values");
});
await Test("explicit output encodings are applied to each redirected reader", async () =>
{
    var info = Start(); info.Environment["CRER_TEST_ENCODING"] = "utf16";
    info.StandardOutputEncoding = Encoding.Unicode; info.StandardErrorEncoding = Encoding.Unicode;
    var (value, error) = await Capture(info);
    Check(value["arguments"]!.AsArray().Count == 0 && error == "owned-error-日本語", "configured UTF-16 readers were ignored");
});
await Test("large stdout and stderr drain independently without pipe deadlock", async () =>
{
    using var child = BackgroundProcessLauncher.Start(Start("flood"));
    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(await output == new string('o', 131072) + "日本語-out" && await error == new string('e', 131072) + "日本語-err", "stdio content was lost");
});
await Test("rapid exit42 retains Process ID wait and exit-code semantics", async () =>
{
    for (var index = 0; index < 25; index++)
    {
        using var child = BackgroundProcessLauncher.Start(Start("exit42"));
        Check(child.Process.Id > 0, "process ID was not retained");
        await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.Process.HasExited && child.Process.WaitForExit(0) && child.Process.ExitCode == 42, "immediate exit lost the original process");
        Check(await child.StandardOutput.ReadToEndAsync() == "" && await child.StandardError.ReadToEndAsync() == "", "empty immediate-exit pipes did not close");
    }
});
await Test("only listed stdio handles inherit and an unrelated inheritable event remains inaccessible", async () =>
{
    var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 };
    using var sentinel = new SafeWaitHandle(Native.CreateEvent(ref security, 1, 0, null), true);
    Check(!sentinel.IsInvalid, "could not create the owned inheritable sentinel");
    var info = Start(); info.Environment["CRER_TEST_HANDLE"] = sentinel.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
    var (value, _) = await Capture(info);
    Check(!value["sentinelSignalled"]!.GetValue<bool>(), "child could signal an unlisted inherited event");
    Check(Native.WaitForSingleObject(sentinel.DangerousGetHandle(), 0) == 258, "an unrelated inheritable event leaked into the child");
});
await Test("managed Process.Kill and wait remain usable before wrapper disposal", async () =>
{
    using var child = BackgroundProcessLauncher.Start(Start("sleep"));
    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "owned child did not become ready");
    child.Process.Kill(entireProcessTree: true); await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(child.Process.HasExited, "managed Kill did not terminate the owned child");
});
await Test("parallel background and standard starts retain separate arguments and pipes", async () =>
{
    var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
    {
        var info = Start("echo", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        JsonObject capture;
        if (index % 2 == 0) (capture, _) = await Capture(info);
        else
        {
            info.RedirectStandardInput = true;
            Process started;
            lock (BackgroundProcessLauncher.CreationGate) started = Process.Start(info)!;
            using var standard = started; standard.StandardInput.Close();
            var output = standard.StandardOutput.ReadToEndAsync(); var error = standard.StandardError.ReadToEndAsync();
            await standard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            capture = JsonNode.Parse(await output)!.AsObject();
            Check(standard.ExitCode == 0 && await error == "owned-error-日本語", "parallel standard child lost stderr");
        }
        Check(capture["arguments"]![0]!.GetValue<string>() == index.ToString(System.Globalization.CultureInfo.InvariantCulture), "parallel child received another process's arguments");
        Check(capture["stdin"]!.GetValue<string>() == "", "parallel child retained an unrelated stdin writer");
    }));
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
});
await Test("wrapper disposal kills its still-running child and is idempotent", async () =>
{
    var child = BackgroundProcessLauncher.Start(Start("sleep"));
    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "owned child did not become ready");
    using var observed = Process.GetProcessById(child.Process.Id); _ = observed.Handle;
    child.Dispose(); child.Dispose();
    Check(observed.WaitForExit(5000), "wrapper disposed its handles while leaving the child alive");
});
await Test("post-create reader failure terminates the suspended child and preserves the primary error", () =>
{
    var info = Start("sleep"); var encoding = new FailingEncoding(); info.StandardOutputEncoding = encoding;
    try { using var child = BackgroundProcessLauncher.Start(info); throw new Exception("injected reader setup succeeded"); }
    catch (InvalidOperationException error) when (ReferenceEquals(error, encoding.Error))
    {
        Check(error.Data["backgroundProcessId"] is uint, "failed startup lost its owned process diagnostic");
        var id = checked((int)(uint)error.Data["backgroundProcessId"]!);
        try { using var process = Process.GetProcessById(id); Check(process.HasExited, "failed startup left a suspended process alive"); }
        catch (ArgumentException) { }
        Check(!error.Data.Contains("cleanupErrors"), "failed startup could not release a resource");
    }
    return Task.CompletedTask;
});
await Test("invalid executable and working-directory failures do not accumulate handles", () =>
{
    using var current = Process.GetCurrentProcess(); _ = current.Handle;
    void Invalid(bool directory)
    {
        var info = Start();
        if (directory) info.WorkingDirectory = Path.Combine(root, "missing-working-directory");
        else info.FileName = Path.Combine(root, "missing-executable.exe");
        try { using var child = BackgroundProcessLauncher.Start(info); throw new Exception("invalid startup succeeded"); }
        catch (Win32Exception) { }
    }
    for (var index = 0; index < 4; index++) Invalid(index % 2 == 0);
    Check(Native.GetProcessHandleCount(current.Handle, out var before) != 0, "could not read the baseline handle count");
    for (var index = 0; index < 60; index++) Invalid(index % 2 == 0);
    Check(Native.GetProcessHandleCount(current.Handle, out var after) != 0 && after <= before + 2, $"failed startup leaked handles: {before} -> {after}");
    return Task.CompletedTask;
});
await Test("invalid options arguments and environment fail before any child is created", () =>
{
    void Reject(Action<ProcessStartInfo> change)
    {
        var info = Start(); change(info);
        try { using var child = BackgroundProcessLauncher.Start(info); throw new Exception("invalid startup was accepted"); }
        catch (ArgumentException error) { Check(!error.Data.Contains("backgroundProcessId"), "invalid input created a process"); }
    }
    Reject(info => info.UseShellExecute = true); Reject(info => info.RedirectStandardOutput = false);
    Reject(info => info.FileName = "relative.exe"); Reject(info => info.Arguments = "raw arguments");
    Reject(info => info.ArgumentList.Add("embedded\0NUL")); Reject(info => info.Environment["CRER_BACKGROUND_VALUE"] = "embedded\0NUL");
    Reject(info => info.ArgumentList.Add(new string('x', 32767)));
    return Task.CompletedTask;
});
Console.WriteLine($"{passed} passed, {failed} failed; evidence: {root}");
return failed == 0 ? 0 : 1;

sealed class FailingEncoding : UTF8Encoding
{
    public InvalidOperationException Error { get; } = new("owned reader setup failure");
    public override Decoder GetDecoder() => throw Error;
}
static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes { public uint Length; public nint Descriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes; public nint Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW")] public static extern void GetStartupInfo(ref StartupInfo info);
    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateEvent(ref SecurityAttributes security, int manualReset, int initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int SetEvent(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetHandleInformation(nint handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int GetProcessHandleCount(nint process, out uint handles);
}
