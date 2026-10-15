using System.Globalization;
using System.Text.Json.Nodes;

namespace Crer;

public sealed class CrerException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed record CliOptions(string Command, string? File, string? Chrome, bool KeepArtifacts,
    double? StepDelay, ulong? Seed, JsonObject? Position, bool MuteAudio)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] == "help")
            return new("help", null, null, false, null, null, null, false);
        var command = args[0];
        if (command is not ("validate" or "play")) throw new CrerException(2, $"unsupported command: {command}");
        var file = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var seen = new HashSet<string>();
        string? chrome = null;
        double? delay = null;
        ulong? seed = null;
        JsonObject? position = null;
        for (var i = file == null ? 1 : 2; i < args.Length; i++)
        {
            var key = args[i];
            if (!seen.Add(key)) throw new CrerException(2, $"option specified more than once: {key}");
            if (command == "validate") throw new CrerException(2, $"unknown option for validate: {key}");
            if (key is "--keep-artifacts" or "--mute-audio") continue;
            if (key is not ("--chrome" or "--step-delay-ms" or "--seed" or "--position"))
                throw new CrerException(2, $"unsupported or unknown option for play: {key}");
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new CrerException(2, $"{key} requires a value");
            var value = args[i];
            switch (key)
            {
                case "--chrome": chrome = value; break;
                case "--step-delay-ms":
                    if (!TryCliNumber(value, out var d) || d < 0)
                        throw new CrerException(2, "--step-delay-ms must be a non-negative number");
                    delay = d; break;
                case "--seed":
                    if (!value.All(char.IsAsciiDigit) || !ulong.TryParse(value, out var n))
                        throw new CrerException(2, "--seed must be a uint64 decimal string");
                    seed = n; break;
                case "--position":
                    var parts = value.Split(',');
                    if (parts.Length != 2 || !TryCliNumber(parts[0], out var x) || !TryCliNumber(parts[1], out var y)
                        || Math.Truncate(x) != x || Math.Truncate(y) != y)
                        throw new CrerException(2, "--position requires integer left,top");
                    position = new() { ["left"] = x, ["top"] = y }; break;
            }
        }
        if (file == null) throw new CrerException(2, "scenario path is required immediately after command");
        return new(command, file, chrome, seen.Contains("--keep-artifacts"), delay, seed, position, seen.Contains("--mute-audio"));
    }

    // The existing CLI uses JavaScript Number for delay and coordinate values.
    private static bool TryCliNumber(string value, out double number)
    {
        value = value.Trim().Trim('\uFEFF').Trim();
        number = 0;
        if (value.Length == 0) return true;
        if (value.Length > 2 && value[0] == '0' && "xXoObB".Contains(value[1]))
        {
            var radix = char.ToLowerInvariant(value[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            foreach (var c in value[2..])
            {
                var digit = c is >= '0' and <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;
                if (digit < 0 || digit >= radix) return false;
                number = number * radix + digit;
            }
            return double.IsFinite(number);
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
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
                Console.WriteLine("crer <validate|play> <file> [options]\nplay: --chrome <path> --keep-artifacts --seed <uint64> --position <left,top> --step-delay-ms <ms> --mute-audio\nC# milestone: click, wait_for, assert, screenshot, sleep; jitter must be disabled. See docs/csharp-cli.md.");
                return 0;
            }
            var scenario = Scenario.Load(options.File!);
            if (options.Command == "validate") { Console.WriteLine($"{options.File}: valid"); return 0; }
            var chrome = options.Chrome ?? Environment.GetEnvironmentVariable("CRER_CHROME");
            if (string.IsNullOrEmpty(chrome)) throw new CrerException(3, "Chrome for Testing must be specified with --chrome or CRER_CHROME");
            var result = await Playback.Run(scenario, options, chrome, cancellation.Token);
            Console.WriteLine(result.ToJsonString(Json.Indented));
            return result["code"]!.GetValue<int>();
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("interrupted by user"); return 5; }
        catch (CrerException e) { Console.Error.WriteLine(e.Message); return e.Code; }
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
