using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Crer;

/// <summary>診断の保存待ちを CDP の受信処理から切り離す、容量制限付きのログ。</summary>
internal sealed class AsyncDiagnosticLog
{
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task writer;
    private long submitted, persisted;
    private long dropped;
    private Exception? failure;

    internal AsyncDiagnosticLog(string path, Func<Stream>? open = null)
    {
        open ??= () => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        writer = Task.Run(() => Consume(open));
    }

    public void Append(string line)
    {
        Interlocked.Increment(ref submitted);
        // 保存先が遅い場合も受信を待たせず、破棄件数を結果に残す。
        if (Volatile.Read(ref failure) != null || !queue.Writer.TryWrite(line)) Interlocked.Increment(ref dropped);
    }

    public void Complete() => queue.Writer.TryComplete();

    public async Task CompleteAsync()
    {
        Complete();
        try { await writer.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException)
        {
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public JsonObject Diagnostic()
    {
        var error = Volatile.Read(ref failure);
        var lost = Interlocked.Read(ref dropped);
        var pending = Math.Max(0, Interlocked.Read(ref submitted) - Interlocked.Read(ref persisted) - lost);
        // 例外の全文には URL や入力値が混入し得るため、型と件数だけを保存する。
        return new JsonObject { ["completed"] = writer.IsCompleted, ["dropped"] = lost, ["pending"] = pending,
            ["writeFailed"] = error != null, ["errorType"] = error?.GetType().Name };
    }

    private async Task Consume(Func<Stream> open)
    {
        var inFlight = 0;
        try
        {
            // ファイルのオープンも呼び出し元の受信スレッドでは実行しない。
            if (!await queue.Reader.WaitToReadAsync(lifetime.Token)) return;
            await using var stream = open();
            await using var output = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
            do
            {
                var count = 0;
                while (count++ < 128 && queue.Reader.TryRead(out var line))
                {
                    inFlight++;
                    await output.WriteLineAsync(line.AsMemory(), lifetime.Token);
                }
                await output.FlushAsync(lifetime.Token);
                Interlocked.Add(ref persisted, inFlight);
                inFlight = 0;
            } while (await queue.Reader.WaitToReadAsync(lifetime.Token));
        }
        catch (Exception error) { Volatile.Write(ref failure, error); }
        finally
        {
            queue.Writer.TryComplete();
            Interlocked.Add(ref dropped, inFlight);
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
            lifetime.Dispose();
        }
    }
}
