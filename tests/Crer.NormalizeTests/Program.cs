using System.Numerics;
using System.Text.Json.Nodes;
using Crer;

var passed = 0;
var failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}");
}
void JsonEqual(JsonNode? actual, string expected)
{
    if (!JsonNode.DeepEquals(actual, JsonNode.Parse(expected))) throw new Exception($"Expected {expected}, got {actual}");
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (CrerException error) when (error.Code == 2 && error.Message.Contains(message, StringComparison.Ordinal)) { return; }
    throw new Exception($"Expected validation rejection containing '{message}'");
}
JsonObject Metadata(string json) => JsonNode.Parse(json)!.AsObject();
NormalizeOptions Options(CoordinateTransform? transform = null, BigInteger? qpc = null) => new()
{
    Url = "https://example.test", Name = "sample", Transform = transform, QpcFrequencyHz = qpc,
};
string Events(params (int Kind, long Data, int X, int Y)[] events) => string.Join("\n", events.Select((e, index) =>
    new JsonObject { ["qpc"] = (index + 1).ToString(), ["kind"] = e.Kind, ["data"] = e.Data, ["x"] = e.X, ["y"] = e.Y }.ToJsonString()));
NormalizeResult Raw(string raw, CoordinateTransform? transform = null, BigInteger? qpc = null) => Normalize.Raw(raw, Options(transform, qpc));
JsonNode Steps(NormalizeResult result) => result.Scenario["steps"]!;
void WithSidecar(string? metadata, Action<string> action)
{
    var directory = Path.Combine(Path.GetTempPath(), $"crer-normalize-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var raw = Path.Combine(directory, "raw.ndjson");
    try
    {
        File.WriteAllText(raw, Events((2, 0, 1100, 700), (3, 0, 1100, 700)));
        if (metadata is not null) File.WriteAllText(raw + ".meta.json", metadata);
        action(raw);
    }
    finally { Directory.Delete(directory, true); }
}

// Every case in tests/normalize_test.ts is represented below in the same order.
Test("converts physical screen coordinates into CSS viewport coordinates", () =>
    Equal(Normalize.ScreenToCss(new(1100, 700), new(new(100, 100), new(2000, 1200), new(1000, 600))), new(500, 300)));
Test("uses complete recording metadata as a coordinate transform", () =>
{
    Equal(Normalize.TransformFromRecordingMetadata(Metadata("""
        {"content_rect_screen_px":{"x":100,"y":200,"width":2000,"height":1200},"css_viewport":{"x":1000,"y":600}}
        """)), new(new(100, 200), new(2000, 1200), new(1000, 600)));
    Equal(Normalize.TransformFromRecordingMetadata(Metadata("""{"css_viewport":{"x":1000,"y":600}}""")), null);
    Equal(Normalize.TransformFromRecordingMetadata(Metadata("""
        {"content_rect_screen_px":{"x":100,"y":200,"width":1,"height":1},"css_viewport":{"x":1000,"y":600}}
        """)), null);
});
Test("calibrates the page origin from the recording marker", () =>
    Equal(Normalize.TransformFromRecordingMetadata(Metadata("""
        {"content_rect_screen_px":{"x":0,"y":16,"width":442,"height":331},"css_viewport":{"x":884,"y":661},
         "marker_calibration":{"screenClick":{"x":86,"y":35},"cssPoint":{"x":3,"y":2}}}
        """)), new(new(84.5, 33.998487140695915), new(442, 331), new(884, 661))));
Test("uses physical screen pixels without scaling across scrollbar dimensions", () =>
{
    var transform = Normalize.TransformFromRecordingMetadata(Metadata("""
        {"content_rect_screen_px":{"x":18,"y":42,"width":1330,"height":1200},"css_viewport":{"x":1315,"y":1200},
         "screen_pixels_per_css_pixel":{"x":1,"y":1},
         "marker_calibration":{"screenClick":{"x":20,"y":47},"cssPoint":{"x":2,"y":5}}}
        """));
    Equal(transform, new(new(18, 42), new(1330, 1200), new(1315, 1200), new(1, 1)));
    Equal(Normalize.ScreenToCss(new(1018, 642), transform!), new(1000, 600));
});
Test("infers the correct DPI scale for older metadata with a scrollbar", () =>
{
    var transform = Normalize.TransformFromRecordingMetadata(Metadata("""
        {"content_rect_screen_px":{"x":9,"y":21,"width":665,"height":600},"requested_content":{"width":1330,"height":1200},
         "css_viewport":{"x":1315,"y":1200},"marker_calibration":{"screenClick":{"x":10,"y":23},"cssPoint":{"x":2,"y":4}}}
        """));
    Equal(transform!.ScreenPixelsPerCssPixel, new(0.5, 0.5));
    Equal(Normalize.ScreenToCss(new(509, 321), transform), new(1000, 600));
});
Test("reads the recorded window position from a sidecar", () =>
    WithSidecar("""{"window_bounds":{"left":-800,"top":40}}""", path => Equal(Normalize.WindowBoundsFromSidecar(path), new(-800, 40))));
Test("reads the requested content size from a sidecar", () =>
    WithSidecar("""{"requested_content":{"width":780,"height":1250}}""", path => Equal(Normalize.RequestedContentFromSidecar(path), new(780, 1250))));
Test("reads the persistent profile directory from a sidecar", () =>
    WithSidecar("""{"profile_dir":".crer/profiles/demo"}""", path => Equal(Normalize.ProfileDirFromSidecar(path), ".crer/profiles/demo")));
Test("warns when recorder metadata marks raw input incomplete", () =>
    WithSidecar("""{"incomplete":true,"recording_error":"Raw Input recorder failed: 111"}""", path =>
        Equal(Normalize.RecordingWarningFromSidecar(path), "recording is incomplete: Raw Input recorder failed: 111")));
Test("normalizes mouse, wheel, and key events into steps", () =>
    JsonEqual(Steps(Raw(Events((2, 0, 100, 200), (3, 0, 100, 200), (6, 120, 120, 220), (7, 65 << 16, 120, 220),
        (8, 65 << 16, 120, 220), (7, 66 << 16, 120, 220), (8, 66 << 16, 120, 220), (7, 13 << 16, 120, 220)))), """
        [{"do":"click","at":{"x":100,"y":200}}, {"do":"scroll","at":{"x":120,"y":220},"delta":{"x":0,"y":-120}},
         {"do":"text","value":"ab"}, {"do":"key","key":"Enter"}]
        """));
Test("preserves recorded pauses between logical actions", () =>
    JsonEqual(Steps(Raw("""
        {"qpc":"1000000","x":10,"y":20,"kind":2,"data":0}
        {"qpc":"1000100","x":10,"y":20,"kind":3,"data":0}
        {"qpc":"6000100","x":30,"y":40,"kind":2,"data":0}
        {"qpc":"6000200","x":30,"y":40,"kind":3,"data":0}
        """, qpc: 1_000_000)), """
        [{"do":"click","at":{"x":10,"y":20},"delay_ms":5000}, {"do":"click","at":{"x":30,"y":40}}]
        """));
Test("applies a coordinate transform while normalizing", () =>
{
    var result = Raw(Events((2, 0, 1100, 700), (3, 0, 1100, 700)), new(new(100, 100), new(2000, 1200), new(1000, 600)));
    JsonEqual(Steps(result), """[{"do":"click","at":{"x":500,"y":300}}]""");
    JsonEqual(result.Scenario["browser"]!["window"]!["content"], """{"width":1000,"height":600}""");
});
Test("uses the event viewport after a recorded navigation", () =>
    JsonEqual(Steps(Raw("""
        {"qpc":"1","x":100,"y":100,"kind":2,"data":0,"css_viewport":{"x":800,"y":600}}
        {"qpc":"2","x":100,"y":100,"kind":3,"data":0,"css_viewport":{"x":800,"y":600}}
        """, new(new(0, 0), new(1000, 750), new(1000, 750)))), """[{"do":"click","at":{"x":80,"y":80}}]"""));
Test("warns and omits an incomplete mouse click", () =>
{
    var result = Raw(Events((2, 0, 100, 200)));
    JsonEqual(Steps(result), "[]");
    Equal(string.Join("\n", result.Warnings), "ignored incomplete left mouse down at end of recording");
});
Test("warns when a mouse down overlaps an unfinished click", () =>
{
    var result = Raw(Events((2, 0, 10, 20), (2, 0, 30, 40), (3, 0, 30, 40)));
    JsonEqual(Steps(result), """[{"do":"click","at":{"x":30,"y":40}}]""");
    Equal(string.Join("\n", result.Warnings), "ignored incomplete left mouse down before next mouse down");
});
Test("normalizes movement while pressed as a drag", () =>
    JsonEqual(Steps(Raw(Events((2, 0, 100, 200), (1, 0, 150, 230), (3, 0, 150, 230)))), """
        [{"do":"drag","from":{"x":100,"y":200},"to":{"x":150,"y":230}}]
        """));
Test("preserves Shift for recorded text", () =>
    JsonEqual(Steps(Raw(Events((7, 160 << 16, 0, 0), (7, 65 << 16, 0, 0), (8, 160 << 16, 0, 0), (7, 66 << 16, 0, 0)))), """
        [{"do":"text","value":"Ab"}]
        """));
Test("preserves punctuation translated by the Windows keyboard layout", () =>
    JsonEqual(Steps(Raw(Events("Crer@.42".Select(c => (9, (long)c, 0, 0)).ToArray()))), """[{"do":"text","value":"Crer@.42"}]"""));
Test("normalizes Ctrl shortcuts as key chords", () =>
    JsonEqual(Steps(Raw(Events((7, 17 << 16, 0, 0), (7, 65 << 16, 0, 0), (8, 17 << 16, 0, 0)))), """
        [{"do":"key_chord","keys":["Control","a"]}]
        """));

// tests/keyboard_normalize_test.ts verifies both event encodings of Space.
foreach (var kind in new[] { 9, 7 })
    Test($"recorded Unicode and virtual-key spaces preserve key activation and delays (kind {kind})", () =>
        JsonEqual(Steps(Raw(Events((9, 97, 0, 0), (kind, kind == 9 ? 32 : 32 << 16, 0, 0), (8, 32 << 16, 0, 0),
            (9, 98, 0, 0), (7, 9 << 16, 0, 0), (8, 9 << 16, 0, 0),
            (kind, kind == 9 ? 32 : 32 << 16, 0, 0), (8, 32 << 16, 0, 0)), qpc: 1000)), """
            [{"do":"text","value":"a","delay_ms":1}, {"do":"key","key":"Space","delay_ms":2},
             {"do":"text","value":"b","delay_ms":1}, {"do":"key","key":"Tab","delay_ms":2}, {"do":"key","key":"Space"}]
            """));

Test("sidecar flow retains profile, content, viewport, position, scale and timing", () =>
    WithSidecar("""
        {"content_rect_screen_px":{"x":100,"y":100,"width":2000,"height":1200},"css_viewport":{"x":1000,"y":600},
         "requested_content":{"width":1000,"height":600},"qpc_frequency_hz":"1000","profile_dir":".crer/profiles/demo",
         "window_bounds":{"left":-800,"top":40}}
        """, path =>
    {
        var result = Normalize.FromFile(path, Options());
        JsonEqual(Steps(result), """[{"do":"click","at":{"x":500,"y":300}}]""");
        JsonEqual(result.Scenario["browser"]!["window"], """
            {"content":{"width":1000,"height":600},"viewport":{"width":1000,"height":600},"bounds":{"left":-800,"top":40}}
            """);
        Equal(result.Scenario["browser"]!["profile"]!.GetValue<string>(), "persistent:.crer/profiles/demo");
        Equal(result.Warnings.Count, 0);
    }));
Test("missing sidecar warns once and leaves physical coordinates", () => WithSidecar(null, path =>
{
    var result = Normalize.FromFile(path, Options());
    JsonEqual(Steps(result), """[{"do":"click","at":{"x":1100,"y":700}}]""");
    Equal(result.Warnings.Single(), Normalize.MissingMetadataWarning);
}));
Test("explicit transform ignores even invalid sidecar and all sidecar-only fields", () => WithSidecar("not json", path =>
{
    var result = Normalize.FromFile(path, Options(new(new(100, 100), new(2000, 1200), new(1000, 600))));
    JsonEqual(Steps(result), """[{"do":"click","at":{"x":500,"y":300}}]""");
    Equal(result.Warnings.Count, 0);
}));
Test("malformed sidecar is diagnosed", () => WithSidecar("{", path =>
    Reject(() => Normalize.FromFile(path, Options()), "could not read recording metadata")));
Test("incomplete warning precedes unfinished mouse warnings", () => WithSidecar("""{"incomplete":true}""", path =>
{
    File.WriteAllText(path, Events((2, 0, 10, 20)));
    var result = Normalize.FromFile(path, Options());
    Equal(string.Join("|", result.Warnings), Normalize.MissingMetadataWarning + "|recording is marked incomplete|ignored incomplete left mouse down at end of recording");
    using var output = new StringWriter();
    result.WriteWarnings(output);
    Equal(output.ToString().StartsWith("Warning: "), true);
}));
Test("metadata helpers reject zero, negative and nonnumeric fallback fields", () =>
{
    foreach (var frequency in new[] { "0", "-1", "1.5", " 1000", "1e3", "" })
        Equal(Normalize.QpcFrequencyFromRecordingMetadata(new JsonObject { ["qpc_frequency_hz"] = frequency }), null);
    Equal(Normalize.QpcFrequencyFromRecordingMetadata(Metadata("""{"qpc_frequency_hz":"18446744073709551616"}""")), BigInteger.One << 64);
    Equal(Normalize.RequestedContentFromRecordingMetadata(Metadata("""{"requested_content":{"width":0,"height":1}}""")), null);
    Equal(Normalize.ProfileDirFromRecordingMetadata(Metadata("""{"profile_dir":"  "}""")), null);
    Equal(Normalize.RecordingWarningFromRecordingMetadata(Metadata("""{"incomplete":false,"recording_error":"ignored"}""")), null);
});
Test("invalid explicit scale falls back to requested content", () =>
    Equal(Normalize.TransformFromRecordingMetadata(Metadata("""
        {"content_rect_screen_px":{"x":0,"y":0,"width":100,"height":200},"css_viewport":{"x":90,"y":190},
         "screen_pixels_per_css_pixel":{"x":0,"y":-1},"requested_content":{"width":200,"height":400}}
        """))!.ScreenPixelsPerCssPixel, new(0.5, 0.5)));
Test("coordinate rounding matches JavaScript at negative and positive half points", () =>
{
    var transform = new CoordinateTransform(new(0, 0), new(100, 100), new(100, 100));
    Equal(Normalize.ScreenToCss(new(-1.5, 2.5), transform), new(-1, 3));
    Reject(() => Normalize.ScreenToCss(new(1, 1), transform with { ClientSize = new(0, 100) }), "client dimensions must be positive");
});
Test("wheel signed payload and release coordinates preserve baseline semantics", () =>
    JsonEqual(Steps(Raw(Events((6, 65416, -2, -4), (2, 0, 10, 20), (3, 0, 80, 90)))), """
        [{"do":"scroll","at":{"x":-2,"y":-4},"delta":{"x":0,"y":120}}, {"do":"click","at":{"x":10,"y":20}}]
        """));
Test("layout Unicode supplements, control characters and old keyup flushing", () =>
    JsonEqual(Steps(Raw(Events((9, 0x1f600, 0, 0), (9, 0x3042, 0, 0), (9, 0x0a, 0, 0), (9, 0x7f, 0, 0), (8, 65 << 16, 0, 0)))), """
        [{"do":"text","value":"😀あ"}]
        """));
Test("modifiers retain insertion order, Shift casing and release order", () =>
    JsonEqual(Steps(Raw(Events((7, 18 << 16, 0, 0), (7, 17 << 16, 0, 0), (7, 18 << 16, 0, 0), (7, 161 << 16, 0, 0),
        (7, 65 << 16, 0, 0), (8, 18 << 16, 0, 0), (7, 13 << 16, 0, 0)))), """
        [{"do":"key_chord","keys":["Alt","Control","A"]}, {"do":"key_chord","keys":["Control","Enter"]}]
        """));
Test("QPC uses big integers and floors submillisecond intervals", () =>
    JsonEqual(Steps(Raw("""
        {"qpc":"18446744073709551616","x":0,"y":0,"kind":7,"data":851968}
        {"qpc":"18446744073709552115","x":0,"y":0,"kind":7,"data":851968}
        {"qpc":"18446744073709553614","x":0,"y":0,"kind":7,"data":851968}
        {"qpc":"1","x":0,"y":0,"kind":7,"data":851968}
        """, qpc: 1_000_000)), """
        [{"do":"key","key":"Enter"}, {"do":"key","key":"Enter","delay_ms":1},
         {"do":"key","key":"Enter"}, {"do":"key","key":"Enter"}]
        """));
Test("malformed raw JSON, QPC and Unicode are diagnosed", () =>
{
    Reject(() => Raw("{"), "line 1");
    Reject(() => Raw("""{"qpc":"oops","x":0,"y":0,"kind":1,"data":0}"""), "qpc must be an integer");
    Reject(() => Raw(Events((9, 0x110000, 0, 0))), "Unicode code point");
});
Test("CRLF and empty lines preserve logical actions", () =>
    JsonEqual(Steps(Raw("\r\n" + Events((2, 0, 10, 20), (3, 0, 10, 20)).Replace("\n", "\r\n") + "\r\n\r\n")), """
        [{"do":"click","at":{"x":10,"y":20}}]
        """));
Test("YAML roundtrip retains scalar types, Unicode, escapes and flow step lines", () =>
{
    var result = Raw(Events((9, 'a', 0, 0), (9, ':', 0, 0), (9, '"', 0, 0), (9, '\\', 0, 0)));
    result.Scenario["name"] = "true\n日本語";
    var yaml = Normalize.ToYaml(result.Scenario);
    JsonEqual(Scenario.Parse(yaml).Root, result.Scenario.ToJsonString());
    Equal(yaml.Split('\n').Count(line => line.TrimStart().StartsWith("- { ", StringComparison.Ordinal)), 1);
});
Test("empty recording YAML retains an empty array instead of a null scalar", () =>
{
    var result = Raw("");
    var yaml = Normalize.ToYaml(result.Scenario);
    Equal(Scenario.Parse(yaml).Steps.Count, 0);
    Equal(yaml.Contains("steps: []", StringComparison.Ordinal), true);
});
Test("saved UTF-8 YAML has no BOM and is loadable", () => WithSidecar(null, path =>
{
    var scenario = Raw(Events((2, 0, 1, 2), (3, 0, 1, 2))).Scenario;
    var yamlPath = path + ".yaml";
    Normalize.SaveYaml(yamlPath, scenario);
    Equal(File.ReadAllBytes(yamlPath)[0] == 0xef, false);
    JsonEqual(Scenario.Load(yamlPath).Root, scenario.ToJsonString());
}));

Console.WriteLine($"{passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;
