using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Core.Connection;

/// <summary>
/// Cliente del WebSocket de escritorio. Reconecta solo con backoff (1 s → 30 s), reparte
/// los mensajes por canal y mide latencia con ping cada 15 s. Un solo escritor al
/// socket (SemaphoreSlim): los módulos pueden mandar desde cualquier hilo.
/// </summary>
public sealed class DesktopConnection : IDesktopConnection, IAsyncDisposable
{
    private readonly Uri _baseUri;
    private readonly Func<string?> _tokenProvider;
    private readonly string _appVersion;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, List<Action<string, JsonNode>>> _subs = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ConnectionState _state = ConnectionState.Disconnected;
    private readonly Dictionary<string, JsonNode?> _modules = new();

    public ConnectionState State
    {
        get => _state;
        private set { if (_state == value) return; _state = value; StateChanged?.Invoke(value); }
    }
    public event Action<ConnectionState>? StateChanged;
    public string? Login { get; private set; }
    public IReadOnlyDictionary<string, JsonNode?> Modules => _modules;
    public event Action? HelloReceived;
    public TimeSpan? Latency { get; private set; }

    /// <summary>Último error de conexión legible (401 = token revocado, etc.).</summary>
    public string? LastError { get; private set; }
    public event Action<string?>? LastErrorChanged;

    /// <summary>El servidor rechazó el token: hay que volver a vincular.</summary>
    public event Action? Unauthorized;

    public DesktopConnection(Uri wsUri, Func<string?> tokenProvider, string appVersion, ILogger logger)
    {
        _baseUri = wsUri; _tokenProvider = tokenProvider; _appVersion = appVersion; _logger = logger;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_ws?.State == WebSocketState.Open) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        try { if (_loop != null) await Task.WhenAny(_loop, Task.Delay(2000)); } catch { }
        _loop = null; _cts = null;
        State = ConnectionState.Disconnected;
    }

    public IDisposable Subscribe(string channel, Action<string, JsonNode> handler)
    {
        var list = _subs.GetOrAdd(channel, _ => new List<Action<string, JsonNode>>());
        lock (list) list.Add(handler);
        return new Unsubscriber(() => { lock (list) list.Remove(handler); });
    }

    public async Task SendAsync(string channel, string type, object? payload = null, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        var node = payload == null ? new JsonObject() : JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        node["ch"] = channel; node["type"] = type;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(Json));
        await _sendLock.WaitAsync(ct);
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch (WebSocketException) { }
        finally { _sendLock.Release(); }
    }

    public async ValueTask SendBinaryAsync(byte channelId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        var buf = new byte[payload.Length + 1];
        buf[0] = channelId;
        payload.CopyTo(buf.AsMemory(1));
        await _sendLock.WaitAsync(ct);
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(buf, WebSocketMessageType.Binary, true, ct); }
        catch (WebSocketException) { }
        finally { _sendLock.Release(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var token = _tokenProvider();
            if (string.IsNullOrEmpty(token)) { State = ConnectionState.Disconnected; return; }

            State = first ? ConnectionState.Connecting : ConnectionState.Reconnecting;
            first = false;
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
                var uri = new Uri($"{_baseUri}?token={Uri.EscapeDataString(token)}&v={_appVersion}&os={os}");
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(uri, connectCts.Token);
                _ws = ws;
                SetError(null);
                delay = TimeSpan.FromSeconds(1);
                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (WebSocketException ex) when (ex.Message.Contains("401"))
            {
                SetError("El servidor rechazó la vinculación. Vuelve a vincular la app.");
                _logger.LogWarning("[Conn] 401: token rechazado");
                State = ConnectionState.Disconnected;
                Unauthorized?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                _logger.LogWarning("[Conn] {Msg}", ex.Message);
            }
            finally
            {
                _ws = null;
                ws.Dispose();
                Latency = null;
            }
            if (ct.IsCancellationRequested) break;
            State = ConnectionState.Reconnecting;
            try { await Task.Delay(delay, ct); } catch { break; }
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
        State = ConnectionState.Disconnected;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = PingLoopAsync(pingCts.Token);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        // Completar el handshake: si no, el servidor ve un corte abrupto.
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                        return;
                    }
                    if (r.MessageType == WebSocketMessageType.Text) sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                Dispatch(sb.ToString());
            }
        }
        finally { pingCts.Cancel(); try { await ping; } catch { } }
    }

    private void Dispatch(string text)
    {
        JsonNode? j;
        try { j = JsonNode.Parse(text); } catch { return; }
        var ch = j?["ch"]?.GetValue<string>() ?? "";
        var type = j?["type"]?.GetValue<string>() ?? "";
        if (ch == "core")
        {
            if (type == "hello")
            {
                Login = j!["login"]?.GetValue<string>();
                _modules.Clear();
                if (j["modules"] is JsonObject mods)
                    foreach (var (k, v) in mods) _modules[k] = v;
                State = ConnectionState.Connected;
                HelloReceived?.Invoke();
            }
            else if (type == "pong" && j!["t"] is JsonValue tv && tv.TryGetValue<long>(out var t))
                Latency = TimeSpan.FromMilliseconds(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            return;
        }
        if (!_subs.TryGetValue(ch, out var list)) return;
        Action<string, JsonNode>[] handlers;
        lock (list) handlers = list.ToArray();
        foreach (var h in handlers)
        {
            try { h(type, j!); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Conn] handler de {Ch}/{Type}", ch, type); }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await SendAsync("core", "ping", new { t = Stopwatch.GetTimestamp() }, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetError(string? msg) { if (LastError == msg) return; LastError = msg; LastErrorChanged?.Invoke(msg); }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}
