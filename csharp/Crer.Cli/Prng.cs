using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Crer;

public readonly record struct ActionPoint(double X, double Y)
{
    public JsonObject ToJson() => new() { ["x"] = X, ["y"] = Y };
    public static ActionPoint From(JsonNode point) => new(ActionNumber.Read(point["x"]), ActionNumber.Read(point["y"]));
}

/// <summary>A step's jitter object replaces the scenario object in its entirety.</summary>
public sealed record JitterConfig(bool Enabled, string Distribution, double RadiusPx,
    double MinDistanceFromEdgePx, string OutOfBounds)
{
    public static JitterConfig? From(JsonNode? value) => value == null ? null : new(
        value["enabled"]?.GetValue<bool>() ?? false,
        value["distribution"]?.GetValue<string>() ?? "none",
        ActionNumber.Read(value["radius_px"]), ActionNumber.Read(value["min_distance_from_edge_px"]),
        value["out_of_bounds"]?.GetValue<string>() ?? "fail");
}

/// <summary>splitmix64 initialization and xoshiro256**; every operation wraps at uint64.</summary>
public sealed class DeterministicRandom
{
    private ulong s0, s1, s2, s3;

    public DeterministicRandom(ulong seed)
    {
        s0 = SplitMix64(ref seed); s1 = SplitMix64(ref seed);
        s2 = SplitMix64(ref seed); s3 = SplitMix64(ref seed);
    }

    private static ulong RotateLeft(ulong value, int shift) => (value << shift) | (value >> (64 - shift));

    private static ulong SplitMix64(ref ulong state)
    {
        unchecked
        {
            state += 0x9e3779b97f4a7c15UL;
            var value = state;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            var result = RotateLeft(s1 * 5, 7) * 9;
            var temporary = s1 << 17;
            s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= temporary;
            s3 = RotateLeft(s3, 45);
            return result;
        }
    }

    public double Next() => (NextUInt64() >> 11) / 9007199254740992d;
}

public static class Prng
{
    public static string RandomSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes).ToString(CultureInfo.InvariantCulture);
    }

    public static ActionPoint? Jitter(ActionPoint point, JitterConfig? config,
        DeterministicRandom random, ActionPoint size)
    {
        // Disabled jitter does not impose bounds or consume random values in the old CLI.
        if (config is not { Enabled: true } || config.Distribution == "none") return point;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var theta = 2 * Math.PI * random.Next();
            var radius = config.Distribution == "uniform"
                ? Math.Sqrt(random.Next()) * config.RadiusPx
                : Math.Min(config.RadiusPx,
                    Math.Sqrt(-2 * Math.Log(Math.Max(random.Next(), double.Epsilon))) * Math.Cos(theta)
                    * config.RadiusPx / 3);
            var result = new ActionPoint(point.X + Math.Cos(theta) * radius, point.Y + Math.Sin(theta) * radius);
            if (result.X >= config.MinDistanceFromEdgePx && result.Y >= config.MinDistanceFromEdgePx
                && result.X <= size.X - config.MinDistanceFromEdgePx
                && result.Y <= size.Y - config.MinDistanceFromEdgePx) return result;
        }
        return config.OutOfBounds == "disable-for-step" ? point : null;
    }
}

/// <summary>JavaScript Number conversion used after function argument expansion.</summary>
public static class ActionNumber
{
    public static double Read(JsonNode? value, double fallback = 0)
    {
        if (value == null) return fallback;
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<double>(out var number)) return number;
            if (scalar.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                return double.Parse(scalar.ToJsonString(), CultureInfo.InvariantCulture);
            if (scalar.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
            if (scalar.TryGetValue<string>(out var text)) return Parse(text);
        }
        return double.NaN;
    }

    public static double Parse(string value)
    {
        value = value.Trim().Trim('\uFEFF').Trim();
        if (value.Length == 0) return 0;
        if (value.Length > 2 && value[0] == '0' && "xXoObB".Contains(value[1]))
        {
            var radix = char.ToLowerInvariant(value[1]) switch { 'x' => 16, 'o' => 8, _ => 2 };
            var number = 0d;
            foreach (var character in value[2..])
            {
                var digit = character is >= '0' and <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10;
                if (digit < 0 || digit >= radix) return double.NaN;
                number = number * radix + digit;
            }
            return number;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
    }

    public static string String(JsonNode? value, string fallback = "null")
    {
        if (value == null) return fallback;
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return text;
        if (value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return value.ToJsonString();
        var number = Read(value);
        if (number == 0) return "0";
        var raw = number.ToString("R", CultureInfo.InvariantCulture);
        var e = raw.IndexOf('E');
        if (e < 0) return raw;
        var exponent = int.Parse(raw[(e + 1)..], CultureInfo.InvariantCulture);
        var mantissa = raw[..e];
        if (exponent is >= -6 and < 21)
        {
            var negative = mantissa.StartsWith('-');
            var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal);
            var decimalPosition = exponent + 1;
            var formatted = decimalPosition <= 0 ? "0." + new string('0', -decimalPosition) + digits
                : decimalPosition >= digits.Length ? digits + new string('0', decimalPosition - digits.Length)
                : digits[..decimalPosition] + "." + digits[decimalPosition..];
            return (negative ? "-" : "") + formatted;
        }
        return mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
