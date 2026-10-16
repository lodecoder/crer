using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crer;

var passed = 0; var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(5)); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); failed++; }
}
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-template-vectors.json")))!;
var temporary = Path.Combine(Path.GetTempPath(), "crer-template-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var file = Path.Combine(temporary, "pixel.png"); await File.WriteAllBytesAsync(file, [0]);
try
{
    await Test("template point selection matches legacy sizes and inset boundaries", () =>
    {
        foreach (var vector in vectors["points"]!.AsArray())
        {
            var randomIndex = 0;
            var actual = Template.RandomPointInMatch(TemplateMatch.From(vector!["match"])!, vector["inset"]!.GetValue<double>(), () => new[] { .25, .75 }[randomIndex++]);
            Check(actual == ActionPoint.From(vector["point"]!), "random inset coordinate differs");
            Check(randomIndex == 2, "template point consumed different random count");
        }
        return Task.CompletedTask;
    });
    await Test("sampled RGB browser expression matches fixed baseline single and multiple search", () =>
    {
        for (var index = 0; index < 2; index++)
        {
            var actual = Template.Expression("\"data:image/png;base64,c2NyZWVu\"", "\"data:image/png;base64,AA==\"", index == 1, .9, 5);
            var expected = vectors["expressions"]![index]!.GetValue<string>();
            Check(Regex.Replace(actual, @"\s+", "") == Regex.Replace(expected, @"\s+", ""), $"browser expression differs for search mode {index}");
        }
        return Task.CompletedTask;
    });
    await Test("relative template paths resolve against scenario directory", () =>
    {
        Check(Template.PathFor("fixtures/playback", "templates/button.png") == Path.GetFullPath("fixtures/playback/templates/button.png"), "relative template path");
        Check(Template.PathFor(temporary, file) == file, "absolute template path"); return Task.CompletedTask;
    });
    await Test("single matching preserves browser match and in-memory screenshot", async () =>
    {
        var calls = new List<string>();
        var result = await Template.MatchAsync((method, _) =>
        {
            calls.Add(method);
            return Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "c2NyZWVu" }
                : new JsonObject { ["result"] = new JsonObject { ["value"] = new TemplateMatch(10, 20, 30, 40, .99).ToJson() } });
        }, new() { ["path"] = file }, null);
        Check(result.Match == new TemplateMatch(10, 20, 30, 40, .99) && result.Screenshot == "c2NyZWVu", "single template result");
        Check(calls.SequenceEqual(new[] { "Page.captureScreenshot", "Runtime.evaluate" }), "template CDP sequence");
    });
    await Test("multiple matching returns all browser rectangles in order", async () =>
    {
        var matches = new[] { new TemplateMatch(10, 20, 30, 40, .99), new TemplateMatch(50, 60, 30, 40, .98) };
        var result = await Template.MatchManyAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "c2NyZWVu" }
            : new JsonObject { ["result"] = new JsonObject { ["value"] = new JsonArray(matches.Select(value => (JsonNode)value.ToJson()).ToArray()) } }), new() { ["path"] = file }, null, .9, 5);
        Check(result.Matches.SequenceEqual(matches) && result.Screenshot == "c2NyZWVu", "multiple template result");
    });
    await Test("evaluation exception retains screenshot for failure-only policy", async () =>
    {
        try
        {
            await Template.MatchAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "ZmFpbHVyZQ==" }
                : new JsonObject { ["exceptionDetails"] = new JsonObject { ["exception"] = new JsonObject { ["description"] = "template is too large" } } }), new() { ["path"] = file }, null);
            throw new Exception("invalid template succeeded");
        }
        catch (TemplateMatchException error) { Check(error.Message.Contains("template match evaluation failed", StringComparison.Ordinal) && error.Screenshot == "ZmFpbHVyZQ==", "failure screenshot or diagnostic lost"); }
    });
    await Test("invalid matches reject dimensions missing values and nonnumeric fields", async () =>
    {
        foreach (var invalid in new JsonNode?[] { null, new JsonObject(), new TemplateMatch(1, 2, 0, 4, 1).ToJson(), new JsonObject { ["x"] = "1", ["y"] = 2, ["width"] = 3, ["height"] = 4, ["similarity"] = 1 } })
        {
            try
            {
                await Template.MatchAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "screen" }
                    : new JsonObject { ["result"] = new JsonObject { ["value"] = invalid?.DeepClone() } }), new() { ["path"] = file }, null);
                throw new Exception("invalid match succeeded");
            }
            catch (TemplateMatchException error) { Check(error.Message.Contains("invalid result", StringComparison.Ordinal) && error.Screenshot == "screen", "invalid result lost screenshot"); }
        }
    });
    await Test("multiple search rejects a nonarray and any invalid rectangle", async () =>
    {
        foreach (var invalid in new JsonNode[] { new JsonObject(), new JsonArray(new TemplateMatch(1, 2, 3, 4, 1).ToJson(), new JsonObject()) })
        {
            try
            {
                await Template.MatchManyAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "screen" }
                    : new JsonObject { ["result"] = new JsonObject { ["value"] = invalid.DeepClone() } }), new() { ["path"] = file }, null, .9, 5);
                throw new Exception("invalid matches succeeded");
            }
            catch (TemplateMatchException error) { Check(error.Message.Contains("invalid result", StringComparison.Ordinal), "invalid matches diagnostic"); }
        }
    });
    await Test("multiple search accepts zero matches", async () =>
    {
        var result = await Template.MatchManyAsync((method, _) => Task.FromResult(method == "Page.captureScreenshot" ? new JsonObject { ["data"] = "screen" }
            : new JsonObject { ["result"] = new JsonObject { ["value"] = new JsonArray() } }), new() { ["path"] = file }, null, .9, 5);
        Check(result.Matches.Count == 0, "zero matches rejected");
    });
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
