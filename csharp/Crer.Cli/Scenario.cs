using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Crer;

/// <summary>The supported v1 scenario subset. Validation always precedes browser creation.</summary>
public sealed class Scenario
{
    public JsonObject Root { get; }
    public JsonObject Browser { get; }
    public JsonObject Window { get; }
    public JsonObject Display { get; }
    public JsonObject Playback { get; }
    public JsonArray Steps { get; }
    public string Name => Root["name"]!.GetValue<string>();

    private Scenario(JsonObject root, JsonObject browser, JsonObject window,
        JsonObject display, JsonObject playback, JsonArray steps)
    {
        Root = root;
        Browser = browser;
        Window = window;
        Display = display;
        Playback = playback;
        Steps = steps;
    }

    public static Scenario Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (CrerException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Invalid($"could not read scenario '{path}': {ex.Message}"); }
    }

    // RepresentationModel reads YAML syntax without reflection-based object deserialization.
    // JSON values are constructed explicitly, including in Native AOT builds.
    public static Scenario Parse(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1) throw Invalid("scenario must contain exactly one YAML document");
            return Validate(ConvertNode(stream.Documents[0].RootNode,
                new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), 0));
        }
        catch (CrerException) { throw; }
        catch (Exception ex) when (ex is YamlException or ArgumentException or FormatException or OverflowException)
        { throw Invalid($"invalid YAML: {ex.Message}"); }
    }

    public static Scenario Validate(JsonNode? value)
    {
        var root = Mapping(value, "scenario");
        Keys(root, "scenario", "version", "name", "browser", "playback", "steps");
        if (!Number(root["version"], out var version) || version != 1 || !IsString(root["name"]))
            throw Invalid("scenario requires version: 1, name, and steps");
        var steps = root["steps"] as JsonArray ?? throw Invalid("scenario.steps must be a step array");
        var browser = Mapping(root["browser"], "browser");
        Keys(browser, "browser", "chrome", "profile", "initial_url", "window", "display");
        OptionalString(browser, "chrome", "browser");
        HttpUrl(browser["initial_url"], "browser.initial_url");
        if (browser.ContainsKey("profile") && String(browser["profile"]) != "ephemeral")
            throw Invalid("browser.profile supports only ephemeral in the C# CLI");

        var window = OptionalMapping(browser, "window", "browser");
        Keys(window, "browser.window", "bounds", "content", "viewport");
        if (window.ContainsKey("bounds"))
        {
            var bounds = Mapping(window["bounds"], "browser.window.bounds");
            Keys(bounds, "browser.window.bounds", "left", "top", "width", "height");
            foreach (var key in new[] { "left", "top" })
                Integer(bounds[key], $"browser.window.bounds.{key}", false);
            foreach (var key in new[] { "width", "height" })
                if (bounds.ContainsKey(key)) Integer(bounds[key], $"browser.window.bounds.{key}", true);
        }
        foreach (var key in new[] { "content", "viewport" })
        {
            if (!window.ContainsKey(key)) continue;
            var size = Mapping(window[key], $"browser.window.{key}");
            Keys(size, $"browser.window.{key}", "width", "height");
            Positive(size["width"], $"browser.window.{key}.width");
            Positive(size["height"], $"browser.window.{key}.height");
        }
        var display = OptionalMapping(browser, "display", "browser");
        Keys(display, "browser.display", "expected_dpr", "browser_zoom", "zoom_check");
        foreach (var key in new[] { "expected_dpr", "browser_zoom" })
            if (display.ContainsKey(key)) Positive(display[key], $"browser.display.{key}");
        if (display.ContainsKey("zoom_check") && String(display["zoom_check"]) != "strict")
            throw Invalid("browser.display.zoom_check supports only strict in the C# CLI");

        var playback = OptionalMapping(root, "playback", "scenario");
        Keys(playback, "playback", "seed", "step_delay_ms", "artifacts", "jitter", "timeouts", "on_failure");
        if (playback.ContainsKey("seed"))
        {
            var seed = String(playback["seed"]);
            if (seed is null || !Regex.IsMatch(seed, "^[0-9]+$", RegexOptions.CultureInvariant)
                || !ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw Invalid("playback.seed must be a uint64 decimal string");
        }
        OptionalNonNegative(playback, "step_delay_ms", "playback");
        if (playback.ContainsKey("jitter")) DisabledJitter(playback["jitter"], "playback.jitter");
        if (playback.ContainsKey("timeouts"))
        {
            var timeouts = Mapping(playback["timeouts"], "playback.timeouts");
            Keys(timeouts, "playback.timeouts", "navigation_ms", "action_ms");
            OptionalNonNegative(timeouts, "navigation_ms", "playback.timeouts");
            OptionalNonNegative(timeouts, "action_ms", "playback.timeouts");
        }
        if (playback.ContainsKey("artifacts"))
        {
            var artifacts = Mapping(playback["artifacts"], "playback.artifacts");
            Keys(artifacts, "playback.artifacts", "template_screenshots");
            if (artifacts.ContainsKey("template_screenshots")
                && String(artifacts["template_screenshots"]) is not ("all" or "failure-only"))
                throw Invalid("playback.artifacts.template_screenshots must be all or failure-only");
        }
        if (playback.ContainsKey("on_failure"))
        {
            var policies = Mapping(playback["on_failure"], "playback.on_failure");
            Keys(policies, "playback.on_failure", "default", "navigation", "timeout", "action", "assertion", "jitter_bounds", "template");
            foreach (var policy in policies)
                if (String(policy.Value) != "abort")
                    throw Invalid($"playback.on_failure.{policy.Key} supports only abort in the C# CLI");
        }

        for (var i = 0; i < steps.Count; i++) ValidateStep(steps[i], $"steps[{i}]");
        return new Scenario(root, browser, window, display, playback, steps);
    }

    private static void ValidateStep(JsonNode? node, string label)
    {
        var step = Mapping(node, label);
        var action = String(step["do"]) ?? throw Invalid($"{label}.do is required");
        switch (action)
        {
            case "click":
                Keys(step, label, "do", "at", "jitter", "count", "hold_ms", "delay_ms");
                var point = Mapping(step["at"], $"{label}.at");
                Keys(point, $"{label}.at", "x", "y");
                foreach (var axis in new[] { "x", "y" })
                    if (!Number(point[axis], out _)) throw Invalid($"{label}.at.{axis} must be a finite number");
                if (step.ContainsKey("jitter")) DisabledJitter(step["jitter"], $"{label}.jitter");
                if (step.ContainsKey("count")) Integer(step["count"], $"{label}.count", true);
                OptionalNonNegative(step, "hold_ms", label);
                break;
            case "wait_for":
            case "assert":
                Keys(step, label, "do", "url", "state", "locator_hint", "delay_ms");
                if (!step.ContainsKey("url") && !step.ContainsKey("state") && !step.ContainsKey("locator_hint"))
                    throw Invalid($"{label} requires url, state, or locator_hint");
                if (step.ContainsKey("url")) HttpUrl(step["url"], $"{label}.url", true);
                if (step.ContainsKey("state"))
                {
                    var state = String(step["state"]);
                    if (state != "complete" && !(action == "wait_for" && state is "visible" or "network_idle"))
                        throw Invalid($"{label}.state is not supported for {action}");
                    if (state == "visible" && !step.ContainsKey("locator_hint"))
                        throw Invalid($"{label}.state visible requires locator_hint");
                }
                if (step.ContainsKey("locator_hint"))
                {
                    var locator = Mapping(step["locator_hint"], $"{label}.locator_hint");
                    Keys(locator, $"{label}.locator_hint", "role", "name", "text");
                    foreach (var key in new[] { "role", "name", "text" })
                        OptionalString(locator, key, $"{label}.locator_hint");
                }
                break;
            case "screenshot":
                Keys(step, label, "do", "name", "delay_ms");
                var name = String(step["name"]);
                if (name is null || !Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
                    throw Invalid($"{label}.name must be a safe artifact filename");
                break;
            case "sleep":
                Keys(step, label, "do", "ms");
                NonNegative(step["ms"], $"{label}.ms");
                break;
            default: throw Invalid($"{label}.do is not supported by the C# CLI: {action}");
        }
        OptionalNonNegative(step, "delay_ms", label);
    }

    private static void DisabledJitter(JsonNode? value, string label)
    {
        var jitter = Mapping(value, label);
        Keys(jitter, label, "enabled");
        if (jitter["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var flag) || flag)
            throw Invalid($"{label} supports only {{ enabled: false }} in the C# CLI");
    }

    private static JsonObject Mapping(JsonNode? value, string label) => value as JsonObject
        ?? throw Invalid($"{label} must be a mapping");
    private static JsonObject OptionalMapping(JsonObject parent, string key, string label) =>
        parent.ContainsKey(key) ? Mapping(parent[key], $"{label}.{key}") : new JsonObject();
    private static void Keys(JsonObject value, string label, params string[] allowed)
    {
        foreach (var pair in value)
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal))
                throw Invalid($"{label}.{pair.Key} is not supported by the C# CLI");
    }
    private static string? String(JsonNode? value) => value is JsonValue scalar
        && scalar.TryGetValue<string>(out var result) ? result : null;
    private static bool IsString(JsonNode? value) => String(value) is not null;
    private static void OptionalString(JsonObject parent, string key, string label)
    {
        if (parent.ContainsKey(key) && !IsString(parent[key])) throw Invalid($"{label}.{key} must be a string");
    }
    private static bool Number(JsonNode? value, out double result)
    {
        result = 0;
        return value is JsonValue scalar && scalar.TryGetValue<double>(out result) && double.IsFinite(result);
    }
    private static void Positive(JsonNode? value, string label)
    {
        if (!Number(value, out var number) || number <= 0) throw Invalid($"{label} must be a positive number");
    }
    private static void NonNegative(JsonNode? value, string label)
    {
        if (!Number(value, out var number) || number < 0) throw Invalid($"{label} must be a non-negative number");
    }
    private static void OptionalNonNegative(JsonObject parent, string key, string label)
    {
        if (parent.ContainsKey(key)) NonNegative(parent[key], $"{label}.{key}");
    }
    private static void Integer(JsonNode? value, string label, bool positive)
    {
        if (!Number(value, out var number) || Math.Truncate(number) != number || positive && number <= 0)
            throw Invalid($"{label} must be a {(positive ? "positive " : "")}integer");
    }
    private static void HttpUrl(JsonNode? value, string label, bool wildcard = false)
    {
        var text = String(value);
        if (text is null || !Uri.TryCreate(wildcard ? text.Replace("*", "x", StringComparison.Ordinal) : text,
            UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw Invalid($"{label} must be a valid http or https URL");
    }

    private static JsonNode? ConvertNode(YamlNode node, HashSet<YamlNode> ancestors, int depth)
    {
        if (depth > 64 || !ancestors.Add(node)) throw Invalid("YAML aliases must not be recursive and nesting must not exceed 64 levels");
        try
        {
            switch (node)
            {
                case YamlMappingNode mapping:
                    CollectionTag(mapping, "map");
                    var result = new JsonObject();
                    // YAML merge aliases contribute defaults; explicit keys always win.
                    foreach (var pair in mapping.Children.Where(pair => IsMergeKey(pair.Key)))
                    {
                        var merged = ConvertNode(pair.Value, ancestors, depth + 1);
                        var sources = merged is JsonArray list ? list.ToArray() : [merged];
                        foreach (var source in sources)
                        {
                            if (source is not JsonObject defaults) throw Invalid("YAML merge requires mappings or a sequence of mappings");
                            foreach (var item in defaults)
                                if (!result.ContainsKey(item.Key)) result[item.Key] = item.Value?.DeepClone();
                        }
                    }
                    foreach (var pair in mapping.Children)
                    {
                        if (IsMergeKey(pair.Key)) continue;
                        if (pair.Key is not YamlScalarNode key || key.Value is null)
                            throw Invalid("YAML mapping keys must be strings");
                        result[key.Value] = ConvertNode(pair.Value, ancestors, depth + 1);
                    }
                    return result;
                case YamlSequenceNode sequence:
                    CollectionTag(sequence, "seq");
                    var array = new JsonArray();
                    foreach (var item in sequence.Children) array.Add(ConvertNode(item, ancestors, depth + 1));
                    return array;
                case YamlScalarNode scalar:
                    return ConvertScalar(scalar);
                default: throw Invalid("unsupported YAML node");
            }
        }
        finally { ancestors.Remove(node); }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var tag = scalar.Tag.ToString();
        if (tag is "tag:yaml.org,2002:str" or "!")
            return JsonValue.Create(value);
        if (tag is not ("?" or "!" or "" or "tag:yaml.org,2002:null" or "tag:yaml.org,2002:bool"
            or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float"))
            throw Invalid($"unsupported YAML scalar tag: {tag}");
        var explicitTag = tag.StartsWith("tag:", StringComparison.Ordinal);
        if (!explicitTag && scalar.Style != ScalarStyle.Plain && scalar.Style != ScalarStyle.Any)
            return JsonValue.Create(value);
        if (!explicitTag && Regex.IsMatch(value,
            "^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}(?:[Tt]|[ \\t]+)[0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]*)?(?:[ \\t]*(?:Z|[-+][0-9]{1,2}(?::[0-9]{2})?))?)$",
            RegexOptions.CultureInvariant))
            throw Invalid("YAML timestamp is not a v1 scenario scalar; quote it to use a string");
        if (tag == "tag:yaml.org,2002:null" || !explicitTag && value is "" or "~" or "null" or "Null" or "NULL")
        {
            if (value is not ("" or "~" or "null" or "Null" or "NULL")) throw Invalid("invalid YAML null scalar");
            return null;
        }
        if (tag == "tag:yaml.org,2002:bool" || !explicitTag && value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
            if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
            throw Invalid("invalid YAML bool scalar");
        }
        // Match the scalar resolution used by the locked @std/yaml parser, rather than
        // treating every string accepted by Double.TryParse as an implicit YAML number.
        var numberText = value.Replace("_", "", StringComparison.Ordinal);
        // YAML's non-finite numeric scalars cannot be represented by the JSON runtime.
        if (Regex.IsMatch(value, "^(?:[+-]?\\.(?:inf|Inf|INF)|\\.(?:nan|NaN|NAN))$", RegexOptions.CultureInvariant))
            throw Invalid("YAML numeric values must be finite");
        var decimalSyntax = !value.EndsWith('_') && Regex.IsMatch(value,
            "^(?:[-+]?(?:0|[1-9][0-9_]*)(?:\\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|\\.[0-9_]+(?:[eE][-+]?[0-9]+)?)$",
            RegexOptions.CultureInvariant);
        var radixSyntax = Regex.IsMatch(value, "^[+-]?0(?:x[0-9a-fA-F_]*[0-9a-fA-F]|b[01_]*[01]|[0-7_]*[0-7])$", RegexOptions.CultureInvariant);
        // The locked @std/yaml 1.2.0 int resolver accepts zero/radix forms, while
        // ordinary decimal numbers are resolved by its float type.
        if (tag == "tag:yaml.org,2002:int" && !radixSyntax && !Regex.IsMatch(value, "^[+-]?0$", RegexOptions.CultureInvariant))
            throw Invalid("invalid YAML int scalar for the existing parser");
        if (tag == "tag:yaml.org,2002:float" && !decimalSyntax)
            throw Invalid("invalid YAML float scalar");
        if (decimalSyntax && double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (!double.IsFinite(number)) throw Invalid("YAML numeric values must be finite");
            if (tag == "tag:yaml.org,2002:int" && Math.Truncate(number) != number)
                throw Invalid("invalid YAML int scalar");
            return JsonValue.Create(number);
        }
        if (radixSyntax)
        {
            var negative = numberText.StartsWith('-');
            var digits = numberText.TrimStart('+', '-');
            var radix = digits[1] switch { 'x' => 16, 'b' => 2, _ => 8 };
            var start = radix == 8 ? 1 : 2;
            double integer = 0;
            foreach (var digit in digits[start..]) integer = integer * radix + (digit <= '9' ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10);
            if (!double.IsFinite(integer)) throw Invalid("YAML numeric values must be finite");
            return JsonValue.Create(negative ? -integer : integer);
        }
        if (explicitTag) throw Invalid($"invalid YAML numeric scalar: {value}");
        return JsonValue.Create(value);
    }

    private static void CollectionTag(YamlNode node, string kind)
    {
        var tag = node.Tag.ToString();
        if (tag is not ("?" or "!" or "") && tag != $"tag:yaml.org,2002:{kind}")
            throw Invalid($"unsupported YAML collection tag: {tag}");
    }

    private static bool IsMergeKey(YamlNode node) => node is YamlScalarNode scalar && scalar.Value == "<<"
        && (scalar.Tag.ToString() == "tag:yaml.org,2002:merge"
            || scalar.Tag.ToString() is "?" or "" && scalar.Style is ScalarStyle.Plain or ScalarStyle.Any);

    private static CrerException Invalid(string message) => new(2, message);
}
