using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Decatron.Desktop.Tests;

/// <summary>
/// Backend falso: HTTP para el canje de código y WebSocket de escritorio con el mismo
/// protocolo que el servidor real (core/hello, ping/pong, canal translation). Sin
/// depender de decatron.net en los tests.
/// </summary>
public sealed class FakeDesktopServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public int Port { get; }
    public Uri HttpBase => new($"http://127.0.0.1:{Port}/");
    public Uri WsUri => new($"ws://127.0.0.1:{Port}/api/desktop/ws");

    public string ValidToken { get; set; } = "tok-ok";
    public string ValidCode { get; set; } = "ABCD2345";
    public bool RejectNext { get; set; }
    public ConcurrentQueue<JsonNode> Received { get; } = new();
    public ConcurrentQueue<byte[]> ReceivedBinary { get; } = new();
    public int Connections;
    public TaskCompletionSource<bool> FirstConnected { get; } = new();
    private WebSocket? _current;

    public FakeDesktopServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add(HttpBase.ToString());
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public async Task KickAsync()
    {
        var ws = _current;
        if (ws?.State != WebSocketState.Open) return;
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "kick", CancellationToken.None); }
        catch (WebSocketException) { /* el cliente ya se fue */ }
    }

    public async Task SendAsync(object payload)
    {
        var ws = _current;
        if (ws?.State != WebSocketState.Open) return;
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path == "/api/desktop/devices/claim")
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            var ok = body["code"]?.GetValue<string>()?.Replace("-", "") == ValidCode; // el servidor real también ignora el guion
            ctx.Response.StatusCode = ok ? 200 : 400;
            ctx.Response.ContentType = "application/json";
            object res = ok
                ? new { success = true, token = ValidToken, deviceId = 7L, wsUrl = WsUri.ToString() }
                : new { success = false, message = "Código inválido o vencido" };
            await ctx.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(res, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            ctx.Response.Close();
            return;
        }
        if (path == "/api/desktop/ws" && ctx.Request.IsWebSocketRequest)
        {
            var token = ctx.Request.QueryString["token"];
            if (token != ValidToken || RejectNext)
            {
                RejectNext = false;
                ctx.Response.StatusCode = 401; ctx.Response.Close(); return;
            }
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws = wsCtx.WebSocket;
            _current = ws;
            Interlocked.Increment(ref Connections);
            FirstConnected.TrySetResult(true);
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
            {
                ch = "core", type = "hello", login = "tester", deviceId = 7, minAppVersion = "0.1.0",
                modules = new { translation = new { available = true, enabled = true, source = "es", languages = new[] { "en", "pt" } } }
            }), WebSocketMessageType.Text, true, CancellationToken.None);

            var buf = new byte[64 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType == WebSocketMessageType.Binary) { ReceivedBinary.Enqueue(buf[..r.Count]); continue; }
                    var j = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
                    Received.Enqueue(j);
                    var ch = j["ch"]?.GetValue<string>(); var type = j["type"]?.GetValue<string>();
                    if (ch == "core" && type == "ping")
                        await Reply(ws, new { ch = "core", type = "pong", t = j["t"]?.GetValue<long>() });
                    else if (ch == "translation" && type == "start")
                        await Reply(ws, new { ch = "translation", type = "started", session = new { active = true, languages = new[] { "en", "pt" }, listeners = new { en = 3 }, activePipelines = new[] { "en" }, speechSeconds = 0.0, segments = 0, creditsUsed = 0 } });
                    else if (ch == "translation" && type == "stop")
                        await Reply(ws, new { ch = "translation", type = "stopped", reason = "stopped_by_user", error = (string?)null });
                }
            }
            catch { }
            finally { try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { } }
            return;
        }
        ctx.Response.StatusCode = 404; ctx.Response.Close();
    }

    private static Task Reply(WebSocket ws, object o) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, true, CancellationToken.None);

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { await Task.WhenAny(_loop, Task.Delay(1000)); } catch { }
    }
}
