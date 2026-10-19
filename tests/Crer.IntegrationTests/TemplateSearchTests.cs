using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Crer;
using Crer.Windows;

internal static class TemplateSearchTests
{
    private sealed record SearchCase(string Name, int Width, int Height, int PatternWidth, int PatternHeight,
        string Prepare, bool Many = false, double Threshold = .9, int Limit = 5, int? X = null, int? Y = null, int? Count = null, int Repetitions = 1);

    public static async Task Run(string chrome, string evidence)
    {
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "template-search-baseline.json")))!["expressions"]!.AsArray();
        var id = "template-search-" + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(".crer/profiles");
        var profile = Path.Combine(root, id); Directory.CreateDirectory(profile);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = new ProcessStartInfo(Path.GetFullPath(chrome))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
            "--disable-component-update", "--disable-sync", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port,
            "--user-data-dir=" + profile, "about:blank" }) start.ArgumentList.Add(argument);
        using var owned = BackgroundProcessLauncher.Start(start);
        var stderr = owned.StandardError.ReadToEndAsync(); var stdout = owned.StandardOutput.ReadToEndAsync();
        await using var cdp = new Cdp();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
            string? websocket = null;
            while (string.IsNullOrEmpty(websocket) && !owned.Process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { websocket = Json.Text(JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", timeout.Token))?["webSocketDebuggerUrl"]); }
                catch (Exception error) when ((error is HttpRequestException or TaskCanceledException) && !timeout.IsCancellationRequested)
                { await Task.Delay(50, timeout.Token); }
            }
            if (string.IsNullOrEmpty(websocket)) throw new Exception("照合検証用の所有 CfT が CDP を公開しませんでした" +
                (owned.Process.HasExited ? " (exit " + owned.Process.ExitCode + ")" : ""));
            await cdp.Open(websocket, timeout.Token);
            var created = await cdp.Call("Target.createTarget", new() { ["url"] = "about:blank", ["background"] = true, ["hidden"] = true });
            var attached = await cdp.Call("Target.attachToTarget", new() { ["targetId"] = created["targetId"]!.DeepClone(), ["flatten"] = true });
            var session = Json.Text(attached["sessionId"]);
            await cdp.Call("Runtime.enable", session: session);
            var reports = new JsonArray();
            foreach (var test in Cases())
            {
                var legacy = Legacy(baseline[test.Many ? 1 : 0]!.GetValue<string>(), test);
                var current = Template.Expression("\"screen\"", "\"source\"", test.Many, test.Threshold, test.Limit);
                var evaluated = await cdp.Call("Runtime.evaluate", new()
                { ["expression"] = Expression(test, legacy, current), ["awaitPromise"] = true, ["returnByValue"] = true }, session, timeoutMs: 60000);
                if (evaluated["exceptionDetails"] != null) throw new Exception(test.Name + ": " + evaluated["exceptionDetails"]);
                var report = evaluated["result"]?["value"]?.DeepClone().AsObject() ?? throw new Exception(test.Name + ": 結果がありません");
                reports.Add(report);
                if (report["same"]?.GetValue<bool>() != true) throw new Exception(test.Name + ": 旧式と最適化後の結果が異なります: " + report);
                var actual = test.Many ? report["actual"]?.AsArray().FirstOrDefault() : report["actual"];
                if (test.X is int x && Json.Number(actual?["x"], -1) != x || test.Y is int y && Json.Number(actual?["y"], -1) != y)
                    throw new Exception(test.Name + ": 期待した最初の一致位置ではありません: " + report);
                if (test.Count is int count && report["actual"]?.AsArray().Count != count)
                    throw new Exception(test.Name + ": 期待した一致件数ではありません: " + report);
            }
            File.WriteAllText(Path.Combine(evidence, "template-search-parity.json"), reports.ToJsonString(Json.Indented));
            var large = reports.OfType<JsonObject>().Single(report => Json.Text(report["name"]) == "大画面の均一背景と不一致画像");
            Console.WriteLine($"template-search: {reports.Count} cases; large baseline={Json.Number(large["baselineMs"]):F1}ms optimized={Json.Number(large["optimizedMs"]):F1}ms");
        }
        finally
        {
            try { await cdp.Call("Browser.close", timeoutMs: 1000); } catch (CrerException) { }
            try { await owned.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { owned.Process.Kill(true); await owned.Process.WaitForExitAsync(); }
            await cdp.DisposeAsync();
            File.WriteAllText(Path.Combine(evidence, "template-search-chrome-stderr.log"), await stderr);
            File.WriteAllText(Path.Combine(evidence, "template-search-chrome-stdout.log"), await stdout);
            // 一意の所有 profile だけを、生成時と同じ専用ルートを確認して削除する。
            if (Path.GetFullPath(profile).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(profile) == id) Directory.Delete(profile, true);
        }
    }

    private static IEnumerable<SearchCase> Cases()
    {
        yield return new("右下端の完全一致", 17, 11, 3, 2, "fill(screen, 0); fill(source, 255); paste(14, 9);", X: 14, Y: 9);
        yield return new("単一照合の同点は行優先", 25, 13, 5, 3, "fill(screen, 71); fill(source, 71);", X: 0, Y: 0);
        yield return new("複数照合の同点と重なり除外", 37, 19, 6, 4, "fill(screen, 71); fill(source, 71);", true, 1, 5, X: 0, Y: 0, Count: 5);
        yield return new("透明度は照合に含めない", 2, 1, 1, 1, "fill(screen, 100); fill(source, 100); screen.data[3] = 0;", true, 1, 2, X: 0, Y: 0, Count: 2);
        var boundary = 1 - 3d / 765;
        yield return new("閾値ちょうどの一致", 1, 1, 1, 1, "fill(screen, 1); fill(source, 0);", true, boundary, 1, Count: 1);
        yield return new("閾値を一単位上回ると不一致", 1, 1, 1, 1, "fill(screen, 1); fill(source, 0);", true, Math.BitIncrement(boundary), 1, Count: 0);
        yield return new("候補上限到達後に良い候補へ置換", 160, 1, 1, 1,
            "fill(source, 0); for (let x = 0; x < screen.width; x++) { const offset = x * 4; screen.data.set([160 - x, 160 - x, 160 - x, 255], offset); }", true, 0, 1, X: 159, Y: 0, Count: 1);
        yield return new("重なる候補を後の高い類似度で置換", 4, 1, 3, 1,
            "fill(screen, 0); fill(source, 0); screen.data.set([30, 30, 30, 255], 0);", true, .9, 2, X: 1, Y: 0, Count: 1);
        yield return new("テンプレートが画面より大きい", 2, 2, 3, 2, "fill(screen, 0); fill(source, 0);");
        for (var seed = 1; seed <= 24; seed++)
        {
            var width = seed % 13 + 1; var height = seed % 11 + 1;
            var prepare = $"random(screen, {seed}); random(source, {seed + 100});" + (seed % 3 == 0 ? $"paste({seed % 5}, {seed % 4});" : "");
            yield return new("小画像とサンプル寸法 " + seed, width + 9, height + 7, width, height, prepare,
                seed % 2 == 0, (seed % 4) switch { 0 => 0, 1 => .5, 2 => .9, _ => 1 }, seed % 5 + 1);
        }
        yield return new("大画面の均一背景と不一致画像", 1235, 780, 72, 24,
            "fill(screen, 255); for (let index = 0; index < source.data.length; index++) source.data[index] = index % 251;", Repetitions: 3, X: 0, Y: 0);
        yield return new("大画面の最初の完全一致", 1235, 780, 72, 24, "fill(screen, 255); fill(source, 255);", X: 0, Y: 0, Repetitions: 3);
    }

    private static string Legacy(string expression, SearchCase test)
    {
        expression = expression.Replace("\"data:image/png;base64,c2NyZWVu\"", "\"screen\"", StringComparison.Ordinal)
            .Replace("\"data:image/png;base64,AA==\"", "\"source\"", StringComparison.Ordinal);
        if (!test.Many) return expression;
        var threshold = test.Threshold.ToString("R", CultureInfo.InvariantCulture);
        return expression.Replace("0.9", threshold, StringComparison.Ordinal)
            .Replace("Math.max(5 * 20, 5)", $"Math.max({test.Limit} * 20, {test.Limit})", StringComparison.Ordinal)
            .Replace("slice(0, 5)", $"slice(0, {test.Limit})", StringComparison.Ordinal);
    }

    private static string Expression(SearchCase test, string legacy, string current) => $$"""
        (async () => {
          const screen = { width: {{test.Width}}, height: {{test.Height}}, data: new Uint8ClampedArray({{test.Width * test.Height * 4}}) };
          const source = { width: {{test.PatternWidth}}, height: {{test.PatternHeight}}, data: new Uint8ClampedArray({{test.PatternWidth * test.PatternHeight * 4}}) };
          const fill = (image, value) => { image.data.fill(value); for (let offset = 3; offset < image.data.length; offset += 4) image.data[offset] = 255; };
          const random = (image, seed) => { for (let index = 0; index < image.data.length; index++) { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; image.data[index] = seed >>> 24; } };
          const paste = (x, y) => { for (let row = 0; row < source.height; row++) screen.data.set(source.data.subarray(row * source.width * 4, (row + 1) * source.width * 4), ((y + row) * screen.width + x) * 4); };
          {{test.Prepare}}
          // 画素列を直接渡し、画像デコードと描画による変換を除いて同じ製品の式を V8 で比較する。
          class Image { set src(value) { Object.assign(this, value === "screen" ? screen : source); queueMicrotask(() => this.onload()); } }
          const document = { createElement: () => { let image; return { getContext: () => ({ drawImage: value => { image = value; }, getImageData: () => ({ data: image.data }) }) }; } };
          const baseline = async () => { try { return await {{legacy}}; } catch (error) { return { error: error.message }; } };
          const optimized = async () => { try { return await {{current}}; } catch (error) { return { error: error.message }; } };
          const baselineTimes = [], optimizedTimes = [];
          let expected, actual;
          for (let attempt = 0; attempt < {{test.Repetitions}}; attempt++) {
            let began = performance.now(); expected = await baseline(); baselineTimes.push(performance.now() - began);
            began = performance.now(); actual = await optimized(); optimizedTimes.push(performance.now() - began);
            if (JSON.stringify(expected) !== JSON.stringify(actual)) return { name: {{JsonValue.Create(test.Name)!.ToJsonString()}}, same: false, expected, actual };
          }
          const median = values => values.sort((a, b) => a - b)[Math.floor(values.length / 2)];
          return { name: {{JsonValue.Create(test.Name)!.ToJsonString()}}, same: true, expected, actual, baselineMs: median(baselineTimes), optimizedMs: median(optimizedTimes) };
        })()
        """;
}
