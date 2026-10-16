using System.Diagnostics;

namespace Crer;

public static class ChromeEnvironment
{
    public static string? FilterCompatibilityLayer(string? value)
    {
        if (value == null) return null;
        var layers = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(layer => !new[] { "DPIUNAWARE", "GDIDPISCALING", "HIGHDPIAWARE" }.Contains(layer, StringComparer.OrdinalIgnoreCase)).ToArray();
        return layers.Any(layer => layer != "~") ? string.Join(' ', layers) : "";
    }

    public static void Apply(ProcessStartInfo process)
    {
        if (process.Environment.TryGetValue("__COMPAT_LAYER", out var value))
            process.Environment["__COMPAT_LAYER"] = FilterCompatibilityLayer(value);
    }
}
