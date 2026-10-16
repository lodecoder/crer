using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Crer;

public sealed partial class Playback
{
    private Func<string, Task>? beforeStep;
    private sealed record CachedTemplate(string Path, double Threshold, TemplateMatch Match, string Screenshot);
    private sealed class ForEachBreak : Exception;

    private async Task<int> ExecuteSteps(Scenario scenario, CliOptions options, JsonArray failures)
    {
        var random = new DeterministicRandom(ulong.Parse(Json.Text(metadata["seed"]).TrimStart('0') is { Length: > 0 } seed ? seed : "0", CultureInfo.InvariantCulture));
        var timeout = ActionNumber.Read(scenario.Playback["timeouts"]?["action_ms"], 10000);
        var stepDelay = options.StepDelay ?? ActionNumber.Read(scenario.Playback["step_delay_ms"]);
        if (!double.IsFinite(stepDelay) || stepDelay < 0) throw new InvalidOperationException("step_delay_ms must be a non-negative number");
        var screenshots = Json.Text(metadata["templateScreenshots"], "all");
        var templateDirectory = Path.GetDirectoryName(Path.GetFullPath(options.File!));
        var defaults = scenario.Playback["template"];
        var globalJitter = JitterConfig.From(scenario.Playback["jitter"]);
        var execution = new ExecutionContext();
        InputCall call = (method, parameters) => Call(method, parameters);
        TemplateMatcher Matcher() => templates ??= new TemplateMatcher(cdp!, session!);
        var adapter = new AtomicActionAdapter
        {
            Call = call, CleanupCall = (method, parameters) => Call(method, parameters, cancellation: CancellationToken.None, timeout: 1500),
            Viewport = new(width, height), Sleep = Delay, Capture = name => Capture(name, token), Log = logger.Log,
            AssertState = AssertActionState,
            WaitFor = WaitForCondition
        };
        var executor = new StepExecutor<CachedTemplate>(execution, async (frame, children) =>
        {
            var step = frame.Step; var index = frame.Index; var action = Json.Text(step["do"]);
            var started = Timestamp();
            ActionPoint? at = null; ActionPoint? offset = null; TemplateMatch? match = null;
            string? failedScreenshot = null;
            var succeeded = false; var delayHandled = false; var stepDelayHandled = false;
            JsonObject Record(string status) => new() { ["index"] = index, ["do"] = action, ["startedAt"] = started, ["completedAt"] = Timestamp(), ["status"] = status };
            async Task Write(JsonObject record)
            {
                try { record["url"] = await Evaluate("location.href", CancellationToken.None); } catch { }
                await File.AppendAllTextAsync(Path.Combine(absoluteRunDir, "steps.ndjson"), record.ToJsonString() + "\n");
            }
            JsonObject WithCoordinates(JsonObject record)
            {
                if (at != null) record["at"] = at.Value.ToJson();
                if (offset != null) record["jitterOffset"] = offset.Value.ToJson();
                if (match != null) record["templateMatch"] = match.ToJson();
                return record;
            }
            async Task TemplateImage(string name, string screenshot)
            {
                if (screenshots == "all") await SaveTemplateImage(name, screenshot);
            }
            try
            {
                token.ThrowIfCancellationRequested();
                Scenario.ValidateExpandedStep(step);
                if (beforeStep != null) await beforeStep(index);
                switch (action)
                {
                    case "fail": throw new ExplicitFailureException(ActionNumber.String(step["message"], "undefined"));
                    case "break":
                    {
                        var record = Record("ok"); record["control"] = "break"; await Write(record); throw new ForEachBreak();
                    }
                    case "call":
                    {
                        var name = Json.Text(step["function"]);
                        if (!scenario.Functions.TryGetValue(name, out var function)) throw new InvalidOperationException($"undefined function: {name}");
                        var leave = execution.EnterFunction(name);
                        try
                        {
                            var record = Record("ok"); record["function"] = name; await Write(record);
                            var body = StepArguments.Expand(function.Steps, step["args"] as JsonObject ?? new())!.AsArray();
                            await children(body, $"{index}.{name}");
                        }
                        finally { leave(); }
                        succeeded = true; break;
                    }
                    case "repeat":
                    {
                        var count = ExpandedInteger(step["count"], 0, "repeat count must be a non-negative integer after argument expansion");
                        var record = Record("ok"); record["count"] = count; await Write(record);
                        for (double iteration = 0; iteration < count && !execution.Stopped; iteration++)
                        {
                            // 子が空、または同期的に完了する場合も worker・利用者の取消しを確認する。
                            token.ThrowIfCancellationRequested();
                            await children(Steps(step, "steps"), $"{index}.{iteration.ToString(CultureInfo.InvariantCulture)}");
                        }
                        succeeded = true; break;
                    }
                    case "repeat_until":
                    {
                        var template = step["template"]!.AsObject();
                        var path = Json.Text(template["path"]);
                        var threshold = ActionNumber.Read(template["min_similarity"], ActionNumber.Read(defaults?["min_similarity"], .8));
                        var desiredVisible = Json.Text(step["state"]) == "visible";
                        var maximum = ExpandedInteger(step["max_attempts"], 1, "repeat_until max_attempts must be a positive integer after argument expansion");
                        double attempts = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            var found = await Matcher().MatchAsync(template, templateDirectory, token); match = found.Match;
                            await TemplateImage($"template-{index}.attempt-{attempts.ToString(CultureInfo.InvariantCulture)}", found.Screenshot);
                            logger.Log($"template: {path}, similarity: {match.Similarity:F4}, threshold: {threshold.ToString("R", CultureInfo.InvariantCulture)}");
                            var visible = match.Similarity >= threshold;
                            if (visible == desiredVisible)
                            {
                                var record = WithCoordinates(Record("ok")); record["state"] = step["state"]?.DeepClone(); record["attempts"] = attempts; await Write(record);
                                succeeded = true; break;
                            }
                            if (attempts >= maximum)
                            {
                                var error = $"repeat_until limit reached: template {path} did not become {Json.Text(step["state"])} after {maximum.ToString(CultureInfo.InvariantCulture)} attempts (similarity {match.Similarity:F4}, threshold {threshold.ToString("R", CultureInfo.InvariantCulture)})";
                                if (Json.Text(step["on_limit"]) == "continue")
                                {
                                    var record = WithCoordinates(Record("skipped")); record["kind"] = "template"; record["reason"] = error; record["state"] = step["state"]?.DeepClone(); record["attempts"] = attempts; await Write(record);
                                    succeeded = true; break;
                                }
                                failedScreenshot = found.Screenshot; throw new InvalidOperationException(error);
                            }
                            attempts++;
                            await children(Steps(step, "steps"), $"{index}.{(attempts - 1).ToString(CultureInfo.InvariantCulture)}", new(path, threshold, match, found.Screenshot));
                            if (execution.Stopped) { succeeded = true; break; }
                        }
                        break;
                    }
                    case "for_each_template":
                    {
                        var template = step["template"]!.AsObject(); var path = Json.Text(template["path"]);
                        var threshold = ActionNumber.Read(template["min_similarity"], ActionNumber.Read(defaults?["min_similarity"], .8));
                        var maximum = ExpandedInteger(step["max_matches"], 1, "for_each_template max_matches must be an integer from 1 through 100 after argument expansion", 100);
                        var found = await Matcher().MatchManyAsync(template, templateDirectory, threshold, maximum, token);
                        await TemplateImage($"template-{index}", found.Screenshot);
                        logger.Log($"template: {path}, matches: {found.Matches.Count}, threshold: {threshold.ToString("R", CultureInfo.InvariantCulture)}");
                        if (found.Matches.Count == 0)
                        {
                            var error = $"template match failed: {path} has no matches at threshold {threshold.ToString("R", CultureInfo.InvariantCulture)}";
                            if (Json.Text(template["on_missing"], Json.Text(defaults?["on_missing"], "fail")) != "skip")
                            { failedScreenshot = found.Screenshot; throw new InvalidOperationException(error); }
                            var record = Record("skipped"); record["kind"] = "template"; record["reason"] = error; record["matches"] = 0; record["templateMatches"] = new JsonArray(); await Write(record);
                        }
                        else
                        {
                            var record = Record("ok"); record["matches"] = found.Matches.Count; record["templateMatches"] = new JsonArray(found.Matches.Select(value => (JsonNode)value.ToJson()).ToArray()); await Write(record);
                            for (var matchIndex = 0; matchIndex < found.Matches.Count && !execution.Stopped; matchIndex++)
                            {
                                var candidate = found.Matches[matchIndex];
                                var args = new JsonObject { ["match_left"] = candidate.X, ["match_top"] = candidate.Y, ["match_width"] = candidate.Width, ["match_height"] = candidate.Height,
                                    ["match_center_x"] = candidate.X + Math.Floor(candidate.Width / 2), ["match_center_y"] = candidate.Y + Math.Floor(candidate.Height / 2), ["match_similarity"] = candidate.Similarity };
                                try { await children(StepArguments.Expand(Steps(step, "steps"), args)!.AsArray(), $"{index}.{matchIndex}"); }
                                catch (ForEachBreak) { break; }
                            }
                        }
                        succeeded = true; break;
                    }
                    case "if" when step["equals"] is JsonObject equals:
                    {
                        var matched = JsonNode.DeepEquals(equals["left"], equals["right"]);
                        var record = Record("ok"); record["condition"] = new JsonObject { ["equals"] = equals.DeepClone() }; record["matched"] = matched; record["branch"] = matched ? "then" : "else"; await Write(record);
                        await children(Steps(step, matched ? "then" : "else"), index); succeeded = true; break;
                    }
                    case "if" when step["weekdays"] is JsonArray days:
                    {
                        var zone = step["time_zone"]?.GetValue<string>();
                        var current = StepArguments.Weekday(DateTimeOffset.UtcNow, zone);
                        var matched = days.Any(day => Json.Text(day) == current);
                        var condition = new JsonObject { ["weekdays"] = days.DeepClone(), ["current"] = current }; if (zone != null) condition["time_zone"] = zone;
                        var record = Record("ok"); record["condition"] = condition; record["matched"] = matched; record["branch"] = matched ? "then" : "else"; await Write(record);
                        await children(Steps(step, matched ? "then" : "else"), index); succeeded = true; break;
                    }
                    default:
                    {
                        if (step["template"] is JsonObject template)
                        {
                            var path = Json.Text(template["path"]);
                            var threshold = ActionNumber.Read(template["min_similarity"], ActionNumber.Read(defaults?["min_similarity"], .8));
                            var reused = frame.CachedTemplate is { } cache && cache.Path == path && cache.Threshold == threshold;
                            var found = reused ? new TemplateResult(frame.CachedTemplate!.Match, frame.CachedTemplate.Screenshot) : await Matcher().MatchAsync(template, templateDirectory, token);
                            match = found.Match;
                            await TemplateImage($"template-{index}", found.Screenshot);
                            logger.Log($"template: {path}, similarity: {match.Similarity:F4}, threshold: {threshold.ToString("R", CultureInfo.InvariantCulture)}{(reused ? " (reused)" : "")}");
                            var error = $"template match failed: {path} similarity {match.Similarity:F4} is below {threshold.ToString("R", CultureInfo.InvariantCulture)}";
                            if (action == "if")
                            {
                                var matched = match.Similarity >= threshold;
                                var record = WithCoordinates(Record("ok")); record["matched"] = matched; record["branch"] = matched ? "then" : "else"; if (!matched) record["reason"] = error; await Write(record);
                                await children(Steps(step, matched ? "then" : "else"), index, new(path, threshold, match, found.Screenshot)); succeeded = true;
                            }
                            if (match.Similarity < threshold)
                            {
                                if (action == "click" && step["then"] != null)
                                {
                                    var record = WithCoordinates(Record("skipped")); record["matched"] = false; record["reason"] = error; await Write(record); succeeded = true; delayHandled = true;
                                }
                                else if (action != "if" && Json.Text(template["on_missing"], Json.Text(defaults?["on_missing"], "fail")) == "skip")
                                {
                                    var record = WithCoordinates(Record("skipped")); record["kind"] = "template"; record["reason"] = error; await Write(record); succeeded = true;
                                }
                                else if (action != "if") { failedScreenshot = found.Screenshot; throw new InvalidOperationException(error); }
                            }
                            if (!succeeded) at = Template.RandomPointInMatch(match, ActionNumber.Read(template["random_inset_px"], ActionNumber.Read(defaults?["random_inset_px"])), random.Next);
                        }
                        else if (step["at"] is JsonObject coordinates)
                        {
                            var point = ActionPoint.From(coordinates);
                            at = Prng.Jitter(point, JitterConfig.From(step["jitter"]) ?? globalJitter, random, new(width, height)) ?? throw new JitterBoundsException();
                            offset = new(at.Value.X - point.X, at.Value.Y - point.Y);
                        }
                        break;
                    }
                }
                if (!succeeded)
                {
                    if (action == "click")
                    {
                        var count = ExpandedInteger(step["count"] ?? JsonValue.Create(1), 1, "click count must be a positive integer after argument expansion");
                        for (double iteration = 0; iteration < count; iteration++)
                        {
                            await Actions.ExecuteAtomicAsync(adapter, step, at, globalJitter, random, timeout);
                            if (iteration == count - 1) { var record = WithCoordinates(Record("ok")); if (count != 1) record["count"] = count; await Write(record); }
                            if (step["delay_ms"] != null) await Delay(Actions.NonNegative(step["delay_ms"], "delay_ms"));
                            if (stepDelay > 0) await Delay(stepDelay);
                        }
                        succeeded = true; delayHandled = true; stepDelayHandled = true;
                        if (step["template"] != null && step["then"] != null) await children(Steps(step, "then"), index);
                    }
                    else
                    {
                        await Actions.ExecuteAtomicAsync(adapter, step, at, globalJitter, random, timeout);
                        var record = WithCoordinates(Record("ok"));
                        if (action == "log") record["message"] = step["message"]?.DeepClone();
                        await Write(record); succeeded = true;
                    }
                }
            }
            catch (ForEachBreak) { throw; }
            catch (OperationCanceledException interrupted)
            {
                var record = WithCoordinates(Record("failed")); record["kind"] = "interrupted"; record["error"] = interrupted.Message;
                if (InputGuard.CleanupErrors(interrupted) is { } cleanup) record["cleanupErrors"] = new JsonArray(cleanup.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                await Write(record);
                try { await Capture($"failure-{index}", CancellationToken.None); } catch { }
                throw;
            }
            catch (CrerException environment) when (environment.Code == 3)
            {
                var record = WithCoordinates(Record("failed")); record["kind"] = "environment"; record["error"] = environment.Message;
                if (InputGuard.CleanupErrors(environment) is { } cleanup) record["cleanupErrors"] = new JsonArray(cleanup.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                await Write(record);
                try { await Capture($"failure-{index}", CancellationToken.None); } catch { }
                throw;
            }
            catch (Exception error)
            {
                if (error is ExplicitFailureException)
                {
                    var record = Record("failed"); record["message"] = error.Message; record["kind"] = "explicit"; record["error"] = ErrorText(error); await Write(record);
                    try { await Capture($"failure-{index}", CancellationToken.None); } catch { }
                    failures.Add((JsonNode?)JsonValue.Create($"{index}:explicit:{error.Message}")); execution.Stop(); return;
                }
                if (error is TemplateMatchException templateError) failedScreenshot ??= templateError.Screenshot;
                var kind = FailureKind(step, error);
                var failure = WithCoordinates(Record("failed")); failure["kind"] = kind; failure["error"] = ErrorText(error);
                if (action == "log") failure["message"] = step["message"]?.DeepClone();
                if (InputGuard.CleanupErrors(error) is { } cleanup) failure["cleanupErrors"] = new JsonArray(cleanup.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                await Write(failure);
                if (kind == "template" && failedScreenshot != null)
                {
                    try { await SaveTemplateImage($"failure-{index}", failedScreenshot); }
                    catch { try { await Capture($"failure-{index}", CancellationToken.None); } catch { } }
                }
                else try { await Capture($"failure-{index}", CancellationToken.None); } catch { }
                if (InputGuard.CleanupErrors(error) is { Count: > 0 })
                    throw new CrerException(3, $"input release failed after {error.Message}: {string.Join("; ", InputGuard.CleanupErrors(error)!)}");
                failures.Add((JsonNode?)JsonValue.Create($"{index}:{kind}:{ErrorText(error)}"));
                if (Json.Text(scenario.Playback["on_failure"]?[kind], Json.Text(scenario.Playback["on_failure"]?["default"], "abort")) == "abort") { execution.Stop(); return; }
            }
            if (execution.Stopped) return;
            if (succeeded && step["delay_ms"] != null && !delayHandled) await Delay(Actions.NonNegative(step["delay_ms"], "delay_ms"));
            if (stepDelay > 0 && !stepDelayHandled) await Delay(stepDelay);
        });
        await executor.ExecuteAsync(scenario.Steps);
        blocker?.AssertHealthy();
        return failures.Count > 0 ? 4 : 0;
    }

    private static JsonArray Steps(JsonObject step, string key) => step[key] as JsonArray ?? new();
    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private static double ExpandedInteger(JsonNode? value, double minimum, string message, double maximum = double.PositiveInfinity)
    {
        var number = ActionNumber.Read(value);
        if (!double.IsFinite(number) || number < minimum || number > maximum || Math.Truncate(number) != number) throw new InvalidOperationException(message);
        return number;
    }
    private Task SaveTemplateImage(string name, string screenshot) => File.WriteAllBytesAsync(Path.Combine(absoluteRunDir, name + ".png"), Convert.FromBase64String(screenshot));
    private static string ErrorText(Exception error) => (error is TemplateMatchException ? "TemplateMatchError: " : error is ExplicitFailureException ? "ExplicitFailureError: " : error is JavaScriptActionException javascript ? javascript.ErrorName + ": " : "Error: ") + error.Message;
    private static string FailureKind(JsonObject step, Exception error)
    {
        if (ErrorText(error).Contains("template", StringComparison.Ordinal)) return "template";
        if (ErrorText(error).Contains("jitter", StringComparison.Ordinal)) return "jitter_bounds";
        if (Json.Text(step["do"]) is "assert" or "wait_for") return error.Message.Contains("timed out", StringComparison.Ordinal) ? "timeout" : "assertion";
        return Json.Text(step["do"]) == "navigate" ? "navigation" : "action";
    }

    private sealed class JavaScriptActionException(string name, string message) : Exception(message)
    {
        public string ErrorName { get; } = name;
    }

    private async Task<JsonObject> ReadActionState(JsonObject step, CancellationToken? cancellation = null)
    {
        var hint = step["locator_hint"]?.ToJsonString() ?? "{}";
        var url = step["url"]?.ToJsonString() ?? "null";
        // Explicit roles only, exactly the legacy locator behavior. Matching URLs in
        // the target renderer also preserves ECMAScript regex syntax and first-star
        // replacement, including any remaining quantifiers in the glob string.
        var expression = $$"""
        (() => {
          const hint = {{hint}};
          const elements = Array.from(document.querySelectorAll('[role]'));
          const found = !hint.role && !hint.name && !hint.text || elements.some((element) => {
            const role = element.getAttribute('role');
            const name = element.getAttribute('aria-label') || element.textContent?.trim() || '';
            const text = element.textContent?.trim() || '';
            return (!hint.role || role === hint.role) && (!hint.name || name === hint.name) && (!hint.text || text === hint.text) &&
              !!(element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
          });
          const expected = {{url}};
          let urlMatches = true;
          if (expected) {
            try {
              urlMatches = new RegExp('^' + expected.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replace('*', '.*') + '$').test(location.href);
            } catch (error) { return { url: location.href, state: document.readyState, found, urlError: error.message, urlErrorName: error.name }; }
          }
          return { url: location.href, state: document.readyState, found, urlMatches };
        })()
        """;
        var state = (await Evaluate(expression, cancellation))?.AsObject() ?? new();
        if (state["urlError"] != null) throw new JavaScriptActionException(Json.Text(state["urlErrorName"], "SyntaxError"), Json.Text(state["urlError"]));
        return state;
    }

    private async Task AssertActionState(JsonObject step)
    {
        var state = await ReadActionState(step);
        if (step["url"] != null && state["urlMatches"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"assert URL failed: {Json.Text(state["url"], "unknown")}");
        if (step["state"] != null && Json.Text(state["state"]) != Json.Text(step["state"]))
            throw new InvalidOperationException($"assert ready state failed: {Json.Text(state["state"], "unknown")}");
        if (state["found"]?.GetValue<bool>() != true) throw new InvalidOperationException("assert locator failed");
    }

    private async Task WaitForCondition(JsonObject step, double limit)
    {
        var watch = Stopwatch.StartNew();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            while (watch.Elapsed.TotalMilliseconds < limit)
            {
                waiting.Token.ThrowIfCancellationRequested();
                var remaining = limit - watch.Elapsed.TotalMilliseconds;
                if (remaining <= 0) break;
                // 大きな有限数も受理し、残り期限がタイマー上限内に入った時点で有効にする。
                if (remaining <= uint.MaxValue - 1d) waiting.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                var matched = await WaitCondition(step, waiting.Token);
                waiting.Token.ThrowIfCancellationRequested();
                remaining = limit - watch.Elapsed.TotalMilliseconds;
                // タイマーと応答が競合しても、期限後の成立を成功として受け入れない。
                if (remaining <= 0) break;
                if (matched) return;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining)), waiting.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // linked token ではなく、元の利用者・worker の取消し理由を優先する。
            token.ThrowIfCancellationRequested();
            if (waiting.IsCancellationRequested) throw new InvalidOperationException("wait_for timed out");
            throw;
        }
        // 利用者・worker の取消しは、ステップの待機期限とは別の理由として伝播する。
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("wait_for timed out");
    }

    private async Task<bool> WaitCondition(JsonObject step, CancellationToken cancellation)
    {
        var state = await ReadActionState(step, cancellation);
        var desired = Json.Text(step["state"]);
        var stateMatches = desired is "visible" or "" || Json.Text(state["state"]) == "complete";
        if (desired == "network_idle") lock (networkLock)
            stateMatches = Json.Text(state["state"]) == "complete" && requests.Count == 0 && Environment.TickCount64 - lastNetworkActivity >= 500;
        return state["urlMatches"]?.GetValue<bool>() == true && stateMatches && state["found"]?.GetValue<bool>() == true;
    }
}
