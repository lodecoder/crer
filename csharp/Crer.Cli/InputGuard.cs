using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace Crer;

public delegate Task<JsonObject> InputCall(string method, JsonObject parameters);

/// <summary>Releases input in reverse order, including down requests whose response was lost.</summary>
public sealed class InputGuard(InputCall call, InputCall? cleanupCall = null)
{
    private JsonObject? mouse;
    private readonly List<JsonObject> keys = [];

    public async Task MouseDownAsync(double x, double y, int clickCount = 1)
    {
        // Record before sending: cancellation or a lost CDP response can follow delivery.
        mouse = new() { ["type"] = "mouseReleased", ["x"] = x, ["y"] = y,
            ["button"] = "left", ["buttons"] = 0, ["clickCount"] = clickCount };
        await call("Input.dispatchMouseEvent", new() { ["type"] = "mousePressed", ["x"] = x, ["y"] = y,
            ["button"] = "left", ["buttons"] = 1, ["clickCount"] = clickCount });
    }

    public async Task MouseUpAsync(double x, double y)
    {
        if (mouse == null) return;
        var release = mouse.DeepClone().AsObject(); release["x"] = x; release["y"] = y;
        await call("Input.dispatchMouseEvent", release);
        mouse = null;
    }

    public async Task KeyDownAsync(JsonObject input)
    {
        var release = new JsonObject { ["type"] = "keyUp", ["key"] = input["key"]?.DeepClone(),
            ["code"] = input["code"]?.DeepClone(), ["windowsVirtualKeyCode"] = input["windowsVirtualKeyCode"]?.DeepClone(),
            ["modifiers"] = input["modifiers"]?.DeepClone() };
        keys.Add(release);
        await call("Input.dispatchKeyEvent", input);
    }

    public async Task KeyUpAsync(JsonObject input)
    {
        await call("Input.dispatchKeyEvent", input);
        var code = input["code"]?.GetValue<string>();
        var index = keys.FindLastIndex(value => value["code"]?.GetValue<string>() == code);
        if (index >= 0) keys.RemoveAt(index);
    }

    public async Task RunAsync(Func<InputGuard, Task> action)
    {
        Exception? primary = null;
        try { await action(this); }
        catch (Exception error) { primary = error; }
        var errors = await ReleaseAllAsync();
        if (primary != null)
        {
            if (errors.Count > 0) primary.Data["cleanupErrors"] = errors.ToArray();
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (errors.Count > 0)
        {
            var cleanupFailure = new InvalidOperationException($"input cleanup failed: {string.Join("; ", errors)}");
            cleanupFailure.Data["cleanupErrors"] = errors.ToArray();
            throw cleanupFailure;
        }
    }

    public async Task<IReadOnlyList<string>> ReleaseAllAsync()
    {
        var errors = new List<string>();
        var release = cleanupCall ?? call;
        for (var index = keys.Count - 1; index >= 0; index--)
        {
            try { await release("Input.dispatchKeyEvent", keys[index]); }
            catch (Exception error) { errors.Add(error.Message); }
        }
        keys.Clear();
        if (mouse != null)
        {
            try { await release("Input.dispatchMouseEvent", mouse); }
            catch (Exception error) { errors.Add(error.Message); }
            mouse = null;
        }
        return errors;
    }

    public static IReadOnlyList<string>? CleanupErrors(Exception error) => error.Data["cleanupErrors"] as string[];
}
