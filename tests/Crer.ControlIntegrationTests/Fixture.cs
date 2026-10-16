using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class Fixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task serving;
    private readonly ConcurrentDictionary<long, Task> pending = new();
    private long sequence;
    private readonly byte[] html;
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/index.html";

    public Fixture(byte[] pattern)
    {
        html = Encoding.UTF8.GetBytes("""
        <!doctype html><html><head><meta charset="utf-8"><style>
        html,body{margin:0;width:100%;height:100%;overflow:hidden;background:rgb(100,178,217);font:14px Arial}
        #counter{position:absolute;left:20px;top:20px;width:60px;height:32px}
        #count{position:absolute;left:100px;top:20px}
        #query{position:absolute;left:240px;top:20px;width:150px;height:30px}
        #echo{position:absolute;left:240px;top:60px}
        .pattern{position:absolute;top:100px;width:16px;height:16px;image-rendering:pixelated}
        #first{left:80px}#second{left:160px}
        #drag{position:absolute;left:300px;top:140px;width:160px;height:140px;background:#335566}
        #moves{position:absolute;left:470px;top:160px}#drop{position:absolute;left:470px;top:190px}
        #scroll{position:absolute;left:300px;top:320px;width:160px;height:100px;overflow:auto;background:#eeeeee}
        #scroll>div{height:1000px;background:linear-gradient(white,gray)}#scroll-state{position:absolute;left:300px;top:435px}
        </style></head><body>
        <button id="counter" role="button" aria-label="Counter">Count</button>
        <output id="count" role="status" aria-label="Count">0</output>
        <input id="query" role="textbox" aria-label="Query"><output id="echo" role="status" aria-label="Echo"></output>
        <img id="first" class="pattern" role="button" aria-label="Pattern" src="PATTERN">
        <img id="second" class="pattern" role="button" aria-label="Pattern" src="PATTERN">
        <div id="drag"></div><output id="moves" role="status" aria-label="Moves">0</output><output id="drop" role="status" aria-label="Drop">none</output>
        <div id="scroll"><div></div></div><output id="scroll-state" role="status" aria-label="Scroll">top</output>
        <script>
        let clicks=0,moves=0,dragging=false;
        const count=document.getElementById('count'),area=document.getElementById('drag');
        document.getElementById('counter').addEventListener('click',()=>count.textContent=String(++clicks));
        document.querySelectorAll('.pattern').forEach(image=>image.addEventListener('click',()=>{count.textContent=String(++clicks);if(new URL(location.href).searchParams.get('mode')==='remove')image.remove();}));
        document.getElementById('query').addEventListener('input',event=>document.getElementById('echo').textContent=event.target.value);
        document.getElementById('scroll').addEventListener('scroll',event=>document.getElementById('scroll-state').textContent=event.target.scrollTop>0?'scrolled':'top');
        area.addEventListener('mousedown',()=>dragging=true);
        document.addEventListener('mousemove',event=>{if(dragging&&event.buttons===1)document.getElementById('moves').textContent=String(++moves);});
        document.addEventListener('mouseup',event=>{if(dragging){document.getElementById('drop').textContent=event.clientX+','+event.clientY;dragging=false;}});
        </script></body></html>
        """.Replace("PATTERN", "data:image/png;base64," + Convert.ToBase64String(pattern), StringComparison.Ordinal));
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        serving = Serve();
    }

    private async Task Serve()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                var serial = Interlocked.Increment(ref sequence);
                var responding = Respond(client); pending[serial] = responding;
                _ = responding.ContinueWith(_ => pending.TryRemove(serial, out var ignored), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task Respond(TcpClient client)
    {
        using (client)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, deadline.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0];
                var ok = line.StartsWith("GET /index.html", StringComparison.Ordinal) || line.StartsWith("GET / ", StringComparison.Ordinal);
                var content = ok ? html : Encoding.UTF8.GetBytes("Not found");
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, deadline.Token); await stream.WriteAsync(content, deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); await serving;
        await Task.WhenAll(pending.Values.ToArray()); stop.Dispose();
    }

    public static byte[] Png(bool missing = false)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var size = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(size, 16); BinaryPrimitives.WriteUInt32BigEndian(size.AsSpan(4), 16); size[8] = 8; size[9] = 2;
        Chunk(output, "IHDR", size);
        using var raw = new MemoryStream();
        for (var y = 0; y < 16; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < 16; x++)
            {
                var white = ((x / 2 + y / 2) & 1) == 0;
                // A slight per-pixel gradient disambiguates the legacy matcher's
                // eight-by-eight samples while retaining the visible checker shape.
                var tone = white ? 220 : 20;
                var color = missing ? new byte[] { 9, 255, 31 }
                    : x < 2 || y < 2 ? new byte[] { (byte)(170 + x * 2), (byte)(15 + y * 3), (byte)(51 + (x + y) % 7) }
                    : new byte[] { (byte)(tone + x), (byte)(tone + y), (byte)(tone + (x * 3 + y * 5) % 30) };
                raw.Write(color);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(raw.ToArray());
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] content)
    {
        var bytes = Encoding.ASCII.GetBytes(kind); Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)content.Length); output.Write(length); output.Write(bytes); output.Write(content);
        uint crc = 0xffffffff;
        foreach (var value in bytes.Concat(content))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xffffffff); output.Write(length);
    }
}
