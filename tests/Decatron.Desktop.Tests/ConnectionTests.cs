using Decatron.Desktop.Core.Connection;
using Decatron.Desktop.Modules.Translation;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Decatron.Desktop.Tests;

public class ConnectionTests
{
    private static async Task WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(cond(), "condición no se cumplió a tiempo");
    }

    [Fact]
    public async Task Conecta_recibe_hello_y_expone_modulos()
    {
        await using var server = new FakeDesktopServer();
        await using var conn = new DesktopConnection(server.WsUri, () => server.ValidToken, "0.1.0-test", NullLogger.Instance);
        var states = new List<ConnectionState>();
        conn.StateChanged += states.Add;
        conn.Start();

        await WaitUntil(() => conn.State == ConnectionState.Connected);
        Assert.Equal("tester", conn.Login);
        Assert.True(conn.Modules.ContainsKey("translation"));
        Assert.Contains(ConnectionState.Connecting, states);
    }

    [Fact]
    public async Task Reconecta_solo_si_el_servidor_corta()
    {
        await using var server = new FakeDesktopServer();
        await using var conn = new DesktopConnection(server.WsUri, () => server.ValidToken, "0.1.0-test", NullLogger.Instance);
        conn.Start();
        await WaitUntil(() => conn.State == ConnectionState.Connected);

        await server.KickAsync();
        // Tras el corte hay backoff de 1 s; la segunda conexión tiene que llegar sola.
        await WaitUntil(() => server.Connections == 2 && conn.State == ConnectionState.Connected, 10000);
    }

    [Fact]
    public async Task Token_rechazado_dispara_Unauthorized_y_no_reintenta()
    {
        await using var server = new FakeDesktopServer();
        await using var conn = new DesktopConnection(server.WsUri, () => "malo", "0.1.0-test", NullLogger.Instance);
        var unauthorized = false;
        conn.Unauthorized += () => unauthorized = true;
        conn.Start();
        await WaitUntil(() => unauthorized);
        await Task.Delay(300);
        Assert.Equal(ConnectionState.Disconnected, conn.State);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Canal_translation_start_audio_stop()
    {
        await using var server = new FakeDesktopServer();
        await using var conn = new DesktopConnection(server.WsUri, () => server.ValidToken, "0.1.0-test", NullLogger.Instance);
        conn.Start();
        await WaitUntil(() => conn.State == ConnectionState.Connected);

        using var client = new TranslationClient(conn);
        Assert.True(client.IsAvailable);
        Assert.True(client.IsEnabled);
        Assert.Equal(new[] { "en", "pt" }, client.ConfiguredLanguages);

        TranslationStatus? status = null; string? stopReason = null;
        client.StatusChanged += s => status = s;
        client.Stopped += (r, _) => stopReason = r;

        await client.StartAsync(CancellationToken.None);
        await WaitUntil(() => status is { Active: true });
        Assert.Equal(3, status!.Listeners["en"]);

        var frame = Enumerable.Range(0, 640).Select(i => (byte)i).ToArray();
        await client.SendAudioAsync(frame, CancellationToken.None);
        await WaitUntil(() => !server.ReceivedBinary.IsEmpty);
        Assert.True(server.ReceivedBinary.TryDequeue(out var bin));
        Assert.Equal(0x01, bin![0]);
        Assert.Equal(641, bin.Length);
        Assert.Equal(frame, bin[1..]);

        await client.StopAsync(CancellationToken.None);
        await WaitUntil(() => stopReason == "stopped_by_user");
    }

    [Fact]
    public async Task Ping_mide_latencia()
    {
        await using var server = new FakeDesktopServer();
        await using var conn = new DesktopConnection(server.WsUri, () => server.ValidToken, "0.1.0-test", NullLogger.Instance);
        conn.Start();
        await WaitUntil(() => conn.State == ConnectionState.Connected);
        await conn.SendAsync("core", "ping", new { t = System.Diagnostics.Stopwatch.GetTimestamp() });
        await WaitUntil(() => conn.Latency.HasValue);
        Assert.True(conn.Latency!.Value < TimeSpan.FromSeconds(2));
    }
}
