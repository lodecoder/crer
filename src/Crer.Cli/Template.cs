using System.Globalization;
using System.Text.Json.Nodes;

namespace Crer;

public sealed record TemplateMatch(double X, double Y, double Width, double Height, double Similarity)
{
    public JsonObject ToJson() => new() { ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height, ["similarity"] = Similarity };
    public static TemplateMatch? From(JsonNode? node)
    {
        if (node is not JsonObject obj || new[] { "x", "y", "width", "height", "similarity" }.Any(key => obj[key]?.GetValueKind() != System.Text.Json.JsonValueKind.Number)) return null;
        var match = new TemplateMatch(ActionNumber.Read(obj["x"]), ActionNumber.Read(obj["y"]), ActionNumber.Read(obj["width"]), ActionNumber.Read(obj["height"]), ActionNumber.Read(obj["similarity"]));
        return double.IsFinite(match.X) && double.IsFinite(match.Y) && double.IsFinite(match.Width) && double.IsFinite(match.Height)
            && double.IsFinite(match.Similarity) && match.Width > 0 && match.Height > 0 ? match : null;
    }
}
public sealed record TemplateResult(TemplateMatch Match, string Screenshot);
public sealed record TemplateMatchesResult(IReadOnlyList<TemplateMatch> Matches, string Screenshot);
public sealed class TemplateMatchException(string message, string screenshot) : Exception(message)
{
    public string Screenshot { get; } = screenshot;
}

public static class Template
{
    public static string PathFor(string? directory, string path) => Path.GetFullPath(Path.Combine(directory ?? Environment.CurrentDirectory, path));

    public static ActionPoint RandomPointInMatch(TemplateMatch match, double inset, Func<double> random)
    {
        var safeInset = Math.Max(0, Math.Min(Math.Floor(inset), Math.Min(Math.Floor((match.Width - 1) / 2), Math.Floor((match.Height - 1) / 2))));
        return new(match.X + safeInset + Math.Floor(random() * Math.Max(1, match.Width - safeInset * 2)),
            match.Y + safeInset + Math.Floor(random() * Math.Max(1, match.Height - safeInset * 2)));
    }

    public static async Task<TemplateResult> MatchAsync(InputCall call, JsonObject options, string? directory, CancellationToken token = default)
    {
        var (value, screenshot) = await EvaluateAsync(call, options, directory, false, 0, 1, token);
        var match = TemplateMatch.From(value) ?? throw new TemplateMatchException("template match returned an invalid result", screenshot);
        return new(match, screenshot);
    }

    public static async Task<TemplateMatchesResult> MatchManyAsync(InputCall call, JsonObject options, string? directory, double threshold, double maxMatches, CancellationToken token = default)
    {
        var (value, screenshot) = await EvaluateAsync(call, options, directory, true, threshold, maxMatches, token);
        if (value is not JsonArray array) throw new TemplateMatchException("template matching returned an invalid result", screenshot);
        var matches = array.Select(TemplateMatch.From).ToArray();
        if (matches.Any(match => match == null)) throw new TemplateMatchException("template matching returned an invalid result", screenshot);
        return new(matches.Select(match => match!).ToArray(), screenshot);
    }

    private static async Task<(JsonNode? Value, string Screenshot)> EvaluateAsync(InputCall call, JsonObject options, string? directory, bool many, double threshold, double maxMatches, CancellationToken token)
    {
        var capture = call("Page.captureScreenshot", new() { ["format"] = "png" });
        var image = File.ReadAllBytesAsync(PathFor(directory, options["path"]!.GetValue<string>()), token);
        await Task.WhenAll(capture, image);
        var screenshot = (await capture)["data"]!.GetValue<string>();
        var sourceUrl = JsonValue.Create("data:image/png;base64," + Convert.ToBase64String(await image))!.ToJsonString();
        var screenshotUrl = JsonValue.Create("data:image/png;base64," + screenshot)!.ToJsonString();
        var result = await call("Runtime.evaluate", new() { ["expression"] = Expression(screenshotUrl, sourceUrl, many, threshold, maxMatches), ["awaitPromise"] = true, ["returnByValue"] = true });
        var failure = result["exceptionDetails"]?["exception"]?["description"]?.GetValue<string>() ?? result["exceptionDetails"]?["text"]?.GetValue<string>();
        if (failure != null) throw new TemplateMatchException($"template match evaluation failed: {failure}", screenshot);
        return (result["result"]?["value"]?.DeepClone(), screenshot);
    }

    // 旧方式のサンプル位置、同点時の走査順、重なり除外、候補置換順を維持する。
    // 候補ごとに変わらない座標と RGB は先に求め、画面全体の照合での再計算を避ける。
    public static string Expression(string screenshotUrl, string sourceUrl, bool many, double threshold, double maxMatches)
    {
        var start = $$"""
        (async () => {
          const load = (url) => new Promise((resolve, reject) => {
            const image = new Image(); image.onload = () => resolve(image); image.onerror = () => reject(new Error("image decode failed")); image.src = url;
          });
          const [screen, source] = await Promise.all([load({{screenshotUrl}}), load({{sourceUrl}})]);
          if (source.width > screen.width || source.height > screen.height) throw new Error("template is larger than screenshot");
          const canvas = document.createElement("canvas"); canvas.width = screen.width; canvas.height = screen.height;
          const context = canvas.getContext("2d", { willReadFrequently: true });
          context.drawImage(screen, 0, 0);
          const pixels = context.getImageData(0, 0, screen.width, screen.height).data;
          canvas.width = source.width; canvas.height = source.height;
          context.drawImage(source, 0, 0);
          const pattern = context.getImageData(0, 0, source.width, source.height).data;
          const columns = Math.min(8, source.width), rows = Math.min(8, source.height), sampleCount = columns * rows;
          const sampleOffsets = [], sampleRed = new Uint8Array(sampleCount), sampleGreen = new Uint8Array(sampleCount), sampleBlue = new Uint8Array(sampleCount);
          for (let gy = 0, index = 0; gy < rows; gy++) for (let gx = 0; gx < columns; gx++, index++) {
            const sx = Math.floor((gx + 0.5) * source.width / columns), sy = Math.floor((gy + 0.5) * source.height / rows);
            const patternOffset = (sy * source.width + sx) * 4;
            sampleOffsets[index] = (sy * screen.width + sx) * 4;
            sampleRed[index] = pattern[patternOffset]; sampleGreen[index] = pattern[patternOffset + 1]; sampleBlue[index] = pattern[patternOffset + 2];
          }
          const maximumDifference = sampleCount * 255 * 3;
        """;
        if (!many) return start + """

          let best = { x: 0, y: 0, width: source.width, height: source.height, similarity: -1 };
          let bestDifference = Number.POSITIVE_INFINITY;
          for (let y = 0; y <= screen.height - source.height; y++) for (let x = 0; x <= screen.width - source.width; x++) {
            let difference = 0;
            const base = (y * screen.width + x) * 4;
            for (let index = 0; index < sampleCount; index++) {
              const screenOffset = base + sampleOffsets[index];
              difference += Math.abs(sampleRed[index] - pixels[screenOffset]) + Math.abs(sampleGreen[index] - pixels[screenOffset + 1]) + Math.abs(sampleBlue[index] - pixels[screenOffset + 2]);
              if (difference >= bestDifference) break;
            }
            if (difference < bestDifference) {
              bestDifference = difference; best = { x, y, width: source.width, height: source.height, similarity: 1 - difference / maximumDifference };
              if (difference === 0) return best;
            }
          }
          return best;
        })()
        """;
        var limit = maxMatches.ToString("R", CultureInfo.InvariantCulture);
        var similarity = threshold.ToString("R", CultureInfo.InvariantCulture);
        return start + $$"""

          const candidates = [];
          const overlap = (a, b) => {
            const left = Math.max(a.x, b.x), top = Math.max(a.y, b.y);
            const right = Math.min(a.x + a.width, b.x + b.width), bottom = Math.min(a.y + a.height, b.y + b.height);
            const intersection = Math.max(0, right - left) * Math.max(0, bottom - top);
            const union = a.width * a.height + b.width * b.height - intersection;
            return union ? intersection / union : 0;
          };
          const candidateLimit = Math.max({{limit}} * 20, {{limit}});
          const allowedDifference = (1 - {{similarity}}) * sampleCount * 255 * 3;
          for (let y = 0; y <= screen.height - source.height; y++) for (let x = 0; x <= screen.width - source.width; x++) {
            let difference = 0;
            const base = (y * screen.width + x) * 4;
            for (let index = 0; index < sampleCount; index++) {
              const screenOffset = base + sampleOffsets[index];
              difference += Math.abs(sampleRed[index] - pixels[screenOffset]) + Math.abs(sampleGreen[index] - pixels[screenOffset + 1]) + Math.abs(sampleBlue[index] - pixels[screenOffset + 2]);
              if (difference > allowedDifference) break;
            }
            const similarity = 1 - difference / maximumDifference;
            if (similarity < {{similarity}}) continue;
            const candidate = { x, y, width: source.width, height: source.height, similarity };
            const duplicate = candidates.findIndex((existing) => overlap(existing, candidate) >= 0.5);
            if (duplicate >= 0) {
              if (candidate.similarity > candidates[duplicate].similarity) candidates[duplicate] = candidate;
            } else if (candidates.length < candidateLimit) {
              candidates.push(candidate);
            } else {
              let worst = 0;
              for (let index = 1; index < candidates.length; index++) if (candidates[index].similarity < candidates[worst].similarity) worst = index;
              if (candidate.similarity > candidates[worst].similarity) candidates[worst] = candidate;
            }
          }
          return candidates.sort((a, b) => b.similarity - a.similarity).slice(0, {{limit}});
        })()
        """;
    }
}
