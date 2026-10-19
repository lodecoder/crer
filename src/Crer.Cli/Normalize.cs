using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Crer;

public sealed record NormalizePoint(double X, double Y);
public sealed record NormalizeSize(double Width, double Height);
public sealed record NormalizeWindowBounds(double Left, double Top);
public sealed record CoordinateTransform(
    NormalizePoint ClientOrigin,
    NormalizePoint ClientSize,
    NormalizePoint Viewport,
    NormalizePoint? ScreenPixelsPerCssPixel = null);

public sealed record NormalizeOptions
{
    public required string Url { get; init; }
    public string Name { get; init; } = "recorded-scenario";
    public CoordinateTransform? Transform { get; init; }
    public BigInteger? QpcFrequencyHz { get; init; }
    public NormalizeSize? RequestedContent { get; init; }
    public string? ProfileDir { get; init; }
    public NormalizeWindowBounds? WindowBounds { get; init; }
    /// <summary>較正原点との差を各イベントのページ領域へ適用するための基準位置。</summary>
    public NormalizePoint? RecordedContentOrigin { get; init; }
    // The original CLI ignores all sidecar fields when --client-origin is explicit.
    public bool UseSidecar { get; init; } = true;
}

public sealed record NormalizeResult(JsonObject Scenario, IReadOnlyList<string> Warnings)
{
    public void WriteWarnings(TextWriter writer)
    {
        foreach (var warning in Warnings) writer.WriteLine($"Warning: {warning}");
    }
}

/// <summary>Converts the recorder's physical input stream to the v1 editing format.</summary>
public static class Normalize
{
    public const string MissingMetadataWarning = "recording metadata is unavailable; output coordinates remain physical screen pixels.";
    private sealed record ContentGeometry(NormalizePoint Origin, NormalizePoint Size);

    public static CoordinateTransform? TransformFromRecordingMetadata(JsonObject metadata)
    {
        var geometry = RecordingGeometry(metadata);
        var viewport = Point(metadata["css_viewport"]);
        if (geometry is null || viewport is null || viewport.X <= 0 || viewport.Y <= 0) return null;

        var origin = geometry.Origin;
        var width = geometry.Size.X;
        var height = geometry.Size.Y;
        var scale = Point(metadata["screen_pixels_per_css_pixel"]);
        if (scale is not { X: > 0, Y: > 0 }) scale = null;
        if (scale is null && RequestedContentFromRecordingMetadata(metadata) is { } requested)
            scale = new NormalizePoint(width / requested.Width, height / requested.Height);

        if (metadata["marker_calibration"] is JsonObject calibration
            && Point(calibration["cssPoint"]) is { X: >= 0, Y: >= 0 } cssPoint
            && Point(calibration["screenClick"]) is { } screenClick)
            origin = new NormalizePoint(
                screenClick.X - cssPoint.X * (scale?.X ?? width / viewport.X),
                screenClick.Y - cssPoint.Y * (scale?.Y ?? height / viewport.Y));
        return new CoordinateTransform(origin, new NormalizePoint(width, height), viewport, scale);
    }

    public static BigInteger? QpcFrequencyFromRecordingMetadata(JsonObject metadata)
    {
        var text = Text(metadata["qpc_frequency_hz"]);
        return text is { Length: > 0 } && text.All(c => c is >= '0' and <= '9')
            && BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : null;
    }

    public static NormalizeWindowBounds? WindowBoundsFromRecordingMetadata(JsonObject metadata)
    {
        if (metadata["window_bounds"] is not JsonObject bounds
            || !Number(bounds["left"], out var left) || !Number(bounds["top"], out var top)) return null;
        return new NormalizeWindowBounds(left, top);
    }

    public static NormalizeSize? RequestedContentFromRecordingMetadata(JsonObject metadata)
    {
        if (metadata["requested_content"] is not JsonObject content
            || !Number(content["width"], out var width) || !Number(content["height"], out var height)
            || width <= 0 || height <= 0) return null;
        return new NormalizeSize(width, height);
    }

    public static string? ProfileDirFromRecordingMetadata(JsonObject metadata)
    {
        var path = Text(metadata["profile_dir"]);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public static string? RecordingWarningFromRecordingMetadata(JsonObject metadata)
    {
        if (metadata["incomplete"] is not JsonValue incomplete || !incomplete.TryGetValue<bool>(out var value) || !value) return null;
        return Text(metadata["recording_error"]) is { Length: > 0 } error
            ? $"recording is incomplete: {error}" : "recording is marked incomplete";
    }

    public static JsonObject? ReadSidecar(string rawPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(rawPath + ".meta.json")) as JsonObject
                ?? throw new JsonException("recording metadata must be an object");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new CrerException(2, $"could not read recording metadata: {error.Message}"); }
    }

    public static CoordinateTransform? TransformFromSidecar(string rawPath) => ReadSidecar(rawPath) is { } m ? TransformFromRecordingMetadata(m) : null;
    public static BigInteger? QpcFrequencyFromSidecar(string rawPath) => ReadSidecar(rawPath) is { } m ? QpcFrequencyFromRecordingMetadata(m) : null;
    public static NormalizeWindowBounds? WindowBoundsFromSidecar(string rawPath) => ReadSidecar(rawPath) is { } m ? WindowBoundsFromRecordingMetadata(m) : null;
    public static NormalizeSize? RequestedContentFromSidecar(string rawPath) => ReadSidecar(rawPath) is { } m ? RequestedContentFromRecordingMetadata(m) : null;
    public static string? ProfileDirFromSidecar(string rawPath) => ReadSidecar(rawPath) is { } m ? ProfileDirFromRecordingMetadata(m) : null;
    public static string? RecordingWarningFromSidecar(string rawPath) => ReadSidecar(rawPath) is { } m ? RecordingWarningFromRecordingMetadata(m) : null;

    public static NormalizePoint ScreenToCss(NormalizePoint point, CoordinateTransform transform)
    {
        if (transform.ClientSize.X <= 0 || transform.ClientSize.Y <= 0)
            throw new CrerException(2, "client dimensions must be positive");
        return new NormalizePoint(
            JsRound(transform.ScreenPixelsPerCssPixel is { } scale
                ? (point.X - transform.ClientOrigin.X) / scale.X
                : (point.X - transform.ClientOrigin.X) * transform.Viewport.X / transform.ClientSize.X),
            JsRound(transform.ScreenPixelsPerCssPixel is { } yScale
                ? (point.Y - transform.ClientOrigin.Y) / yScale.Y
                : (point.Y - transform.ClientOrigin.Y) * transform.Viewport.Y / transform.ClientSize.Y));
    }

    public static NormalizeResult FromFile(string rawPath, NormalizeOptions options)
    {
        var warnings = new List<string>();
        var missingTransform = false;
        if (options.UseSidecar && options.Transform is null)
        {
            var metadata = ReadSidecar(rawPath);
            var transform = metadata is null ? null : TransformFromRecordingMetadata(metadata);
            missingTransform = transform is null;
            if (metadata is not null)
            {
                if (RecordingWarningFromRecordingMetadata(metadata) is { } warning) warnings.Add(warning);
                options = options with
                {
                    Transform = transform,
                    QpcFrequencyHz = options.QpcFrequencyHz ?? QpcFrequencyFromRecordingMetadata(metadata),
                    RequestedContent = options.RequestedContent ?? RequestedContentFromRecordingMetadata(metadata),
                    ProfileDir = options.ProfileDir ?? ProfileDirFromRecordingMetadata(metadata),
                    WindowBounds = options.WindowBounds ?? WindowBoundsFromRecordingMetadata(metadata),
                    RecordedContentOrigin = options.RecordedContentOrigin ?? RecordingGeometry(metadata)?.Origin,
                };
            }
        }
        string raw;
        try { raw = File.ReadAllText(rawPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new CrerException(2, $"could not read raw recording '{rawPath}': {error.Message}"); }
        var result = Raw(raw, options);
        if (missingTransform && result.Scenario["browser"]?["window"]?["content"] is null) warnings.Insert(0, MissingMetadataWarning);
        warnings.AddRange(result.Warnings);
        return result with { Warnings = warnings };
    }

    public static NormalizeResult Raw(string ndjson, NormalizeOptions options)
    {
        var steps = new JsonArray();
        var warnings = new List<string>();
        var outputTransform = options.Transform;
        NormalizePoint? mouseDown = null, mouseLast = null;
        var text = new StringBuilder();
        var textQpc = BigInteger.Zero;
        BigInteger? lastActionQpc = null;
        // 左右の修飾キーを別々に追跡し、片方を離しても他方の押下状態を残す。
        var pressedModifiers = new HashSet<int>();
        var modifiers = new List<string>();
        static string? Modifier(int key) => key switch
        {
            16 or 160 or 161 => "Shift", 17 or 162 or 163 => "Control",
            18 or 164 or 165 => "Alt", 91 or 92 => "Meta", _ => null,
        };
        void AddStep(JsonObject step, BigInteger qpc)
        {
            if (options.QpcFrequencyHz is { } frequency && frequency != 0 && lastActionQpc is { } last && qpc >= last)
            {
                var delayMs = (double)((qpc - last) * 1000 / frequency);
                if (delayMs > 0)
                {
                    if (steps.LastOrDefault() is JsonObject previous && Text(previous["do"]) != "sleep") previous["delay_ms"] = delayMs;
                    else steps.Add((JsonNode)new JsonObject { ["do"] = "sleep", ["ms"] = delayMs });
                }
            }
            steps.Add((JsonNode)step);
            lastActionQpc = qpc;
        }
        void FlushText()
        {
            if (text.Length > 0) AddStep(new JsonObject { ["do"] = "text", ["value"] = text.ToString() }, textQpc);
            text.Clear();
        }
        void AddChord(string key, BigInteger qpc)
        {
            FlushText();
            var chord = new JsonArray();
            foreach (var item in modifiers) chord.Add((JsonNode?)JsonValue.Create(item));
            chord.Add((JsonNode?)JsonValue.Create(key));
            AddStep(new JsonObject { ["do"] = "key_chord", ["keys"] = chord }, qpc);
        }

        var lineNumber = 0;
        foreach (var line in ndjson.Split('\n'))
        {
            lineNumber++;
            var content = line.EndsWith('\r') ? line[..^1] : line;
            if (content.Length == 0) continue;
            try
            {
                var input = JsonNode.Parse(content) as JsonObject ?? throw new JsonException("event must be an object");
                var qpcText = Text(input["qpc"]) ?? input["qpc"]?.ToJsonString();
                if (!BigInteger.TryParse(qpcText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qpc))
                    throw new FormatException("qpc must be an integer");
                var kind = Value(input["kind"]);
                var data = Value(input["data"]);
                var transform = options.Transform;
                if (transform is not null && Point(input["css_viewport"]) is { } viewport) transform = transform with { Viewport = viewport };
                if (options.UseSidecar && input["content_rect_screen_px"] is { } eventRect)
                {
                    var geometry = Geometry(eventRect) ?? throw new FormatException("content_rect_screen_px must contain finite coordinates and dimensions of at least 32 pixels");
                    if (transform is not null)
                    {
                        // マーカーで補正した HWND 原点との差は残し、取得時点の移動量だけを反映する。
                        var reference = options.RecordedContentOrigin ?? transform.ClientOrigin;
                        transform = transform with
                        {
                            ClientOrigin = new(geometry.Origin.X + transform.ClientOrigin.X - reference.X,
                                geometry.Origin.Y + transform.ClientOrigin.Y - reference.Y),
                            ClientSize = geometry.Size,
                        };
                    }
                    else if (Point(input["css_viewport"]) is { X: > 0, Y: > 0 } eventViewport)
                        transform = new(geometry.Origin, geometry.Size, eventViewport, new(1, 1));
                }
                outputTransform ??= transform;
                var point = new NormalizePoint(Value(input["x"]), Value(input["y"]));
                if (transform is not null) point = ScreenToCss(point, transform);
                if (kind == 9)
                {
                    if (data != Math.Truncate(data) || data is < 0 or > 0x10ffff)
                        throw new FormatException("Unicode code point is outside the valid range");
                    var codePoint = (int)data;
                    if (codePoint >= 0x20 && codePoint != 0x7f
                        && (modifiers.Any(modifier => modifier != "Shift") || codePoint == 0x20 && modifiers.Count > 0))
                        AddChord(codePoint == 0x20 ? "Space" : char.ConvertFromUtf32(codePoint), qpc);
                    else if (codePoint == 0x20)
                    {
                        FlushText();
                        AddStep(new JsonObject { ["do"] = "key", ["key"] = "Space" }, qpc);
                    }
                    else if (codePoint > 0x20 && codePoint != 0x7f)
                    {
                        text.Append(codePoint <= char.MaxValue ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint));
                        textQpc = qpc;
                    }
                    continue;
                }
                var virtualKey = (int)(JsUint32(data) >> 16);
                var modifier = Modifier(virtualKey);
                if (kind is 7 or 8 && modifier is not null)
                {
                    if (kind == 7 && pressedModifiers.Add(virtualKey) && !modifiers.Contains(modifier)) modifiers.Add(modifier);
                    if (kind == 8 && pressedModifiers.Remove(virtualKey)
                        && !pressedModifiers.Any(key => Modifier(key) == modifier)) modifiers.Remove(modifier);
                    continue;
                }
                if (kind == 8) continue;
                var shift = modifiers.Contains("Shift");
                var printable = virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5a;
                if (kind == 7 && modifiers.Count > 0 && (!printable || modifiers.Any(item => item != "Shift")))
                {
                    var key = printable ? Printable(virtualKey, shift) : Key(virtualKey);
                    AddChord(key, qpc);
                    continue;
                }
                if (kind == 7 && printable)
                {
                    text.Append(Printable(virtualKey, shift));
                    textQpc = qpc;
                    continue;
                }
                FlushText();
                if (kind == 1 && mouseDown is not null) mouseLast = point;
                if (kind == 2)
                {
                    if (mouseDown is not null) warnings.Add("ignored incomplete left mouse down before next mouse down");
                    mouseDown = point;
                    mouseLast = point;
                }
                if (kind == 3 && mouseDown is not null)
                {
                    AddStep(mouseLast is not null && mouseLast != mouseDown
                        ? new JsonObject { ["do"] = "drag", ["from"] = JsonPoint(mouseDown), ["to"] = JsonPoint(mouseLast) }
                        : new JsonObject { ["do"] = "click", ["at"] = JsonPoint(mouseDown) }, qpc);
                    mouseDown = mouseLast = null;
                }
                if (kind == 6)
                {
                    var delta = data > 0x7fff ? data - 0x10000 : data;
                    AddStep(new JsonObject { ["do"] = "scroll", ["at"] = JsonPoint(point), ["delta"] = new JsonObject { ["x"] = 0, ["y"] = -delta } }, qpc);
                }
                if (kind == 7) AddStep(new JsonObject { ["do"] = "key", ["key"] = Key(virtualKey) }, qpc);
            }
            catch (CrerException) { throw; }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException)
            { throw new CrerException(2, $"invalid raw recording at line {lineNumber}: {error.Message}"); }
        }
        FlushText();
        if (mouseDown is not null) warnings.Add("ignored incomplete left mouse down at end of recording");

        var browser = new JsonObject
        {
            ["chrome"] = "chrome-for-testing@pinned",
            ["profile"] = string.IsNullOrEmpty(options.ProfileDir) ? "ephemeral" : $"persistent:{options.ProfileDir}",
            ["initial_url"] = options.Url,
        };
        if (outputTransform is not null)
        {
            var window = new JsonObject
            {
                ["content"] = JsonSize(options.RequestedContent ?? new NormalizeSize(outputTransform.Viewport.X, outputTransform.Viewport.Y)),
            };
            if (options.RequestedContent is not null) window["viewport"] = JsonSize(new NormalizeSize(outputTransform.Viewport.X, outputTransform.Viewport.Y));
            browser["window"] = window;
        }
        if (options.WindowBounds is { } bounds)
        {
            if (browser["window"] is not JsonObject) browser["window"] = new JsonObject();
            browser["window"]!["bounds"] = new JsonObject { ["left"] = bounds.Left, ["top"] = bounds.Top };
        }
        return new NormalizeResult(new JsonObject
        {
            ["version"] = 1,
            ["name"] = options.Name,
            ["browser"] = browser,
            ["playback"] = new JsonObject
            {
                ["jitter"] = new JsonObject
                {
                    ["enabled"] = false, ["distribution"] = "none", ["radius_px"] = 0,
                    ["min_distance_from_edge_px"] = 0, ["out_of_bounds"] = "fail",
                },
            },
            ["steps"] = steps,
        }, warnings);
    }

    /// <summary>Preserves scalar types without reflection; each step occupies one flow-mapping line.</summary>
    public static string ToYaml(JsonObject scenario)
    {
        var header = new YamlMappingNode();
        foreach (var entry in scenario)
            if (entry.Key != "steps") header.Add(new YamlScalarNode(entry.Key), YamlNode(entry.Value));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(header)).Save(writer, false);
        var output = new StringBuilder(writer.ToString());
        // YamlStream emits an optional document-end marker; steps belong to the same document.
        if (output.ToString().EndsWith("..." + writer.NewLine, StringComparison.Ordinal)) output.Length -= 3 + writer.NewLine.Length;
        if (scenario["steps"] is not JsonArray steps) throw new CrerException(2, "scenario.steps must be a step array");
        if (steps.Count == 0) output.AppendLine("steps: []");
        else
        {
            output.AppendLine("steps:");
            foreach (var step in steps) output.Append("  - ").AppendLine(FlowYaml(step));
        }
        return output.ToString();
    }

    public static void SaveYaml(string path, JsonObject scenario)
    {
        try { File.WriteAllText(path, ToYaml(scenario), new UTF8Encoding(false)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new CrerException(2, $"could not save scenario '{path}': {error.Message}"); }
    }

    private static string FlowYaml(JsonNode? value) => value switch
    {
        JsonObject mapping => "{ " + string.Join(", ", mapping.Select(entry => $"{FlowKey(entry.Key)}: {FlowYaml(entry.Value)}")) + " }",
        JsonArray array => "[ " + string.Join(", ", array.Select(FlowYaml)) + " ]",
        null => "null",
        _ => value.ToJsonString(),
    };

    private static string FlowKey(string value) => value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : JsonValue.Create(value)!.ToJsonString();

    private static YamlNode YamlNode(JsonNode? value) => value switch
    {
        JsonObject mapping => new YamlMappingNode(mapping.Select(entry => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(entry.Key), YamlNode(entry.Value)))),
        JsonArray array => new YamlSequenceNode(array.Select(YamlNode)),
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => new YamlScalarNode(scalar.GetValue<string>()) { Style = ScalarStyle.DoubleQuoted },
        null => new YamlScalarNode("null"),
        _ => new YamlScalarNode(value.ToJsonString()),
    };

    private static JsonObject JsonPoint(NormalizePoint point) => new() { ["x"] = point.X, ["y"] = point.Y };
    private static JsonObject JsonSize(NormalizeSize size) => new() { ["width"] = size.Width, ["height"] = size.Height };
    private static NormalizePoint? Point(JsonNode? node) => node is JsonObject point
        && Number(point["x"], out var x) && Number(point["y"], out var y) ? new NormalizePoint(x, y) : null;
    private static ContentGeometry? RecordingGeometry(JsonObject metadata) => Geometry((metadata["marker_calibration"] as JsonObject)?["content_rect_screen_px"])
        ?? Geometry(metadata["content_rect_screen_px"]);
    private static ContentGeometry? Geometry(JsonNode? node) => node is JsonObject rect
        && Number(rect["x"], out var x) && Number(rect["y"], out var y)
        && Number(rect["width"], out var width) && Number(rect["height"], out var height) && width >= 32 && height >= 32
        ? new(new(x, y), new(width, height)) : null;
    private static bool Number(JsonNode? node, out double number)
    {
        number = 0;
        return node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number
            && double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
    private static double Value(JsonNode? node) => Number(node, out var number) ? number : throw new FormatException("expected a finite number");
    private static string? Text(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    private static string Printable(int key, bool shift) => shift ? ((char)key).ToString().ToUpperInvariant() : ((char)key).ToString().ToLowerInvariant();
    private static string Key(int key) => key switch
    {
        8 => "Backspace", 9 => "Tab", 13 => "Enter", 27 => "Escape", 32 => "Space", 46 => "Delete",
        37 => "ArrowLeft", 38 => "ArrowUp", 39 => "ArrowRight", 40 => "ArrowDown", _ => ((char)key).ToString(),
    };
    // Math.Round uses bankers' rounding; JavaScript Math.round resolves ties toward +infinity.
    private static double JsRound(double number)
    {
        var floor = Math.Floor(number);
        return number - floor >= 0.5 ? floor + 1 : floor;
    }
    private static uint JsUint32(double number)
    {
        var modulo = Math.Truncate(number) % 4294967296d;
        return (uint)(modulo < 0 ? modulo + 4294967296d : modulo);
    }
}
