using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentBag<Task> clients = [];
    private readonly TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, bool> arrived = new();
    private readonly TaskCompletionSource occlusionBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, bool> occlusionArrived = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> readinessRedirectGates = new();
    private int startupActive;
    private Task? accept;
    public readonly ConcurrentQueue<string> Requests = [];
    public readonly ConcurrentQueue<(string Path, string Phase)> Exchanges = [];
    public readonly ConcurrentDictionary<string, string> Tokens = new();
    public readonly ConcurrentDictionary<string, string> Snapshots = new();
    public readonly ConcurrentDictionary<string, string> InitialDisplays = new();
    public readonly ConcurrentDictionary<string, string> ReadinessStates = new();
    public bool Overlapped { get; private set; }
    public bool StartupOverlapped { get; private set; }
    public bool OcclusionOverlapped { get; private set; }
    public string Base { get; private set; } = "";
    public void Start()
    {
        listener.Start(); Base = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        accept = Accept();
    }
    public void ReleaseReadinessRedirect(string name) => readinessRedirectGates.GetOrAdd(name,
        _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    private async Task Accept()
    {
        try { while (!lifetime.IsCancellationRequested) clients.Add(Handle(await listener.AcceptTcpClientAsync(lifetime.Token))); }
        catch (OperationCanceledException) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }
    private static string Query(Uri uri, string name) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2 && parts[0] == name).Select(parts => Uri.UnescapeDataString(parts[1])).FirstOrDefault() ?? "";
    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                var line = await reader.ReadLineAsync(lifetime.Token); if (line == null) return;
                var parts = line.Split(' '); if (parts.Length < 2) return;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(lifetime.Token))) { }
                var uri = new Uri(Base + parts[1]); var path = uri.AbsolutePath; Requests.Enqueue(path);
                Exchanges.Enqueue((path, "request"));
                var status = "200 OK"; var headers = ""; var type = "text/html; charset=utf-8";
                string body;
                if (path == "/seen") { Tokens[Query(uri, "name")] = Query(uri, "token"); body = "ok"; type = "text/plain; charset=utf-8"; }
                else if (path == "/submitted") { Snapshots[Query(uri, "case")] = Query(uri, "value"); body = "ok"; type = "text/plain; charset=utf-8"; }
                else if (path == "/initial-display") { InitialDisplays[Query(uri, "name")] = Query(uri, "value"); body = "ok"; type = "text/plain; charset=utf-8"; }
                else if (path == "/occlusion-barrier")
                {
                    occlusionArrived[Query(uri, "name")] = true;
                    if (occlusionArrived.Count == 3) { OcclusionOverlapped = true; occlusionBarrier.TrySetResult(); }
                    await occlusionBarrier.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
                    // 完全に覆われた二つのウィンドウを Chromium が認識してから、
                    // それらのページでアニメーションフレームの進行を待つ。
                    await Task.Delay(1500, lifetime.Token);
                    body = "ok"; type = "text/plain; charset=utf-8";
                }
                else if (path.StartsWith("/startup-hang/"))
                {
                    // 応答ヘッダーを送らず、初期遷移が起動期限まで待機する状態を再現する。
                    await Task.Delay(Timeout.Infinite, lifetime.Token);
                    return;
                }
                else if (path.StartsWith("/startup-close/")) return;
                else if (path.StartsWith("/readiness-redirect-gate/"))
                {
                    var name = path["/readiness-redirect-gate/".Length..];
                    await readinessRedirectGates.GetOrAdd(name,
                        _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(lifetime.Token);
                    body = "ok"; type = "text/plain; charset=utf-8";
                }
                else if (path == "/readiness-redirect-state")
                {
                    ReadinessStates[Query(uri, "name")] = Query(uri, "state");
                    body = "ok"; type = "text/plain; charset=utf-8";
                }
                else if (path.StartsWith("/readiness-redirect-body/"))
                {
                    var name = path["/readiness-redirect-body/".Length..];
                    // ヘッダーと先頭を即時に返し、HTML の末尾だけを保留して loading を維持する。
                    var prefix = "<!doctype html><html><head><title>crer readiness redirect</title><style>body{margin:0;overflow:hidden}</style></head>"
                        + "<body><p role='status'>loading</p><script>fetch('/readiness-redirect-state?name=" + name
                        + "&state='+document.readyState).then(response=>response.text());</script><!--" + new string(' ', 4096) + "-->";
                    var suffix = "<script>document.querySelector('[role=status]').textContent='ready';"
                        + "const token=sessionStorage.token ||= crypto.randomUUID();fetch('/seen?name=" + name
                        + "&token='+token).then(response=>response.text());</script></body></html>";
                    var first = Encoding.UTF8.GetBytes(prefix); var last = Encoding.UTF8.GetBytes(suffix);
                    var responseHead = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {first.Length + last.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(responseHead, lifetime.Token); await stream.WriteAsync(first, lifetime.Token);
                    Exchanges.Enqueue((path, "prefix"));
                    await Task.Delay(12000, lifetime.Token);
                    await stream.WriteAsync(last, lifetime.Token);
                    Exchanges.Enqueue((path, "response"));
                    return;
                }
                else if (path.StartsWith("/readiness-redirect/"))
                {
                    var name = path["/readiness-redirect/".Length..];
                    // fetch の保留は文書の load を妨げず、テストが viewport 待機へ入ってから追加遷移できる。
                    // 本文まで読み切ってから遷移し、完了前の通信をネットワーク待機に残さない。
                    body = "<!doctype html><title>crer before readiness redirect</title><style>body{margin:0;overflow:hidden}</style>"
                        + "<p role='status'>before redirect</p><script>addEventListener('load',()=>fetch('/readiness-redirect-gate/"
                        + name + "').then(response=>response.text()).then(()=>location.replace('/readiness-redirect-body/" + name + "')),{once:true});</script>";
                }
                else if (path.EndsWith(".svg")) { type = "image/svg+xml"; body = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10' fill='red'/></svg>"; }
                else if (path.StartsWith("/initial-viewport/"))
                {
                    var name = path[18..];
                    // ページのスクリプトが反応する前に viewport を取得する。
                    // 後のリサイズで、記録済みの初期サイズが補正されないようにする。
                    body = "<!doctype html><html><head><script>const initialDisplay={width:innerWidth,height:innerHeight,dpr:devicePixelRatio};"
                        + "const reported=fetch('/initial-display?name=" + name + "&value='+encodeURIComponent(JSON.stringify(initialDisplay)));"
                        + "const painted=new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));"
                        + "const loaded=new Promise(resolve=>addEventListener('DOMContentLoaded',resolve,{once:true}));"
                        + "Promise.all([reported,painted,loaded]).then(()=>document.getElementById('status').textContent='ready');</script>"
                        + "<title>crer initial viewport</title><style>body{margin:0;overflow:hidden}</style></head>"
                        + "<body><p id='status' role='status'>waiting</p><img src='/initial-viewport.svg'></body></html>";
                }
                else if (path.StartsWith("/occlusion/"))
                {
                    var name = path[11..];
                    body = "<!doctype html><title>crer occluded loading</title><style>body{margin:0;overflow:hidden}</style>"
                        + "<p id='status' role='status'>waiting</p><script>addEventListener('DOMContentLoaded',()=>{"
                        + "fetch('/occlusion-barrier?name=" + name + "').then(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))))"
                        + ".then(()=>document.getElementById('status').textContent='ready');},{once:true});</script>";
                }
                else if (path.StartsWith("/startup/"))
                {
                    var name = path[9..];
                    var initial = name.EndsWith('1');
                    if (initial && Interlocked.Increment(ref startupActive) == 3) StartupOverlapped = true;
                    // 従来の文書・CDP の固定10秒期限を超えつつ、ブラウザーの起動期限には余裕を残す。
                    try { await Task.Delay(12000, lifetime.Token); }
                    finally { if (initial) Interlocked.Decrement(ref startupActive); }
                    headers = "X-Crer-Test-Private: crer-network-header-secret\r\n";
                    body = "<!doctype html><!-- crer-network-body-secret --><title>crer delayed startup</title><p role='status'>ready</p><img src='/startup.svg'><script>const token=sessionStorage.token ||= crypto.randomUUID();fetch('/seen?name=" + name + "&token='+token);</script>";
                }
                else if (path.StartsWith("/reuse/"))
                {
                    var name = path[7..];
                    if (name is "a1" or "b1")
                    {
                        arrived[name] = true;
                        if (arrived.Count == 2) { Overlapped = true; barrier.TrySetResult(); }
                        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
                    }
                    body = "<!doctype html><title>crer integration reuse</title><p role='status'>ready</p><script>const token=sessionStorage.token ||= crypto.randomUUID();fetch('/seen?name=" + name + "&token='+token);</script>";
                }
                else if (path == "/keyboard") body = """
                    <!doctype html><title>crer keyboard regression</title><style>body{margin:0}input,button{display:block;width:240px;height:30px}</style>
                    <form><input id='email' type='email' aria-label='Email'><input id='password' type='password' aria-label='Password'>
                    <button id='toggle' type='button'>Show password</button><input id='remember' type='checkbox' aria-label='Remember me'><button id='login' type='submit'>Log in</button></form>
                    <p id='status' role='status'>waiting</p><script>
                    const events=[];for(const type of ['focusin','keydown','keyup'])document.addEventListener(type,event=>events.push({type,id:event.target.id,key:event.key,code:event.code}));
                    document.querySelector('form').addEventListener('submit',event=>{event.preventDefault();setTimeout(()=>{
                    const value={submitted:{email:email.value,password:password.value,remember:remember.checked},events};
                    fetch('/submitted?case='+new URL(location.href).searchParams.get('case')+'&value='+encodeURIComponent(JSON.stringify(value)));
                    document.getElementById('status').textContent='submitted';},50);});</script>
                    """;
                else body = "<!doctype html><title>crer integration window</title><style>body{margin:0;overflow:hidden}</style><p role='status'>ready</p>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var cache = path is "/seen" or "/initial-display" ? "" : "Cache-Control: " + (path.EndsWith(".svg") ? "max-age=3600" : "no-store") + "\r\n";
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n{cache}Connection: close\r\n{headers}\r\n");
                await stream.WriteAsync(head, lifetime.Token); await stream.WriteAsync(bytes, lifetime.Token);
                Exchanges.Enqueue((path, "response"));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        barrier.TrySetResult(); occlusionBarrier.TrySetResult(); lifetime.Cancel(); listener.Stop();
        if (accept != null) await accept;
        try { await Task.WhenAll(clients); } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
