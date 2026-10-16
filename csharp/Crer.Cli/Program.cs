using System.Globalization;
using System.Text.Json.Nodes;

namespace Crer;

public sealed class CrerException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Command == "help")
            {
                Console.WriteLine("crer <doctor|inspect|validate|play|run|record|normalize> <file> [options]");
                return 0;
            }
            if (options.Command == "doctor") { Console.WriteLine((await Diagnostics.Doctor(options)).ToJsonString(Json.Indented)); return 0; }
            if (options.Command == "inspect") { Console.WriteLine(Diagnostics.Inspect(options.File!).ToJsonString(Json.Indented)); return 0; }
            if (options.Command == "normalize")
            {
                var output = options.Value("--output"); var url = options.Value("--url");
                if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(url)) throw new CrerException(2, "normalize requires --output <scenario.crer.yaml> and --url <URL>");
                NormalizePoint? Point(string key)
                {
                    var raw = options.Value(key);
                    if (string.IsNullOrEmpty(raw)) return null;
                    var pair = CliOptions.Point(raw, key); return new(pair.x, pair.y);
                }
                var origin = Point("--client-origin"); var size = Point("--client-size"); var viewport = Point("--viewport");
                if ((origin != null || size != null || viewport != null) && (origin == null || size == null || viewport == null))
                    throw new CrerException(2, "coordinate conversion requires --client-origin, --client-size, and --viewport");
                var normalized = Normalize.FromFile(options.File!, new NormalizeOptions
                { Url = url, Name = options.Value("--name") ?? "recorded-scenario", UseSidecar = origin == null,
                    Transform = origin == null ? null : new(origin, size!, viewport!) });
                Normalize.SaveYaml(output, normalized.Scenario); normalized.WriteWarnings(Console.Error);
                Console.WriteLine($"Wrote {output}"); return 0;
            }
            if (options.Command == "validate")
            {
                var value = Scenario.LoadYaml(options.File!);
                try { Scenario.Validate(value); } catch (CrerException) { Scenario.ValidatePlan(value); }
                Console.WriteLine($"{options.File}: valid"); return 0;
            }
            var scenario = options.Command == "play" ? Scenario.Load(options.File!) : null;
            var plan = options.Command == "run" ? Scenario.LoadPlan(options.File!) : null;
            var chrome = options.Chrome ?? Environment.GetEnvironmentVariable("CRER_CHROME");
            if (string.IsNullOrEmpty(chrome)) throw new CrerException(3, "Chrome for Testing must be specified with --chrome or CRER_CHROME");
            if (options.Command == "record") return await Recorder.Run(options, chrome, cancellation.Token);
            if (options.Command == "run")
            {
                var outcome = await PlanRunner.Run(plan!, options, chrome, cancellation.Token);
                Console.WriteLine(outcome.Results.ToJsonString(Json.Indented)); return outcome.Code;
            }
            var result = await Playback.Run(scenario!, options, chrome, cancellation.Token);
            Console.WriteLine(result.ToJsonString(Json.Indented));
            return result["code"]!.GetValue<int>();
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("crer: interrupted by user"); return 5; }
        catch (CrerException e) { Console.Error.WriteLine($"crer: {e.Message}"); if (Environment.GetEnvironmentVariable("CRER_DEBUG") == "1") Console.Error.WriteLine(e); return e.Code; }
        catch (Exception e) { Console.Error.WriteLine($"crer: {e.Message}"); return 2; }
        finally { Console.CancelKeyPress -= handler; }
    }
}

public static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true, TypeInfoResolver = JsonContext.Default };
    public static string Text(JsonNode? node, string fallback = "") => node?.GetValue<string>() ?? fallback;
    public static double Number(JsonNode? node, double fallback = 0) => node == null ? fallback : double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
    public static void Write(string path, JsonNode value) => System.IO.File.WriteAllText(path, value.ToJsonString(Indented) + "\n");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonArray))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext;
