using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Crer;

internal sealed class ForegroundObservation : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<JsonObject> samples = new();
    private readonly List<JsonObject> originalWindows = [];
    private readonly Task sampling;

    public ForegroundObservation()
    {
        Capture();
        sampling = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(20, lifetime.Token);
                    Capture();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
    }

    private void Capture()
    {
        var window = NativeWindows.GetForegroundWindow();
        samples.Enqueue(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["window"] = $"0x{window:x}", ["owner"] = NativeWindows.Owner(window),
        });
    }

    private static nint Original(JsonObject diagnostic) => (nint)Convert.ToInt64(Json.Text(diagnostic["before"])[2..], 16);

    public void ObserveOriginal(JsonObject diagnostic)
    {
        var window = Original(diagnostic);
        originalWindows.Add(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["window"] = $"0x{window:x}",
            ["exists"] = NativeWindows.IsWindow(window), ["owner"] = NativeWindows.Owner(window),
        });
    }

    public async Task CheckAndSave(string path, JsonObject diagnostic, int pid)
    {
        lifetime.Cancel();
        await sampling;
        Capture(); ObserveOriginal(diagnostic);
        var acquired = diagnostic["foregroundAcquired"]?.GetValue<bool>()
            ?? throw new Exception("前景の取得有無を診断で確認できない");
        var restored = diagnostic["restorePerformed"]?.GetValue<bool>() == true;
        var ownedSamples = samples.Count(sample => Json.Number(sample["owner"]) == pid);
        var originalExists = NativeWindows.IsWindow(Original(diagnostic));
        File.WriteAllText(path, new JsonObject
        {
            ["pid"] = pid, ["foregroundAcquired"] = acquired, ["restorePerformed"] = restored,
            ["ownedForegroundSamples"] = ownedSamples, ["diagnostic"] = diagnostic.DeepClone(),
            ["originalWindows"] = new JsonArray(originalWindows.Select(item => (JsonNode)item).ToArray()),
            ["samples"] = new JsonArray(samples.Select(item => (JsonNode)item).ToArray()),
        }.ToJsonString(Json.Indented));
        if (acquired)
        {
            if (!restored) throw new Exception("取得した前景の復元を実行しなかった: " + path);
            if (originalExists && Json.Number(diagnostic["restoreStatus"], -1) != 0)
                throw new Exception("生存する元ウィンドウへの前景復元が失敗した: " + path);
            if (!originalExists) Console.WriteLine("OBSERVATION 復元先の外部ウィンドウが終了したため、復元要求の実行を確認: " + path);
        }
        else
        {
            // Windows の前景許可拒否は仕様上の正常な分岐。未取得なら復元による他アプリへの干渉を禁止する。
            if (restored || diagnostic["restoreStatus"] != null || Json.Number(diagnostic["foregroundStatus"]) == 0
                || diagnostic["foregroundOwned"]?.GetValue<bool>() != false || ownedSamples != 0)
                throw new Exception("前景未取得の診断・実観測と、不要な復元を行わない動作が一致しない: " + path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await sampling;
        lifetime.Dispose();
    }
}
