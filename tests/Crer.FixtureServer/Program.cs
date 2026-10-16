using System.Net;
using System.Net.Sockets;
using System.Text;

return await FixtureServer.RunAsync(args);

internal static class FixtureServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var port = 8080;
            var root = Path.GetFullPath("fixtures/playback");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                var option = args[i];
                if (option is "--help" or "-h")
                {
                    Console.WriteLine("Usage: dotnet run --project tests/Crer.FixtureServer -- [--port <0..65535>] [--root <fixture-directory>]");
                    return 0;
                }
                if (option is not ("--port" or "--root"))
                    throw new ArgumentException($"Unknown option: {option}");
                if (!seen.Add(option)) throw new ArgumentException($"Duplicate option: {option}");
                if (++i >= args.Length) throw new ArgumentException($"Missing value for {option}");
                if (option == "--port")
                {
                    if (!int.TryParse(args[i], out port) || port is < 0 or > 65535)
                        throw new ArgumentException("--port must be an integer from 0 through 65535.");
                }
                else root = Path.GetFullPath(args[i]);
            }
            if (!Directory.Exists(root)) throw new ArgumentException($"Fixture directory does not exist: {root}");
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                Console.WriteLine($"Serving playback fixture at http://127.0.0.1:{actualPort}/click-only.html");
                Console.Out.Flush();
                // Each fixture response is tiny. Sequential requests keep shutdown deterministic.
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await RespondAsync(client, root, timeout.Token); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                    catch (IOException) { /* A browser may close an in-flight connection. */ }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { Console.CancelKeyPress -= onCancel; }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SocketException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Fixture server: {ex.Message}");
            return ex is ArgumentException ? 2 : 3;
        }
    }

    private static async Task RespondAsync(TcpClient client, string root, CancellationToken token)
    {
        using var stream = client.GetStream();
        // Cap request headers; this server accepts no request body and always closes connections.
        var header = new List<byte>();
        var single = new byte[1];
        while (header.Count < 16384)
        {
            if (await stream.ReadAsync(single, token) == 0) return;
            header.Add(single[0]);
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n'
                && header[^2] == '\r' && header[^1] == '\n') break;
        }
        var request = Encoding.ASCII.GetString(header.ToArray()).Split('\r', '\n')[0].Split(' ');
        if (request.Length != 3 || request[0] is not ("GET" or "HEAD"))
        {
            await WriteAsync(stream, "405 Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("Method not allowed"), false, token);
            return;
        }
        var path = request[1].Split('?')[0];
        // Deliberate allowlist: never expose the scenario YAML or files outside the fixture directory.
        var filename = path switch
        {
            "/" or "/index.html" => "index.html",
            "/click-only.html" => "click-only.html",
            _ => null,
        };
        var file = filename is null ? null : Path.Combine(root, filename);
        if (file is null || !File.Exists(file))
        {
            await WriteAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"), request[0] == "HEAD", token);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(file, token);
        await WriteAsync(stream, "200 OK", "text/html; charset=utf-8", bytes, request[0] == "HEAD", token);
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string contentType, byte[] body,
        bool head, CancellationToken token)
    {
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token);
        if (!head) await stream.WriteAsync(body, token);
    }
}
