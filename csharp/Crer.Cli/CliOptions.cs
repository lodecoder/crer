using System.Globalization;
using System.Text.Json.Nodes;

namespace Crer;

public sealed record CliOptions(string Command, string? File, string? Chrome, bool KeepArtifacts,
    double? StepDelay, ulong? Seed, JsonObject? Position, bool MuteAudio)
{
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
    public bool IgnoreViewportMismatch { get; init; }
    public string? Dll => Value("--dll");
    public string? ProfileDir => Value("--profile-dir");
    public string? TemplateScreenshots => Value("--template-screenshots");
    public JsonObject? BoundsOverride { get; init; }
    public double? OpacityOverride { get; init; }
    public string? Value(string key) => Values.TryGetValue(key, out var value) ? value : null;

    private static readonly Dictionary<string, (string[] values, string[] switches)> Commands = new()
    {
        ["doctor"] = (["--chrome", "--dll"], []),
        ["inspect"] = ([], []),
        ["validate"] = ([], []),
        ["play"] = (["--chrome", "--dll", "--seed", "--step-delay-ms", "--profile-dir", "--template-screenshots", "--position"],
            ["--keep-artifacts", "--ignore-viewport-mismatch", "--mute-audio"]),
        ["run"] = (["--chrome", "--dll", "--profile-dir", "--plan-window-bounds-override", "--plan-window-opacity-override", "--template-screenshots"],
            ["--ignore-viewport-mismatch", "--mute-audio"]),
        ["record"] = (["--chrome", "--dll", "--profile-dir", "--url", "--content-size", "--position", "--duration-ms", "--stop-file", "--name"], []),
        ["normalize"] = (["--output", "--url", "--name", "--client-origin", "--client-size", "--viewport"], []),
    };

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] == "help") return new("help", null, null, false, null, null, null, false);
        var command = args[0];
        if (!Commands.TryGetValue(command, out var spec)) throw Invalid($"unknown command: {command}");
        var takesFile = command != "doctor";
        var file = takesFile && args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = file == null ? 1 : 2; i < args.Length; i++)
        {
            var key = args[i];
            if (!seen.Add(key)) throw Invalid($"option specified more than once: {key}");
            if (spec.switches.Contains(key, StringComparer.Ordinal)) continue;
            if (!spec.values.Contains(key, StringComparer.Ordinal)) throw Invalid($"unknown option for {command}: {key}");
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw Invalid($"{key} requires a value");
            values[key] = args[i];
        }
        if (takesFile && file == null) throw Invalid(command == "inspect" ? "artifact directory is required" : "scenario or plan path is required immediately after command");
        string? Get(string key) => values.GetValueOrDefault(key);
        double? delay = null;
        if (Get("--step-delay-ms") is string d)
        {
            if (!TryNumber(d, out var n) || n < 0) throw Invalid("--step-delay-ms must be a non-negative number");
            delay = n;
        }
        ulong? seed = null;
        if (Get("--seed") is string s)
        {
            if (s.Length == 0 || !s.All(char.IsAsciiDigit) || !ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw Invalid("--seed must be a uint64 decimal string");
            seed = n;
        }
        JsonObject? position = null;
        if (Get("--position") is string p)
        {
            var point = Point(p, "--position");
            if (Math.Truncate(point.x) != point.x || Math.Truncate(point.y) != point.y) throw Invalid("--position must be integer left,top");
            position = new() { ["left"] = point.x, ["top"] = point.y };
        }
        if (Get("--template-screenshots") is string policy && policy is not ("all" or "failure-only")) throw Invalid("--template-screenshots must be all or failure-only");
        JsonObject? bounds = null;
        if (Get("--plan-window-bounds-override") is string b)
        {
            var parts = b.Split(',');
            if (parts.Length is not (2 or 4)) throw Invalid("--plan-window-bounds-override must be left,top or left,top,width,height");
            var numbers = parts.Select(part => TryNumber(part, out var number) ? number : double.NaN).ToArray();
            if (numbers.Any(number => !double.IsFinite(number) || number != Math.Truncate(number))) throw Invalid("--plan-window-bounds-override values must be integers");
            if (numbers.Length == 4 && (numbers[2] <= 0 || numbers[3] <= 0)) throw Invalid("--plan-window-bounds-override width and height must be positive");
            bounds = new() { ["left"] = numbers[0], ["top"] = numbers[1] };
            if (numbers.Length == 4) { bounds["width"] = numbers[2]; bounds["height"] = numbers[3]; }
        }
        double? opacity = null;
        if (Get("--plan-window-opacity-override") is string o)
        {
            if (string.IsNullOrWhiteSpace(o) || !TryNumber(o, out var n) || n < 0 || n > 1) throw Invalid("--plan-window-opacity-override must be a finite number from 0 to 1");
            opacity = n;
        }
        if (Get("--duration-ms") is string duration && (!TryNumber(duration, out var ms) || ms < 0)) throw Invalid("--duration-ms must be a non-negative number");
        if (Get("--content-size") is string size)
        {
            var point = Point(size, "--content-size");
            if (point.x < 32 || point.y < 32 || Math.Truncate(point.x) != point.x || Math.Truncate(point.y) != point.y)
                throw Invalid("--content-size must be integer width,height with both values at least 32");
        }
        foreach (var key in new[] { "--client-origin", "--client-size", "--viewport" }) if (Get(key) is string point) Point(point, key);
        return new(command, file, Get("--chrome"), seen.Contains("--keep-artifacts"), delay, seed, position, seen.Contains("--mute-audio"))
        { Values = values, IgnoreViewportMismatch = seen.Contains("--ignore-viewport-mismatch"), BoundsOverride = bounds, OpacityOverride = opacity };
    }

    // The original pointOption consumes the first two comma separated values (including Number("")=0).
    public static (double x, double y) Point(string value, string key)
    {
        var parts = value.Split(',');
        if (parts.Length < 2 || !TryNumber(parts[0], out var x) || !TryNumber(parts[1], out var y)) throw Invalid($"{key} must be x,y");
        return (x, y);
    }

    public static bool TryNumber(string value, out double number)
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
    private static CrerException Invalid(string message) => new(2, message);
}
