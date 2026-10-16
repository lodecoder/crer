using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Crer;

public sealed class ExecutionContext
{
    public bool Stopped { get; private set; }
    private readonly List<string> callStack = [];
    public void Stop() => Stopped = true;
    public Action EnterFunction(string name)
    {
        if (callStack.Contains(name, StringComparer.Ordinal))
            throw new InvalidOperationException($"recursive function call: {string.Join(" -> ", callStack.Append(name))}");
        callStack.Add(name);
        return () =>
        {
            if (callStack.Count == 0 || callStack[^1] != name) throw new InvalidOperationException("function call stack is unbalanced");
            callStack.RemoveAt(callStack.Count - 1);
        };
    }
}

public sealed record StepFrame<T>(JsonObject Step, string Index, T? CachedTemplate) where T : class;
public delegate Task ExecuteChildren<T>(JsonArray steps, string? parentIndex = null, T? initialCache = null) where T : class;

/// <summary>Stable nested indexes, stop state and a cache consumed by the first child only.</summary>
public sealed class StepExecutor<T>(ExecutionContext context,
    Func<StepFrame<T>, ExecuteChildren<T>, Task> handler) where T : class
{
    public async Task ExecuteAsync(JsonArray steps, string parentIndex = "", T? initialCache = null)
    {
        var nextCache = initialCache;
        for (var offset = 0; offset < steps.Count; offset++)
        {
            if (context.Stopped) return;
            var cache = nextCache; nextCache = null;
            var index = parentIndex.Length > 0 ? $"{parentIndex}.{offset}" : offset.ToString(CultureInfo.InvariantCulture);
            await handler(new(steps[offset]!.AsObject(), index, cache),
                (children, parent, childCache) => ExecuteAsync(children, parent ?? index, childCache));
        }
    }
}

public static class StepArguments
{
    private static readonly Regex Reference = new(@"\$\{([A-Za-z_][A-Za-z0-9_-]*)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static JsonNode? Expand(JsonNode? value, JsonObject args)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            var whole = Reference.Match(text);
            if (whole.Success && whole.Index == 0 && whole.Length == text.Length && args.ContainsKey(whole.Groups[1].Value))
                return args[whole.Groups[1].Value]?.DeepClone();
            return JsonValue.Create(Reference.Replace(text, match => args.TryGetPropertyValue(match.Groups[1].Value, out var argument) ? String(argument) : match.Value));
        }
        if (value is JsonArray array) return new JsonArray(array.Select(item => Expand(item, args)).ToArray());
        if (value is JsonObject obj)
        {
            var expanded = new JsonObject();
            foreach (var (key, item) in obj) expanded[key] = Expand(item, args);
            return expanded;
        }
        return value?.DeepClone();
    }

    public static string String(JsonNode? value) => ActionNumber.String(value);

    public static string Weekday(DateTimeOffset instant, string? zone = null)
    {
        var time = TimeZoneInfo.ConvertTime(instant, zone == null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zone));
        return time.DayOfWeek switch { DayOfWeek.Monday => "mon", DayOfWeek.Tuesday => "tue", DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu", DayOfWeek.Friday => "fri", DayOfWeek.Saturday => "sat", _ => "sun" };
    }
}
