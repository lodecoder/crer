using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

return await InterruptTest.Run(args);

internal static class InterruptTest
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 7) { Console.Error.WriteLine("usage: helper <crer.exe> <scenario> <chrome> <cwd> <case-name> <output-dir> <evidence.json>"); return 2; }
        var handles = new List<nint>();
        Process? child = null;
        var attached = false;
        var evidence = new JsonObject();
        try
        {
            if (!OperatingSystem.IsWindows()) throw new Exception("Ctrl+C integration requires Windows");
            Directory.CreateDirectory(args[5]);
            var start = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = 0x101, ShowWindow = 0 };
            start.StdInput = Open("NUL", 0x80000000, handles);
            start.StdOutput = Open(Path.Combine(args[5], "stdout.log"), 0x40000000, handles);
            start.StdError = Open(Path.Combine(args[5], "stderr.log"), 0x40000000, handles);
            var command = new StringBuilder(string.Join(' ', new[] { args[0], "play", args[1], "--chrome", args[2] }.Select(Quote)));
            // Only this child receives a new hidden console. Never detach or signal the parent's console.
            if (!Native.CreateProcess(args[0], command, 0, 0, true, 0x10, 0, args[3], ref start, out var info)) throw Win32("CreateProcess");
            Native.CloseHandle(info.Thread);
            Native.CloseHandle(info.Process);
            child = Process.GetProcessById(info.ProcessId);
            evidence["cliPid"] = child.Id;
            var run = await FindRun(args[3], args[4], child);
            evidence["runDir"] = run;
            await ObservePressed(run, child);
            evidence["pressedObserved"] = true;
            // This executes inside an isolated helper process, never in the harness/parent process.
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)child.Id)) throw Win32("AttachConsole");
            attached = true;
            if (!Native.SetConsoleCtrlHandler(0, true)) throw Win32("ignore Ctrl+C in helper");
            if (!Native.GenerateConsoleCtrlEvent(0, 0)) throw Win32("GenerateConsoleCtrlEvent");
            evidence["ctrlCGenerated"] = true;
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            evidence["exitCode"] = child.ExitCode;
            if (child.ExitCode != 5) throw new Exception($"Ctrl+C exit code {child.ExitCode}, expected 5");
            return 0;
        }
        catch (Exception e) { evidence["error"] = e.Message; Console.Error.WriteLine(e.Message); return 1; }
        finally
        {
            if (attached) Native.FreeConsole();
            if (child != null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
                child.Dispose();
            }
            foreach (var handle in handles) Native.CloseHandle(handle);
            File.WriteAllText(args[6], evidence.ToJsonString() + "\n");
        }
    }

    private static async Task<string> FindRun(string cwd, string name, Process child)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (child.HasExited) throw new Exception($"CLI exited before hold ({child.ExitCode})");
            foreach (var path in Directory.EnumerateDirectories(Path.Combine(cwd, ".crer", "runs"), $"*-{name}-*"))
                if (File.Exists(Path.Combine(path, "before.png"))) return path;
            await Task.Delay(50);
        }
        throw new Exception("CLI never reached the pre-hold screenshot");
    }

    private static async Task ObservePressed(string run, Process child)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!;
        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "launch.json")))!;
        var endpoint = new Uri(metadata["cdpEndpoint"]!.GetValue<string>());
        if (endpoint.Scheme != "http" || !endpoint.IsLoopback || endpoint.Port <= 0) throw new Exception("owned CDP endpoint is not loopback HTTP");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        var targets = JsonNode.Parse(await http.GetStringAsync(new Uri(endpoint, "/json/list")))!.AsArray();
        var target = targets.First(x => x?["type"]?.GetValue<string>() == "page" && x?["id"]?.GetValue<string>() == launch["targetId"]!.GetValue<string>())!;
        var socketUri = new Uri(target["webSocketDebuggerUrl"]!.GetValue<string>());
        if (socketUri.Scheme != "ws" || !socketUri.IsLoopback || socketUri.Port != endpoint.Port) throw new Exception("owned CDP WebSocket endpoint changed");
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(socketUri, timeout.Token);
        var buffer = new byte[8192];
        for (var id = 1; !timeout.IsCancellationRequested; id++)
        {
            if (child.HasExited) throw new Exception("CLI exited before observing mousePressed");
            var message = new JsonObject { ["id"] = id, ["method"] = "Runtime.evaluate", ["params"] = new JsonObject
            {
                ["expression"] = "document.querySelector('[role=button]')?.matches(':active') === true", ["returnByValue"] = true,
            } };
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, timeout.Token);
            using var response = new MemoryStream();
            WebSocketReceiveResult part;
            do { part = await socket.ReceiveAsync(buffer, timeout.Token); response.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
            var reply = JsonNode.Parse(response.ToArray());
            if (reply?["result"]?["result"]?["value"]?.GetValue<bool>() == true) return;
            await Task.Delay(50, timeout.Token);
        }
        throw new Exception("could not observe held primary button before sending Ctrl+C");
    }

    private static nint Open(string path, uint access, List<nint> handles)
    {
        var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
        var handle = Native.CreateFile(path, access, 3, ref attributes, path == "NUL" ? 3u : 2u, 0x80, 0);
        if (handle == -1) throw Win32("open redirected stream");
        handles.Add(handle);
        return handle;
    }
    private static Exception Win32(string action) => new Win32Exception(Marshal.GetLastWin32Error(), action);
    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public nint ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern nint CreateFile(string path, uint access, uint sharing, ref SecurityAttributes attributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GenerateConsoleCtrlEvent(uint signal, uint processGroup);
}
