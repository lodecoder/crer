using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Crer;

// Diagnostic only: compare Windows startup show commands without changing product launch behavior.
internal static class StartupProbe
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedPointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);

    public static async Task<int> Run(string chrome)
    {
        var id = Guid.NewGuid().ToString("N");
        var evidence = Path.GetFullPath(Path.Combine(".crer", "integration", "startup-" + id)); Directory.CreateDirectory(evidence);
        var profileRoot = Path.GetFullPath(Path.Combine(".crer", "profiles")) + Path.DirectorySeparatorChar;
        var results = new JsonArray();
        foreach (var showCommand in new ushort[] { 0, 4, 8 })
        {
            var label = showCommand == 0 ? "process-start-baseline" : "create-process-show-" + showCommand;
            var profile = Path.Combine(profileRoot, "startup-probe-" + id + "-" + showCommand);
            if (!Path.GetFullPath(profile).StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("probe profile escaped owned root");
            Directory.CreateDirectory(profile);
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var html = "<!doctype html><title>CRER owned startup probe</title><button style='position:absolute;left:20px;top:20px;width:120px;height:40px'>button</button><input style='position:absolute;left:20px;top:80px;width:200px;height:40px'><script>window.probeClicks=0;window.probeKeys=[];document.querySelector('button').addEventListener('click',()=>window.probeClicks++);document.addEventListener('keydown',e=>window.probeKeys.push(e.key));</script>";
            var arguments = new[] { "--app=data:text/html," + Uri.EscapeDataString(html), "--no-first-run", "--no-default-browser-check", "--force-device-scale-factor=1", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port, "--user-data-dir=" + profile };
            var original = NativeWindows.GetForegroundWindow(); var cursor = NativeWindows.Cursor();
            var samples = new JsonArray(); var watch = Stopwatch.StartNew();
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    var foreground = NativeWindows.GetForegroundWindow(); var position = NativeWindows.Cursor();
                    samples.Add(new JsonObject { ["elapsedMs"] = watch.Elapsed.TotalMilliseconds, ["foreground"] = "0x" + foreground.ToString("x"), ["foregroundPid"] = NativeWindows.Owner(foreground), ["cursorX"] = position.X, ["cursorY"] = position.Y });
                    try { await Task.Delay(20, sampling.Token); } catch (OperationCanceledException) { }
                }
            });
            Process? process = null; await using var cdp = new Cdp();
            var observation = new JsonObject { ["mode"] = label, ["originalForeground"] = "0x" + original.ToString("x"), ["originalForegroundPid"] = NativeWindows.Owner(original), ["samples"] = samples };
            try
            {
                if (showCommand == 0)
                {
                    var start = new ProcessStartInfo(chrome) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var argument in arguments) start.ArgumentList.Add(argument);
                    process = Process.Start(start) ?? throw new Exception("baseline launch failed");
                }
                else
                {
                    var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = showCommand };
                    var command = new StringBuilder(Quote(chrome) + " " + string.Join(" ", arguments.Select(Quote)));
                    if (!CreateProcessW(chrome, command, 0, 0, false, 0x08000000, 0, Environment.CurrentDirectory, ref startup, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try { process = Process.GetProcessById((int)information.ProcessId); }
                    finally { CloseHandle(information.Thread); CloseHandle(information.Process); }
                }
                observation["ownedPid"] = process.Id;
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
                string? websocket = null; var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline && !process.HasExited && string.IsNullOrEmpty(websocket))
                {
                    try { websocket = Json.Text(JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version"))?["webSocketDebuggerUrl"]); }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { await Task.Delay(50); }
                }
                if (string.IsNullOrEmpty(websocket)) throw new Exception("owned startup probe CDP endpoint unavailable");
                observation["endpointReadyMs"] = watch.Elapsed.TotalMilliseconds;
                await cdp.Open(websocket, default);
                var targets = await cdp.Call("Target.getTargets"); observation["targets"] = targets.DeepClone();
                var page = targets["targetInfos"]!.AsArray().Single(target => Json.Text(target!["type"]) == "page")!;
                var attached = await cdp.Call("Target.attachToTarget", new() { ["targetId"] = page["targetId"]!.DeepClone(), ["flatten"] = true });
                var session = Json.Text(attached["sessionId"]);
                await cdp.Call("Page.enable", session: session); await cdp.Call("Runtime.enable", session: session);
                var browserWindow = await cdp.Call("Browser.getWindowForTarget", new() { ["targetId"] = page["targetId"]!.DeepClone() });
                var browserWindowId = browserWindow["windowId"]!.DeepClone();
                await cdp.Call("Browser.setWindowBounds", new() { ["windowId"] = browserWindowId.DeepClone(), ["bounds"] = new JsonObject { ["windowState"] = "normal" } });
                await cdp.Call("Browser.setWindowBounds", new() { ["windowId"] = browserWindowId.DeepClone(), ["bounds"] = new JsonObject { ["left"] = 25, ["top"] = 12, ["width"] = 800, ["height"] = 600 } });
                await cdp.Call("Browser.setContentsSize", new() { ["windowId"] = browserWindowId.DeepClone(), ["width"] = 600, ["height"] = 400 });
                await Task.Delay(200);
                var screenshot = await cdp.Call("Page.captureScreenshot", new() { ["format"] = "png" }, session);
                File.WriteAllBytes(Path.Combine(evidence, label + ".png"), Convert.FromBase64String(Json.Text(screenshot["data"])));
                observation["configuredMs"] = watch.Elapsed.TotalMilliseconds;
                observation["page"] = await cdp.Call("Runtime.evaluate", new() { ["expression"] = "({standalone:matchMedia('(display-mode: standalone)').matches,ready:document.readyState,width:innerWidth,height:innerHeight})", ["returnByValue"] = true }, session);
                var window = NativeWindows.Find(process.Id); observation["visibleWindow"] = "0x" + window.ToString("x");
                if (window != 0)
                {
                    var measured = NativeWindows.Measure(window);
                    observation["window"] = new JsonObject { ["left"] = measured.Rect.Left, ["top"] = measured.Rect.Top, ["width"] = measured.Rect.Right - measured.Rect.Left, ["height"] = measured.Rect.Bottom - measured.Rect.Top, ["dpi"] = measured.Dpi };
                }
                foreach (var point in new[] { (X: 50, Y: 40), (X: 50, Y: 100) })
                {
                    await cdp.Call("Input.dispatchMouseEvent", new() { ["type"] = "mouseMoved", ["x"] = point.X, ["y"] = point.Y, ["button"] = "none", ["buttons"] = 0 }, session);
                    await cdp.Call("Input.dispatchMouseEvent", new() { ["type"] = "mousePressed", ["x"] = point.X, ["y"] = point.Y, ["button"] = "left", ["buttons"] = 1, ["clickCount"] = 1 }, session);
                    await cdp.Call("Input.dispatchMouseEvent", new() { ["type"] = "mouseReleased", ["x"] = point.X, ["y"] = point.Y, ["button"] = "left", ["buttons"] = 0, ["clickCount"] = 1 }, session);
                }
                await cdp.Call("Input.dispatchKeyEvent", new() { ["type"] = "keyDown", ["key"] = "a", ["code"] = "KeyA", ["windowsVirtualKeyCode"] = 65, ["nativeVirtualKeyCode"] = 65, ["text"] = "a", ["unmodifiedText"] = "a" }, session);
                await cdp.Call("Input.dispatchKeyEvent", new() { ["type"] = "keyUp", ["key"] = "a", ["code"] = "KeyA", ["windowsVirtualKeyCode"] = 65, ["nativeVirtualKeyCode"] = 65 }, session);
                observation["input"] = await cdp.Call("Runtime.evaluate", new() { ["expression"] = "({clicks:window.probeClicks,keys:window.probeKeys,value:document.querySelector('input').value,active:document.activeElement.tagName})", ["returnByValue"] = true }, session);
                var input = observation["input"]!["result"]!["value"]!;
                if (Json.Number(input["clicks"]) != 1 || Json.Text(input["value"]) != "a" || Json.Text(input["active"]) != "INPUT") throw new Exception("background CDP click/key did not reach the owned page");
                var dimensions = observation["page"]!["result"]!["value"]!;
                if (dimensions["standalone"]?.GetValue<bool>() != true || Json.Number(dimensions["width"]) != 600 || Json.Number(dimensions["height"]) != 400) throw new Exception("app-window or content sizing changed");
                observation["inputCompletedMs"] = watch.Elapsed.TotalMilliseconds;
                await Task.Delay(2000); await cdp.Call("Browser.close");
            }
            catch (Exception error) { observation["error"] = error.ToString(); }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
                    observation["exited"] = process.HasExited; process.Dispose();
                }
                await Task.Delay(200); sampling.Cancel(); await sampler;
                observation["ownedForegroundSamples"] = samples.Count(sample => Json.Number(sample!["foregroundPid"]) == Json.Number(observation["ownedPid"]));
                observation["foregroundChangedSamples"] = samples.Count(sample => Json.Text(sample!["foreground"]) != "0x" + original.ToString("x"));
                observation["cursorChangedSamples"] = samples.Count(sample => Json.Number(sample!["cursorX"]) != cursor.X || Json.Number(sample["cursorY"]) != cursor.Y);
                File.WriteAllText(Path.Combine(evidence, label + ".json"), observation.ToJsonString(Json.Indented));
                results.Add(observation);
                try { Directory.Delete(profile, true); observation["profileDeleted"] = true; } catch (IOException error) { observation["profileCleanupError"] = error.Message; }
            }
            Console.WriteLine($"{label}: owned foreground {observation["ownedForegroundSamples"]}, changed {observation["foregroundChangedSamples"]}, cursor {observation["cursorChangedSamples"]}, visible {observation["visibleWindow"]}");
        }
        File.WriteAllText(Path.Combine(evidence, "summary.json"), results.ToJsonString(Json.Indented)); Console.WriteLine("evidence: " + evidence);
        return 0;
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append(character);
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
