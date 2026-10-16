using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Crer;

public static class Diagnostics
{
    public static async Task<JsonObject> Doctor(CliOptions options)
    {
        return new() { ["dotnet"] = RuntimeInformation.FrameworkDescription, ["os"] = new JsonObject
            { ["os"] = OperatingSystem.IsWindows() ? "windows" : RuntimeInformation.OSDescription, ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() },
            ["chrome"] = await Chrome(options.Chrome ?? Environment.GetEnvironmentVariable("CRER_CHROME")) };
    }

    public static async Task<JsonObject> Chrome(string? configured)
    {
        if (string.IsNullOrEmpty(configured)) return new() { ["path"] = "not configured", ["exists"] = false };
        if (!File.Exists(configured)) return new() { ["path"] = configured, ["exists"] = false };
        var resolved = Profiles.RealPath(configured);
        var version = "unavailable";
        var sha256 = "unavailable";
        try { version = FileVersionInfo.GetVersionInfo(resolved).ProductVersion ?? "unavailable"; } catch { }
        try { using var stream = File.OpenRead(resolved); sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)); } catch { }
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("CRER_CHROME_MANIFEST") is string manifestPath && manifestPath.Length > 0) candidates.Add(manifestPath);
        candidates.Add(Path.Combine(Environment.CurrentDirectory, ".crer", "browsers", "crer-chrome.json"));
        var directory = Path.GetDirectoryName(resolved);
        for (var i = 0; i < 8 && directory != null; i++, directory = Path.GetDirectoryName(directory)) candidates.Add(Path.Combine(directory, "crer-chrome.json"));
        JsonObject? manifest = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var raw = JsonNode.Parse(await File.ReadAllTextAsync(candidate))!.AsObject();
                var hash = raw["sha256"] is JsonValue hashValue && hashValue.TryGetValue<string>(out var h) ? h.ToLowerInvariant() : null;
                manifest = new() { ["path"] = candidate,
                    ["matchesChrome"] = raw["chrome"] is JsonValue chrome && chrome.TryGetValue<string>(out var c) && string.Equals(c, resolved, StringComparison.OrdinalIgnoreCase),
                    ["matchesSha256"] = hash != null && sha256 != "unavailable" ? JsonValue.Create(hash == sha256) : JsonValue.Create("unverified") };
                foreach (var key in new[] { "requested", "installed" })
                    if (raw[key] is JsonValue v && v.TryGetValue<string>(out var text)) manifest[key] = text;
                if (hash != null) manifest["sha256"] = hash;
                break;
            }
            catch { }
        }
        JsonNode verified = JsonValue.Create("unverified")!;
        if (manifest != null)
        {
            var pathMatches = manifest["matchesChrome"]!.GetValue<bool>();
            verified = manifest["matchesSha256"] is JsonValue value && value.TryGetValue<bool>(out var matches)
                ? JsonValue.Create(pathMatches && matches)! : pathMatches ? JsonValue.Create("unverified")! : JsonValue.Create(false)!;
        }
        return new() { ["path"] = resolved, ["exists"] = true, ["version"] = version, ["sha256"] = sha256,
            ["isChromeForTesting"] = verified, ["manifest"] = manifest ?? (JsonNode)JsonValue.Create("not found")! };
    }

    public static JsonObject Inspect(string runDir)
    {
        if (!Directory.Exists(runDir)) throw new CrerException(2, $"artifact directory is not a directory: {runDir}");
        var files = Directory.EnumerateFiles(runDir).Select(path => new FileInfo(path)).OrderBy(f => f.Name, StringComparer.CurrentCulture).ToArray();
        var result = new JsonObject { ["runDir"] = runDir,
            ["screenshots"] = new JsonArray(files.Where(f => f.Name.EndsWith(".png", StringComparison.Ordinal)).Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)new JsonObject { ["name"] = f.Name, ["bytes"] = f.Length }).ToArray()) };
        foreach (var key in new[] { "run", "display", "foreground", "launch" })
            try { if (JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, key + ".json"))) is JsonObject json) result[key] = json; } catch { }
        try
        {
            var steps = File.ReadAllText(Path.Combine(runDir, "steps.ndjson")).Trim().Split('\n').Where(line => line.Length != 0).Select(line => JsonNode.Parse(line)).ToArray();
            result["steps"] = new JsonObject { ["count"] = steps.Length, ["failed"] = steps.Count(step => Json.Text(step?["status"]) == "failed") };
        }
        catch { }
        return result;
    }
}
