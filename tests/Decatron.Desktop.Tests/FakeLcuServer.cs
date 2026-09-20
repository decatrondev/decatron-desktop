using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Decatron.Desktop.Tests;

/// <summary>
/// Cliente de LoL falso: responde los endpoints del LCU que lee el watcher con lo que
/// el test le cargue, por http (el lockfile lleva el protocolo, el real dice https).
/// Escribe su propio lockfile en un directorio temporal.
/// </summary>
public sealed class FakeLcuServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Dictionary<string, string> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Port { get; }
    public string Dir { get; }
    public string LockfilePath => Path.Combine(Dir, "lockfile");
    public int Requests;

    public FakeLcuServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        Dir = Path.Combine(Path.GetTempPath(), "decatron-fake-lcu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        File.WriteAllText(LockfilePath, $"LeagueClient:1234:{Port}:secret:http");
        _loop = Task.Run(LoopAsync);
    }

    public void Set(string path, string json) { lock (_lock) _routes[path] = json; }
    public void Remove(string path) { lock (_lock) _routes.Remove(path); }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { break; }
            Interlocked.Increment(ref Requests);
            var auth = ctx.Request.Headers["Authorization"] ?? "";
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:secret"));
            string? body;
            lock (_lock) _routes.TryGetValue(ctx.Request.Url!.AbsolutePath, out body);
            ctx.Response.StatusCode = auth != expected ? 401 : body == null ? 404 : 200;
            if (body != null && auth == expected)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { await _loop; } catch { }
        try { Directory.Delete(Dir, true); } catch { }
    }
}
