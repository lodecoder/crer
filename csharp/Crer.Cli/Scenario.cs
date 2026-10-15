using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acornima;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Crer;

public sealed record ScenarioFunction(string[] Parameters, JsonArray Steps);

/// <summary>The v1 scenario contract. Validation always precedes browser creation.</summary>
public sealed class Scenario
{
    public JsonObject Root { get; }
    public JsonObject Browser { get; }
    public JsonObject Window { get; }
    public JsonObject Display { get; }
    public JsonObject Playback { get; }
    public JsonArray Steps { get; }
    public IReadOnlyDictionary<string, ScenarioFunction> Functions { get; }
    public string Name => Root["name"]!.GetValue<string>();

    private Scenario(JsonObject root, JsonObject browser, JsonObject window,
        JsonObject display, JsonObject playback, JsonArray steps,
        Dictionary<string, ScenarioFunction> functions)
    {
        Root = root;
        Browser = browser;
        Window = window;
        Display = display;
        Playback = playback;
        Steps = steps;
        Functions = functions;
    }

    public static Scenario Load(string path) => Validate(LoadYaml(path));
    public static Scenario Parse(string yaml) => Validate(ParseYaml(yaml));
    public static JsonObject LoadPlan(string path) => ValidatePlan(LoadYaml(path));
    public static JsonObject ParsePlan(string yaml) => ValidatePlan(ParseYaml(yaml));

    public static JsonNode? LoadYaml(string path)
    {
        try { return ParseYaml(File.ReadAllText(path)); }
        catch (CrerException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Invalid($"could not read YAML '{path}': {ex.Message}"); }
    }

    // RepresentationModel reads YAML without reflection-based object deserialization.
    // JSON values are constructed explicitly, including in Native AOT builds.
    public static JsonNode? ParseYaml(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1) throw Invalid("scenario must contain exactly one YAML document");
            return ConvertNode(stream.Documents[0].RootNode,
                new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), 0);
        }
        catch (CrerException) { throw; }
        catch (Exception ex) when (ex is YamlException or ArgumentException or FormatException or OverflowException)
        { throw Invalid($"invalid YAML: {ex.Message}"); }
    }

    public static Scenario Validate(JsonNode? value)
    {
        var root = Mapping(value, "scenario");
        Keys(root, "scenario", "version", "name", "browser", "playback", "functions", "steps");
        if (!Number(root["version"], out var version) || version != 1 || !IsString(root["name"]) || root["steps"] is not JsonArray steps)
            throw Invalid("scenario requires version: 1, name, and steps");
        var browser = Mapping(root["browser"], "browser");
        Keys(browser, "browser", "chrome", "profile", "initial_url", "block_urls", "window", "display");
        ValidateBlockUrls(browser);
        OptionalString(browser, "chrome", "browser");
        if (!IsString(browser["initial_url"])) throw Invalid("browser.initial_url is required");
        HttpUrl(browser["initial_url"], "browser.initial_url");
        OptionalString(browser, "profile", "browser");
        if (String(browser["profile"]) is { } profile && profile != "ephemeral")
        {
            if (!profile.StartsWith("persistent:", StringComparison.Ordinal))
                throw Invalid("browser.profile must be ephemeral or persistent:<directory>");
            if (string.IsNullOrWhiteSpace(profile["persistent:".Length..]))
                throw Invalid("browser.profile persistent: requires a directory");
        }
        var window = OptionalMapping(browser, "window", "browser");
        Keys(window, "browser.window", "bounds", "foreground", "foreground_mode", "opacity", "content", "viewport");
        if (window.ContainsKey("opacity") && (!Number(window["opacity"], out var opacity) || opacity < 0 || opacity > 1))
            throw Invalid("browser.window.opacity must be a finite number from 0 to 1");
        if (window.ContainsKey("bounds"))
        {
            var bounds = Mapping(window["bounds"], "browser.window.bounds");
            Keys(bounds, "browser.window.bounds", "left", "top", "width", "height");
            foreach (var key in new[] { "left", "top" }) Integer(bounds[key], $"browser.window.bounds.{key}", false);
            foreach (var key in new[] { "width", "height" })
                if (bounds.ContainsKey(key)) Integer(bounds[key], $"browser.window.bounds.{key}", true);
        }
        OptionalBoolean(window, "foreground", "browser.window");
        EnumValue(window, "foreground_mode", "browser.window", "always", "once");
        foreach (var key in new[] { "content", "viewport" })
        {
            if (!window.ContainsKey(key)) continue;
            var size = Mapping(window[key], $"browser.window.{key}");
            Keys(size, $"browser.window.{key}", "width", "height");
            if (!Number(size["width"], out var width) || width <= 0 || !Number(size["height"], out var height) || height <= 0)
                throw Invalid($"browser.window.{key} requires positive width and height");
        }
        var display = OptionalMapping(browser, "display", "browser");
        Keys(display, "browser.display", "expected_dpr", "browser_zoom", "zoom_check");
        foreach (var key in new[] { "expected_dpr", "browser_zoom" })
            if (display.ContainsKey(key)) Positive(display[key], $"browser.display.{key}");
        if (display.ContainsKey("zoom_check") && String(display["zoom_check"]) is not ("strict" or "advisory" or "off"))
            throw Invalid("browser.display.zoom_check must be strict, advisory, or off");

        // These truthiness checks deliberately preserve the fixed Deno validator's behavior.
        var playback = Truthy(root["playback"]) ? Mapping(root["playback"], "playback") : new JsonObject();
        Keys(playback, "playback", "seed", "speed", "step_delay_ms", "log_color", "template", "artifacts", "jitter", "timeouts", "on_failure");
        if (playback.ContainsKey("speed")) Positive(playback["speed"], "playback.speed");
        if (playback.ContainsKey("timeouts"))
        {
            var timeouts = Mapping(playback["timeouts"], "playback.timeouts");
            Keys(timeouts, "playback.timeouts", "navigation_ms", "action_ms");
            OptionalNonNegative(timeouts, "navigation_ms", "playback.timeouts");
            OptionalNonNegative(timeouts, "action_ms", "playback.timeouts");
        }
        if (playback.ContainsKey("log_color") && String(playback["log_color"]) is not ("default" or "black" or "red" or "green" or "yellow" or "blue" or "magenta" or "cyan" or "white"))
            throw Invalid("playback.log_color must be one of: default, black, red, green, yellow, blue, magenta, cyan, white");
        if (Truthy(playback["jitter"])) ValidateJitter(playback["jitter"], "playback.jitter", []);
        if (Truthy(playback["template"])) ValidateTemplate(playback["template"], "playback.template", false, []);
        if (playback.ContainsKey("artifacts"))
        {
            var artifacts = Mapping(playback["artifacts"], "playback.artifacts");
            Keys(artifacts, "playback.artifacts", "template_screenshots");
            EnumValue(artifacts, "template_screenshots", "playback.artifacts", "all", "failure-only");
        }
        if (Truthy(playback["on_failure"])) FailurePolicies(playback["on_failure"], "playback.on_failure",
            "default", "navigation", "timeout", "action", "assertion", "jitter_bounds", "template");
        OptionalNonNegative(playback, "step_delay_ms", "playback");
        if (playback.ContainsKey("seed"))
        {
            var seed = String(playback["seed"]);
            if (seed is null || !Regex.IsMatch(seed, "^[0-9]+$", RegexOptions.CultureInvariant))
                throw Invalid("playback.seed must be a uint64 decimal string");
            // BigInt accepts arbitrarily many leading zeroes; UInt64.TryParse does not.
            var digits = seed.TrimStart('0');
            if (digits.Length > 20 || digits.Length == 20 && string.CompareOrdinal(digits, "18446744073709551615") > 0)
                throw Invalid("playback.seed exceeds uint64");
        }
        var definitions = new Dictionary<string, ScenarioFunction>(StringComparer.Ordinal);
        var functions = root.ContainsKey("functions") ? Mapping(root["functions"], "functions") : new JsonObject();
        foreach (var (name, raw) in functions)
        {
            if (!Identifier(name)) throw Invalid($"functions.{name} must be an identifier");
            if (raw is JsonArray body)
            {
                definitions.Add(name, new ScenarioFunction([], body));
                continue;
            }
            var definition = Mapping(raw, $"functions.{name}");
            Keys(definition, $"functions.{name}", "params", "steps");
            if (definition["steps"] is not JsonArray functionSteps) throw Invalid($"functions.{name}.steps must be a step array");
            var parameters = definition.ContainsKey("params") ? definition["params"] as JsonArray : new JsonArray();
            if (parameters is null || parameters.Any(p => String(p) is not { } text || !Identifier(text))
                || parameters.Select(String).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
                throw Invalid($"functions.{name}.params must be unique identifiers");
            definitions.Add(name, new ScenarioFunction(parameters.Select(p => String(p)!).ToArray(), functionSteps));
        }
        foreach (var (name, definition) in definitions)
            for (var i = 0; i < definition.Steps.Count; i++)
                ValidateStep(definition.Steps[i], $"functions.{name}[{i}]", definitions, new HashSet<string>(definition.Parameters, StringComparer.Ordinal), false);
        for (var i = 0; i < steps.Count; i++) ValidateStep(steps[i], $"steps[{i}]", definitions, [], false);
        return new Scenario(root, browser, window, display, playback, steps, definitions);
    }

    public static JsonObject ValidatePlan(JsonNode? value)
    {
        var plan = Mapping(value, "plan");
        Keys(plan, "plan", "version", "name", "max_parallel", "browser_session", "timeouts", "on_failure", "run");
        if (!Number(plan["version"], out var version) || version != 1 || !IsString(plan["name"]) || !Truthy(plan["run"]))
            throw Invalid("plan requires version: 1, name, and run");
        if (plan.ContainsKey("max_parallel")) Integer(plan["max_parallel"], "plan.max_parallel", true);
        if (plan.ContainsKey("browser_session"))
        {
            var session = Mapping(plan["browser_session"], "plan.browser_session");
            Keys(session, "plan.browser_session", "reuse", "focus");
            if (String(session["reuse"]) != "same-profile") throw Invalid("plan.browser_session.reuse must be same-profile");
            EnumValue(session, "focus", "plan.browser_session", "once", "before-step");
        }
        if (Truthy(plan["timeouts"]))
        {
            var timeouts = Mapping(plan["timeouts"], "plan.timeouts");
            Keys(timeouts, "plan.timeouts", "worker_ms");
            OptionalNonNegative(timeouts, "worker_ms", "plan.timeouts");
        }
        if (Truthy(plan["on_failure"])) FailurePolicies(plan["on_failure"], "plan.on_failure", "default", "scenario_failure", "timeout", "environment");
        ValidatePlanNode(plan["run"], "plan.run");
        return plan;
    }

    private static void ValidatePlanNode(JsonNode? value, string label)
    {
        var node = Mapping(value, label);
        Keys(node, label, "scenario", "serial", "parallel");
        if (new[] { "scenario", "serial", "parallel" }.Count(node.ContainsKey) != 1)
            throw Invalid($"{label} must have exactly one of scenario, serial, or parallel");
        if (node.ContainsKey("scenario"))
        {
            if (!IsString(node["scenario"])) throw Invalid($"{label}.scenario must be a string");
            return;
        }
        if (node.ContainsKey("serial"))
        {
            if (node["serial"] is not JsonArray serial) throw Invalid($"{label}.serial must be an array");
            for (var i = 0; i < serial.Count; i++) ValidatePlanNode(serial[i], $"{label}.serial[{i}]");
            return;
        }
        var parallel = Mapping(node["parallel"], $"{label}.parallel");
        Keys(parallel, $"{label}.parallel", "fail_fast", "jobs");
        if (parallel["jobs"] is not JsonArray jobs) throw Invalid($"{label}.parallel.jobs must be an array");
        OptionalBoolean(parallel, "fail_fast", $"{label}.parallel");
        for (var i = 0; i < jobs.Count; i++) ValidatePlanNode(jobs[i], $"{label}.parallel.jobs[{i}]");
    }
    private static readonly Dictionary<string, string[]> StepKeys = new(StringComparer.Ordinal)
    {
        ["navigate"] = ["do", "url", "delay_ms"],
        ["wait_for"] = ["do", "url", "state", "locator_hint", "delay_ms"],
        ["click"] = ["do", "at", "template", "jitter", "count", "hold_ms", "then", "delay_ms"],
        ["double_click"] = ["do", "at", "jitter", "delay_ms"],
        ["mouse_move"] = ["do", "at", "delay_ms"],
        ["drag"] = ["do", "from", "to", "delay_ms"],
        ["scroll"] = ["do", "at", "delta", "delay_ms"],
        ["text"] = ["do", "value", "delay_ms"],
        ["key"] = ["do", "key", "delay_ms"],
        ["key_chord"] = ["do", "keys", "delay_ms"],
        ["screenshot"] = ["do", "name", "delay_ms"],
        ["assert"] = ["do", "url", "state", "locator_hint", "delay_ms"],
        ["sleep"] = ["do", "ms"],
        ["log"] = ["do", "message", "delay_ms"],
        ["fail"] = ["do", "message"],
        ["if"] = ["do", "template", "weekdays", "time_zone", "equals", "then", "else", "delay_ms"],
        ["repeat"] = ["do", "count", "steps", "delay_ms"],
        ["repeat_until"] = ["do", "template", "state", "max_attempts", "on_limit", "steps", "delay_ms"],
        ["for_each_template"] = ["do", "template", "max_matches", "steps", "delay_ms"],
        ["call"] = ["do", "function", "args", "delay_ms"],
        ["break"] = ["do"],
    };
    private static readonly string[] TemplateMatchParameters = ["match_left", "match_top", "match_width", "match_height", "match_center_x", "match_center_y", "match_similarity"];

    private static void ValidateStep(JsonNode? value, string label, IReadOnlyDictionary<string, ScenarioFunction> definitions,
        HashSet<string> parameters, bool breakAllowed)
    {
        var step = Mapping(value, label);
        var action = String(step["do"]) ?? throw Invalid($"{label}.do is required");
        if (!StepKeys.TryGetValue(action, out var allowed)) throw Invalid($"{label}.do is not supported: {action}");
        foreach (var key in new[] { "at", "from", "to", "delta" })
            if (step.ContainsKey(key)) ValidatePoint(step[key], $"{label}.{key}", parameters);
        if (action == "navigate") HttpUrl(step["url"], $"{label}.url");
        if (action is "wait_for" or "assert")
        {
            if (step.ContainsKey("url")) HttpUrl(step["url"], $"{label}.url", true);
            if (!step.ContainsKey("url") && !step.ContainsKey("state") && !step.ContainsKey("locator_hint"))
                throw Invalid($"{label} requires url, state, or locator_hint");
            var state = String(step["state"]);
            if (step.ContainsKey("state") && state != "complete" && !(action == "wait_for" && state is "network_idle" or "visible"))
                throw Invalid($"{label}.state is not supported for {action}");
            if (state == "visible" && !step.ContainsKey("locator_hint")) throw Invalid($"{label}.state visible requires locator_hint");
        }
        if (action is "click" or "double_click" or "mouse_move" or "scroll")
            if (!step.ContainsKey("at") && !(action == "click" && step.ContainsKey("template")))
                throw Invalid($"{label} requires at or template");
        if (action == "drag" && (!step.ContainsKey("from") || !step.ContainsKey("to"))) throw Invalid($"{label} requires from and to");
        if (action == "scroll" && !step.ContainsKey("delta")) throw Invalid($"{label}.delta is required");
        if (action == "text" && !IsString(step["value"])) throw Invalid($"{label}.value must be a string for text");
        if (action == "key" && string.IsNullOrEmpty(String(step["key"]))) throw Invalid($"{label}.key must be a non-empty string for key");
        if (action == "screenshot" && (String(step["name"]) is not { } name || !Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)))
            throw Invalid($"{label}.name must be a safe artifact filename");
        if (action == "break" && !breakAllowed) throw Invalid($"{label}.break is supported only inside for_each_template.steps");
        if (action == "key_chord")
        {
            if (step["keys"] is not JsonArray keys || keys.Count < 2 || keys.Any(key => !IsString(key)))
                throw Invalid($"{label}.keys requires at least two strings");
            if (keys.Take(keys.Count - 1).Any(key => String(key) is not ("Alt" or "Control" or "Meta" or "Shift")))
                throw Invalid($"{label}.keys must contain modifiers followed by a final key");
        }
        if (action == "sleep" && !NonNegativeOrParameter(step["ms"], parameters)) throw Invalid($"{label}.ms must be a non-negative number for sleep");
        if (action == "log" && !IsString(step["message"])) throw Invalid($"{label}.message must be a string for log");
        if (action == "fail" && string.IsNullOrWhiteSpace(String(step["message"]))) throw Invalid($"{label}.message must be a non-empty string for fail");
        if (action == "call")
        {
            if (String(step["function"]) is not { } function || !definitions.TryGetValue(function, out var definition))
                throw Invalid($"{label}.function must name a defined function for call");
            var args = step.ContainsKey("args") ? Mapping(step["args"], $"{label}.args") : new JsonObject();
            if (args.Any(pair => !StringOrFiniteNumber(pair.Value))) throw Invalid($"{label}.args values must be strings or finite numbers");
            if (args.Count != definition.Parameters.Length || definition.Parameters.Any(p => !args.ContainsKey(p)))
                throw Invalid($"{label}.args must provide exactly the function parameters");
        }
        else if (step.ContainsKey("function")) throw Invalid($"{label}.function is supported only for call");
        else if (step.ContainsKey("args")) throw Invalid($"{label}.args is supported only for call");
        if (step.ContainsKey("delay_ms") && (action == "sleep" || !NonNegativeOrParameter(step["delay_ms"], parameters)))
            throw Invalid($"{label}.delay_ms must be a non-negative number on a non-sleep step");
        if (step.ContainsKey("hold_ms") && (action != "click" || !NonNegativeOrParameter(step["hold_ms"], parameters)))
            throw Invalid($"{label}.hold_ms must be a non-negative number on a click step");
        if (Truthy(step["locator_hint"]))
        {
            var locator = Mapping(step["locator_hint"], $"{label}.locator_hint");
            Keys(locator, $"{label}.locator_hint", "role", "name", "text");
            foreach (var key in new[] { "role", "name", "text" }) OptionalString(locator, key, $"{label}.locator_hint");
        }
        if (Truthy(step["jitter"])) ValidateJitter(step["jitter"], $"{label}.jitter", parameters);
        if (Truthy(step["template"]))
        {
            if (action is not ("click" or "if" or "repeat_until" or "for_each_template"))
                throw Invalid($"{label}.template is supported only for click, if, repeat_until, or for_each_template");
            if (Truthy(step["at"])) throw Invalid($"{label} cannot specify both at and template");
            if (Truthy(step["jitter"])) throw Invalid($"{label} cannot specify both jitter and template");
            ValidateTemplate(step["template"], $"{label}.template", true, parameters);
        }
        if (step.ContainsKey("on_missing")) throw Invalid($"{label}.on_missing must be nested under template");
        if (action == "if")
        {
            if (new[] { "template", "weekdays", "equals" }.Count(step.ContainsKey) != 1)
                throw Invalid($"{label} requires exactly one of template, weekdays, or equals for if");
            if (step.ContainsKey("equals"))
            {
                var equals = Mapping(step["equals"], $"{label}.equals");
                Keys(equals, $"{label}.equals", "left", "right");
                if (!StringOrFiniteNumber(equals["left"]) || !StringOrFiniteNumber(equals["right"]))
                    throw Invalid($"{label}.equals requires string or finite-number left and right");
            }
            if (step.ContainsKey("weekdays") && (step["weekdays"] is not JsonArray days || days.Count == 0
                || days.Any(day => String(day) is not ("mon" or "tue" or "wed" or "thu" or "fri" or "sat" or "sun"))))
                throw Invalid($"{label}.weekdays must be a non-empty array of mon through sun");
            if (step.ContainsKey("time_zone") && (String(step["time_zone"]) is not { } zone || !SupportedTimeZones.Contains(zone)))
                throw Invalid($"{label}.time_zone must be an IANA time zone string");
            ValidateChildren(step["then"], $"{label}.then", "for if", definitions, parameters, breakAllowed);
            if (step.ContainsKey("else")) ValidateChildren(step["else"], $"{label}.else", "for if", definitions, parameters, breakAllowed);
        }
        else if (action == "click" && Truthy(step["template"]) && step.ContainsKey("then"))
        {
            ValidateChildren(step["then"], $"{label}.then", "for template click", definitions, parameters, breakAllowed);
            if (new[] { "else", "weekdays", "time_zone", "equals" }.Any(step.ContainsKey))
                throw Invalid($"{label}.else, weekdays, time_zone, and equals are supported only for if");
        }
        else if (new[] { "then", "else", "weekdays", "time_zone", "equals" }.Any(step.ContainsKey))
            throw Invalid($"{label}.then is supported for if or template click; else, weekdays, time_zone, and equals are supported only for if");
        if (action == "repeat")
        {
            if (!IntegerOrParameter(step["count"], parameters, 0)) throw Invalid($"{label}.count must be a non-negative integer for repeat");
            ValidateChildren(step["steps"], $"{label}.steps", "for repeat", definitions, parameters, breakAllowed);
        }
        else if (action == "click")
        {
            if (step.ContainsKey("count") && !IntegerOrParameter(step["count"], parameters, 1))
                throw Invalid($"{label}.count must be a positive integer for click");
        }
        else if (step.ContainsKey("count")) throw Invalid($"{label}.count is supported only for repeat or click");
        if (action == "repeat_until")
        {
            if (!Truthy(step["template"])) throw Invalid($"{label}.template is required for repeat_until");
            if (String(step["state"]) is not ("visible" or "hidden")) throw Invalid($"{label}.state must be visible or hidden for repeat_until");
            if (!IntegerOrParameter(step["max_attempts"], parameters, 1)) throw Invalid($"{label}.max_attempts must be a positive integer for repeat_until");
            if (String(step["on_limit"]) is not ("fail" or "continue")) throw Invalid($"{label}.on_limit must be fail or continue for repeat_until");
            ValidateChildren(step["steps"], $"{label}.steps", "for repeat_until", definitions, parameters, breakAllowed);
        }
        else if (action == "for_each_template")
        {
            if (!Truthy(step["template"])) throw Invalid($"{label}.template is required for for_each_template");
            if (!IntegerOrParameter(step["max_matches"], parameters, 1, 100))
                throw Invalid($"{label}.max_matches must be an integer from 1 through 100 for for_each_template");
            var childParameters = new HashSet<string>(parameters, StringComparer.Ordinal);
            childParameters.UnionWith(TemplateMatchParameters);
            ValidateChildren(step["steps"], $"{label}.steps", "for for_each_template", definitions, childParameters, true);
        }
        else
        {
            if (new[] { "max_attempts", "on_limit", "max_matches" }.Any(step.ContainsKey))
                throw Invalid($"{label}.max_attempts, on_limit, and max_matches are supported only for repeat_until or for_each_template");
            if (step.ContainsKey("steps") && action != "repeat")
                throw Invalid($"{label}.steps is supported only for repeat, repeat_until, or for_each_template");
        }
        Keys(step, label, allowed);
    }

    private static void ValidateChildren(JsonNode? value, string label, string suffix,
        IReadOnlyDictionary<string, ScenarioFunction> definitions, HashSet<string> parameters, bool breakAllowed)
    {
        if (value is not JsonArray children) throw Invalid($"{label} must be a step array {suffix}");
        for (var i = 0; i < children.Count; i++) ValidateStep(children[i], $"{label}[{i}]", definitions, parameters, breakAllowed);
    }
    private static void ValidateJitter(JsonNode? value, string label, HashSet<string> parameters)
    {
        var jitter = Mapping(value, label);
        Keys(jitter, label, "enabled", "distribution", "radius_px", "min_distance_from_edge_px", "out_of_bounds");
        if (Boolean(jitter["enabled"]) == false) return;
        if (Boolean(jitter["enabled"]) != true || String(jitter["distribution"]) is not ("none" or "uniform" or "normal")
            || !NonNegativeOrParameter(jitter["radius_px"], parameters)
            || !NonNegativeOrParameter(jitter["min_distance_from_edge_px"], parameters)
            || String(jitter["out_of_bounds"]) is not ("fail" or "disable-for-step"))
            throw Invalid($"{label} is invalid");
    }

    private static void ValidateTemplate(JsonNode? value, string label, bool pathRequired, HashSet<string> parameters)
    {
        var template = Mapping(value, label);
        Keys(template, label, "path", "min_similarity", "random_inset_px", "on_missing");
        if (pathRequired && string.IsNullOrEmpty(String(template["path"]))) throw Invalid($"{label}.path is required");
        if (template.ContainsKey("min_similarity") && !ParameterReference(template["min_similarity"], parameters)
            && (!Number(template["min_similarity"], out var similarity) || similarity < 0 || similarity > 1))
            throw Invalid($"{label}.min_similarity must be between 0 and 1");
        if (template.ContainsKey("random_inset_px") && !NonNegativeOrParameter(template["random_inset_px"], parameters))
            throw Invalid($"{label}.random_inset_px must be non-negative");
        EnumValue(template, "on_missing", label, "fail", "skip");
    }

    private static void ValidatePoint(JsonNode? value, string label, HashSet<string> parameters)
    {
        var point = Mapping(value, label);
        Keys(point, label, "x", "y");
        foreach (var axis in new[] { "x", "y" })
            if (!Number(point[axis], out _) && !ParameterReference(point[axis], parameters)) throw Invalid($"{label}.{axis} must be a finite number");
    }

    private static void ValidateBlockUrls(JsonObject browser)
    {
        if (!browser.ContainsKey("block_urls")) return;
        if (browser["block_urls"] is not JsonArray patterns) throw Invalid("browser.block_urls must be an array of regex strings");
        for (var i = 0; i < patterns.Count; i++)
        {
            var label = $"browser.block_urls[{i}]";
            if (String(patterns[i]) is not { } pattern || string.IsNullOrWhiteSpace(pattern)) throw Invalid($"{label} must be a non-empty regex string");
            // Validate the ECMAScript source, not a .NET Regex translation. Matching is
            // performed by the browser's RegExp engine on the intercepted request URL.
            if (!Tokenizer.ValidateRegExp(pattern, "", out var error)) throw Invalid($"{label} must be a valid regular expression: {error}");
        }
    }

    private static void FailurePolicies(JsonNode? value, string label, params string[] allowed)
    {
        var policies = Mapping(value, label);
        foreach (var (kind, policy) in policies)
        {
            if (!allowed.Contains(kind, StringComparer.Ordinal)) throw Invalid($"{label}.{kind} is not supported");
            if (String(policy) is not ("abort" or "continue")) throw Invalid($"{label}.{kind} must be abort or continue");
        }
    }

    /// <summary>Preserves numeric whole references and expands embedded references as JavaScript String(number).</summary>
    public static JsonNode? ExpandArguments(JsonNode? value, JsonObject args)
    {
        if (String(value) is { } text)
        {
            var whole = Regex.Match(text, "^\\$\\{([A-Za-z_][A-Za-z0-9_-]*)\\}$", RegexOptions.CultureInvariant);
            if (whole.Success && args.ContainsKey(whole.Groups[1].Value)) return args[whole.Groups[1].Value]?.DeepClone();
            return JsonValue.Create(Regex.Replace(text, "\\$\\{([A-Za-z_][A-Za-z0-9_-]*)\\}", match =>
            {
                var name = match.Groups[1].Value;
                return args.ContainsKey(name) ? ArgumentString(args[name]) : match.Value;
            }, RegexOptions.CultureInvariant));
        }
        if (value is JsonArray array) return new JsonArray(array.Select(item => ExpandArguments(item, args)).ToArray());
        if (value is JsonObject mapping)
        {
            var result = new JsonObject();
            foreach (var (key, item) in mapping) result[key] = ExpandArguments(item, args);
            return result;
        }
        return value?.DeepClone();
    }

    private static string ArgumentString(JsonNode? value)
    {
        if (String(value) is { } text) return text;
        if (!Number(value, out var number)) return value is null ? "null" : value.ToJsonString();
        if (number == 0) return "0";
        var shortest = number.ToString("G", CultureInfo.InvariantCulture);
        var exponentIndex = shortest.IndexOfAny(['E', 'e']);
        if (exponentIndex < 0) return shortest;
        var exponent = int.Parse(shortest[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var mantissa = shortest[..exponentIndex];
        if (Math.Abs(number) >= 1e21 || Math.Abs(number) < 1e-6)
            return mantissa + "e" + (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        var negative = mantissa.StartsWith('-');
        var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal);
        var decimalIndex = exponent + 1;
        var expanded = decimalIndex <= 0 ? "0." + new string('0', -decimalIndex) + digits
            : decimalIndex >= digits.Length ? digits + new string('0', decimalIndex - digits.Length)
            : digits.Insert(decimalIndex, ".");
        return negative ? "-" + expanded : expanded;
    }

    private static JsonObject Mapping(JsonNode? value, string label) => value as JsonObject ?? throw Invalid($"{label} must be a mapping");
    private static JsonObject OptionalMapping(JsonObject parent, string key, string label) => parent.ContainsKey(key) ? Mapping(parent[key], $"{label}.{key}") : new JsonObject();
    private static void Keys(JsonObject value, string label, params string[] allowed)
    {
        foreach (var (key, _) in value) if (!allowed.Contains(key, StringComparer.Ordinal)) throw Invalid($"{label}.{key} is not supported");
    }
    private static string? String(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static bool IsString(JsonNode? value) => String(value) is not null;
    private static bool? Boolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag : null;
    private static bool Number(JsonNode? value, out double result)
    {
        result = 0;
        if (value is not JsonValue scalar) return false;
        if (scalar.TryGetValue<double>(out var real)) result = real;
        else if (scalar.TryGetValue<int>(out var integer)) result = integer;
        else if (scalar.TryGetValue<long>(out var signed)) result = signed;
        else if (scalar.TryGetValue<uint>(out var unsigned)) result = unsigned;
        else if (scalar.TryGetValue<ulong>(out var large)) result = large;
        else if (scalar.TryGetValue<float>(out var single)) result = single;
        else if (scalar.TryGetValue<decimal>(out var decimalValue)) result = (double)decimalValue;
        else return false;
        return double.IsFinite(result);
    }
    private static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonObject or JsonArray => true,
        _ when String(value) is { } text => text.Length != 0,
        _ when Boolean(value) is { } flag => flag,
        _ when Number(value, out var number) => number != 0,
        _ => true,
    };
    private static bool StringOrFiniteNumber(JsonNode? value) => IsString(value) || Number(value, out _);
    private static bool Identifier(string value) => Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);
    private static bool ParameterReference(JsonNode? value, HashSet<string> parameters)
    {
        if (String(value) is not { } text) return false;
        var match = Regex.Match(text, "^\\$\\{([A-Za-z_][A-Za-z0-9_-]*)\\}$", RegexOptions.CultureInvariant);
        return match.Success && parameters.Contains(match.Groups[1].Value);
    }
    private static bool NonNegativeOrParameter(JsonNode? value, HashSet<string> parameters) => Number(value, out var number) && number >= 0 || ParameterReference(value, parameters);
    private static bool IntegerOrParameter(JsonNode? value, HashSet<string> parameters, double minimum, double maximum = double.MaxValue) =>
        Number(value, out var number) && Math.Truncate(number) == number && number >= minimum && number <= maximum || ParameterReference(value, parameters);
    private static void OptionalString(JsonObject parent, string key, string label)
    {
        if (parent.ContainsKey(key) && !IsString(parent[key])) throw Invalid($"{label}.{key} must be a string");
    }
    private static void OptionalBoolean(JsonObject parent, string key, string label)
    {
        if (parent.ContainsKey(key) && Boolean(parent[key]) is null) throw Invalid($"{label}.{key} must be a boolean");
    }
    private static void EnumValue(JsonObject parent, string key, string label, params string[] allowed)
    {
        if (parent.ContainsKey(key) && !allowed.Contains(String(parent[key]), StringComparer.Ordinal))
            throw Invalid($"{label}.{key} must be {string.Join(" or ", allowed)}");
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
        if (string.IsNullOrEmpty(text)) throw Invalid($"{label} must be a URL string");
        var candidate = wildcard ? text.Replace("*", "x", StringComparison.Ordinal) : text;
        // WHATWG special URLs trim C0 whitespace, strip embedded tab/newline, and
        // accept missing/repeated authority slashes and backslashes. Keep the user's
        // original URL untouched; this normalization is only for validation.
        candidate = candidate.Trim(Enumerable.Range(0, 33).Select(code => (char)code).ToArray())
            .Replace("\t", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        var scheme = Regex.Match(candidate, "^([A-Za-z][A-Za-z0-9+.-]*):", RegexOptions.CultureInvariant);
        if (!scheme.Success) throw Invalid($"{label} must be a valid http or https URL");
        if (scheme.Groups[1].Value.ToLowerInvariant() is not ("http" or "https")) throw Invalid($"{label} must use http or https");
        var authority = candidate[scheme.Length..].Replace('\\', '/').TrimStart('/').Split('/', '?', '#')[0];
        authority = authority[(authority.LastIndexOf('@') + 1)..];
        if (!ValidHttpAuthority(authority)) throw Invalid($"{label} must be a valid http or https URL");
    }

    private static bool ValidHttpAuthority(string authority)
    {
        string host, port;
        if (authority.StartsWith('['))
        {
            var end = authority.IndexOf(']');
            if (end < 0) return false;
            host = authority[1..end];
            var suffix = authority[(end + 1)..];
            if (suffix.Length != 0 && !suffix.StartsWith(':')) return false;
            port = suffix.Length == 0 ? "" : suffix[1..];
            if (host.Contains('%') || !System.Net.IPAddress.TryParse(host, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            if (host.Contains('.'))
            {
                var ipv4 = host[(host.LastIndexOf(':') + 1)..].Split('.');
                if (ipv4.Length != 4 || ipv4.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)
                    || part.Length > 1 && part[0] == '0' || !int.TryParse(part, out var octet) || octet > 255)) return false;
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? "" : authority[(colon + 1)..];
            host = Uri.UnescapeDataString(host).Replace('\u3002', '.').Replace('\uFF0E', '.').Replace('\uFF61', '.');
            if (host.Length == 0 || host.Any(character => character <= 0x20 || character == 0x7f || "#%/:<>?@[\\]^|".Contains(character))) return false;
            var idn = new IdnMapping { UseStd3AsciiRules = false };
            try
            {
                // WHATWG's non-strict domain conversion permits empty labels, ASCII
                // punctuation and long ASCII labels which System.Uri rejects.
                var labels = host.Split('.');
                for (var index = 0; index < labels.Length; index++)
                {
                    var label = labels[index];
                    if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) _ = idn.GetUnicode(label);
                    if (label.Any(character => character > 0x7f)) labels[index] = idn.GetAscii(label);
                }
                host = string.Join('.', labels);
            }
            catch (ArgumentException) { return false; }
            if (!ValidSpecialHost(host)) return false;
        }
        double portNumber = 0;
        foreach (var character in port)
        {
            if (!char.IsAsciiDigit(character)) return false;
            portNumber = portNumber * 10 + character - '0';
            if (portNumber > 65535) return false;
        }
        return true;
    }

    private static bool ValidSpecialHost(string host)
    {
        // WHATWG rejects scoped IPv6 literals; System.Uri accepts them.
        if (host.StartsWith('[')) return !host.Contains('%');
        var parts = (host.EndsWith('.') ? host[..^1] : host).Split('.');
        var last = parts[^1];
        var endsInNumber = last.Length > 0 && last.All(char.IsAsciiDigit) || Ipv4Number(last, out _);
        if (!endsInNumber) return true;
        if (parts.Length > 4) return false;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!Ipv4Number(parts[index], out var number)) return false;
            var limit = index == parts.Length - 1 ? Math.Pow(256, 5 - parts.Length) : 256;
            if (number >= limit) return false;
        }
        return true;
    }

    private static bool Ipv4Number(string text, out double number)
    {
        number = 0;
        if (text.Length == 0) return false;
        var radix = 10;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; text = text[2..]; }
        else if (text.Length >= 2 && text[0] == '0') { radix = 8; text = text[1..]; }
        foreach (var character in text)
        {
            var digit = character is >= '0' and <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10;
            if (digit < 0 || digit >= radix) return false;
            number = number * radix + digit;
        }
        return true;
    }
    // Frozen canonical IANA identifiers accepted by the fixed Deno comparison runtime; aliases and Windows names remain rejected.
    private static readonly HashSet<string> SupportedTimeZones = new("Africa/Abidjan Africa/Accra Africa/Addis_Ababa Africa/Algiers Africa/Asmera Africa/Bamako Africa/Bangui Africa/Banjul Africa/Bissau Africa/Blantyre Africa/Brazzaville Africa/Bujumbura Africa/Cairo Africa/Casablanca Africa/Ceuta Africa/Conakry Africa/Dakar Africa/Dar_es_Salaam Africa/Djibouti Africa/Douala Africa/El_Aaiun Africa/Freetown Africa/Gaborone Africa/Harare Africa/Johannesburg Africa/Juba Africa/Kampala Africa/Khartoum Africa/Kigali Africa/Kinshasa Africa/Lagos Africa/Libreville Africa/Lome Africa/Luanda Africa/Lubumbashi Africa/Lusaka Africa/Malabo Africa/Maputo Africa/Maseru Africa/Mbabane Africa/Mogadishu Africa/Monrovia Africa/Nairobi Africa/Ndjamena Africa/Niamey Africa/Nouakchott Africa/Ouagadougou Africa/Porto-Novo Africa/Sao_Tome Africa/Tripoli Africa/Tunis Africa/Windhoek America/Adak America/Anchorage America/Anguilla America/Antigua America/Araguaina America/Argentina/La_Rioja America/Argentina/Rio_Gallegos America/Argentina/Salta America/Argentina/San_Juan America/Argentina/San_Luis America/Argentina/Tucuman America/Argentina/Ushuaia America/Aruba America/Asuncion America/Bahia America/Bahia_Banderas America/Barbados America/Belem America/Belize America/Blanc-Sablon America/Boa_Vista America/Bogota America/Boise America/Buenos_Aires America/Cambridge_Bay America/Campo_Grande America/Cancun America/Caracas America/Catamarca America/Cayenne America/Cayman America/Chicago America/Chihuahua America/Ciudad_Juarez America/Coral_Harbour America/Cordoba America/Costa_Rica America/Coyhaique America/Creston America/Cuiaba America/Curacao America/Danmarkshavn America/Dawson America/Dawson_Creek America/Denver America/Detroit America/Dominica America/Edmonton America/Eirunepe America/El_Salvador America/Fort_Nelson America/Fortaleza America/Glace_Bay America/Godthab America/Goose_Bay America/Grand_Turk America/Grenada America/Guadeloupe America/Guatemala America/Guayaquil America/Guyana America/Halifax America/Havana America/Hermosillo America/Indiana/Knox America/Indiana/Marengo America/Indiana/Petersburg America/Indiana/Tell_City America/Indiana/Vevay America/Indiana/Vincennes America/Indiana/Winamac America/Indianapolis America/Inuvik America/Iqaluit America/Jamaica America/Jujuy America/Juneau America/Kentucky/Monticello America/Kralendijk America/La_Paz America/Lima America/Los_Angeles America/Louisville America/Lower_Princes America/Maceio America/Managua America/Manaus America/Marigot America/Martinique America/Matamoros America/Mazatlan America/Mendoza America/Menominee America/Merida America/Metlakatla America/Mexico_City America/Miquelon America/Moncton America/Monterrey America/Montevideo America/Montserrat America/Nassau America/New_York America/Nome America/Noronha America/North_Dakota/Beulah America/North_Dakota/Center America/North_Dakota/New_Salem America/Ojinaga America/Panama America/Paramaribo America/Phoenix America/Port-au-Prince America/Port_of_Spain America/Porto_Velho America/Puerto_Rico America/Punta_Arenas America/Rankin_Inlet America/Recife America/Regina America/Resolute America/Rio_Branco America/Santarem America/Santiago America/Santo_Domingo America/Sao_Paulo America/Scoresbysund America/Sitka America/St_Barthelemy America/St_Johns America/St_Kitts America/St_Lucia America/St_Thomas America/St_Vincent America/Swift_Current America/Tegucigalpa America/Thule America/Tijuana America/Toronto America/Tortola America/Vancouver America/Whitehorse America/Winnipeg America/Yakutat Antarctica/Casey Antarctica/Davis Antarctica/DumontDUrville Antarctica/Macquarie Antarctica/Mawson Antarctica/McMurdo Antarctica/Palmer Antarctica/Rothera Antarctica/Syowa Antarctica/Troll Antarctica/Vostok Arctic/Longyearbyen Asia/Aden Asia/Almaty Asia/Amman Asia/Anadyr Asia/Aqtau Asia/Aqtobe Asia/Ashgabat Asia/Atyrau Asia/Baghdad Asia/Bahrain Asia/Baku Asia/Bangkok Asia/Barnaul Asia/Beirut Asia/Bishkek Asia/Brunei Asia/Calcutta Asia/Chita Asia/Colombo Asia/Damascus Asia/Dhaka Asia/Dili Asia/Dubai Asia/Dushanbe Asia/Famagusta Asia/Gaza Asia/Hebron Asia/Hong_Kong Asia/Hovd Asia/Irkutsk Asia/Jakarta Asia/Jayapura Asia/Jerusalem Asia/Kabul Asia/Kamchatka Asia/Karachi Asia/Katmandu Asia/Khandyga Asia/Krasnoyarsk Asia/Kuala_Lumpur Asia/Kuching Asia/Kuwait Asia/Macau Asia/Magadan Asia/Makassar Asia/Manila Asia/Muscat Asia/Nicosia Asia/Novokuznetsk Asia/Novosibirsk Asia/Omsk Asia/Oral Asia/Phnom_Penh Asia/Pontianak Asia/Pyongyang Asia/Qatar Asia/Qostanay Asia/Qyzylorda Asia/Rangoon Asia/Riyadh Asia/Saigon Asia/Sakhalin Asia/Samarkand Asia/Seoul Asia/Shanghai Asia/Singapore Asia/Srednekolymsk Asia/Taipei Asia/Tashkent Asia/Tbilisi Asia/Tehran Asia/Thimphu Asia/Tokyo Asia/Tomsk Asia/Ulaanbaatar Asia/Urumqi Asia/Ust-Nera Asia/Vientiane Asia/Vladivostok Asia/Yakutsk Asia/Yekaterinburg Asia/Yerevan Atlantic/Azores Atlantic/Bermuda Atlantic/Canary Atlantic/Cape_Verde Atlantic/Faeroe Atlantic/Madeira Atlantic/Reykjavik Atlantic/South_Georgia Atlantic/St_Helena Atlantic/Stanley Australia/Adelaide Australia/Brisbane Australia/Broken_Hill Australia/Darwin Australia/Eucla Australia/Hobart Australia/Lindeman Australia/Lord_Howe Australia/Melbourne Australia/Perth Australia/Sydney Europe/Amsterdam Europe/Andorra Europe/Astrakhan Europe/Athens Europe/Belgrade Europe/Berlin Europe/Bratislava Europe/Brussels Europe/Bucharest Europe/Budapest Europe/Busingen Europe/Chisinau Europe/Copenhagen Europe/Dublin Europe/Gibraltar Europe/Guernsey Europe/Helsinki Europe/Isle_of_Man Europe/Istanbul Europe/Jersey Europe/Kaliningrad Europe/Kiev Europe/Kirov Europe/Lisbon Europe/Ljubljana Europe/London Europe/Luxembourg Europe/Madrid Europe/Malta Europe/Mariehamn Europe/Minsk Europe/Monaco Europe/Moscow Europe/Oslo Europe/Paris Europe/Podgorica Europe/Prague Europe/Riga Europe/Rome Europe/Samara Europe/San_Marino Europe/Sarajevo Europe/Saratov Europe/Simferopol Europe/Skopje Europe/Sofia Europe/Stockholm Europe/Tallinn Europe/Tirane Europe/Ulyanovsk Europe/Vaduz Europe/Vatican Europe/Vienna Europe/Vilnius Europe/Volgograd Europe/Warsaw Europe/Zagreb Europe/Zurich Indian/Antananarivo Indian/Chagos Indian/Christmas Indian/Cocos Indian/Comoro Indian/Kerguelen Indian/Mahe Indian/Maldives Indian/Mauritius Indian/Mayotte Indian/Reunion Pacific/Apia Pacific/Auckland Pacific/Bougainville Pacific/Chatham Pacific/Easter Pacific/Efate Pacific/Enderbury Pacific/Fakaofo Pacific/Fiji Pacific/Funafuti Pacific/Galapagos Pacific/Gambier Pacific/Guadalcanal Pacific/Guam Pacific/Honolulu Pacific/Kiritimati Pacific/Kosrae Pacific/Kwajalein Pacific/Majuro Pacific/Marquesas Pacific/Midway Pacific/Nauru Pacific/Niue Pacific/Norfolk Pacific/Noumea Pacific/Pago_Pago Pacific/Palau Pacific/Pitcairn Pacific/Ponape Pacific/Port_Moresby Pacific/Rarotonga Pacific/Saipan Pacific/Tahiti Pacific/Tarawa Pacific/Tongatapu Pacific/Truk Pacific/Wake Pacific/Wallis".Split(' '), StringComparer.Ordinal);

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
