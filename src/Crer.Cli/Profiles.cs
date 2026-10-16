using System.Text.Json.Nodes;

namespace Crer;

public static class Profiles
{
    public static string? Configured(Scenario scenario, CliOptions options)
    {
        if (options.ProfileDir is string path) return path;
        var profile = Json.Text(scenario.Browser["profile"]);
        return profile.StartsWith("persistent:", StringComparison.Ordinal) ? profile[11..] : null;
    }

    public static string PersistentDirectory(string configured)
    {
        var workspace = RealPath(Environment.CurrentDirectory);
        var metadata = Path.Combine(workspace, ".crer");
        var root = Path.Combine(metadata, "profiles");
        if (Directory.Exists(metadata) && !RealPath(metadata).Equals(metadata, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root) && !RealPath(root).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new CrerException(2, "--profile-dir resolves outside .crer\\profiles");
        Directory.CreateDirectory(root);
        root = RealPath(root);
        var raw = configured.Trim();
        if (raw.Length == 0) throw new CrerException(2, "--profile-dir requires a directory");
        var value = raw.Replace('/', Path.DirectorySeparatorChar);
        if (value.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
            throw new CrerException(2, "--profile-dir must not contain .. and must be under .crer\\profiles");
        var candidate = Path.GetFullPath(value);
        if (!Inside(candidate, root)) throw new CrerException(2, "--profile-dir must be under .crer\\profiles");
        // Resolve every existing ancestor before creation; an intermediate junction must not
        // cause even the directory creation to write outside the dedicated profile root.
        var pending = new Stack<string>();
        var ancestor = candidate;
        while (!Directory.Exists(ancestor))
        {
            pending.Push(Path.GetFileName(ancestor));
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new CrerException(2, "profile ancestor is unavailable");
        }
        var resolved = RealPath(ancestor);
        if (!Inside(resolved, root)) throw new CrerException(2, "--profile-dir resolves outside .crer\\profiles");
        foreach (var part in pending) resolved = Path.Combine(resolved, part);
        Directory.CreateDirectory(resolved);
        resolved = RealPath(resolved);
        if (!Inside(resolved, root)) throw new CrerException(2, "--profile-dir resolves outside .crer\\profiles");
        return resolved;
    }

    public static bool Inside(string path, string parent) =>
        Path.GetFullPath(path).Equals(Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null) current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException($"could not resolve link: {current}");
        }
        return current;
    }

    public static FileStream Acquire(string directory)
    {
        var path = ContainedPath(directory, ".crer-profile.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new CrerException(3, $"persistent profile is already in use: {directory}: {e.Message}"); }
    }

    public static void Prepare(string directory)
    {
        ValidateLinks(directory);
        var defaults = ContainedPath(directory, "Default");
        var path = ContainedPath(directory, Path.Combine("Default", "Preferences"));
        Directory.CreateDirectory(defaults);
        var preferences = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new JsonObject();
        var profile = preferences["profile"] as JsonObject;
        if (profile == null) { profile = new(); preferences["profile"] = profile; }
        var translate = preferences["translate"] as JsonObject;
        if (translate == null) { translate = new(); preferences["translate"] = translate; }
        translate["enabled"] = false;
        preferences["credentials_enable_service"] = false;
        profile["password_manager_enabled"] = false;
        profile["password_manager_leak_detection"] = false;
        File.WriteAllText(path, preferences.ToJsonString());
    }

    private static void ValidateLinks(string directory)
    {
        var root = RealPath(directory);
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Do not traverse aliases or cycles. Every real directory below the
                    // root is visited separately, including an internal alias's target.
                    _ = ContainedPath(root, Path.GetRelativePath(root, entry));
                }
                else if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static string ContainedPath(string directory, string relative)
    {
        var root = RealPath(directory);
        var path = Path.Combine(root, relative);
        try
        {
            if (!Inside(RealPath(path), root)) throw new CrerException(2, $"profile path resolves outside its dedicated directory: {relative}");
        }
        catch (IOException e) { throw new CrerException(2, $"profile path could not be resolved: {relative}: {e.Message}"); }
        return path;
    }
}
