using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Decatron.Desktop.Core.Connection;
using Decatron.Desktop.Modules.Downloads;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Decatron.Desktop.Tests;

public class DownloadsTests
{
    private static JsonNode Opt(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Opciones_invalidas_se_rechazan_antes_de_llegar_a_yt_dlp()
    {
        Assert.Equal("invalid_url", DownloadOptions.Parse(Opt("""{"url":"file:///etc/passwd"}""")).Error);
        Assert.Equal("invalid_url", DownloadOptions.Parse(Opt("""{"url":"--exec calc"}""")).Error);
        Assert.Equal("invalid_format", DownloadOptions.Parse(Opt("""{"url":"https://youtu.be/x","kind":"audio","format":"mp4"}""")).Error);
        Assert.Equal("invalid_quality", DownloadOptions.Parse(Opt("""{"url":"https://youtu.be/x","maxHeight":999}""")).Error);
        Assert.Equal("invalid_trim", DownloadOptions.Parse(Opt("""{"url":"https://youtu.be/x","trimStart":30,"trimEnd":10}""")).Error);
        var (ok, error) = DownloadOptions.Parse(Opt("""{"url":"https://youtu.be/x","subtitles":["es","en-US","; rm -rf","xx_bad"]}"""));
        Assert.Null(error);
        Assert.Equal(new[] { "es", "en-US" }, ok!.Subtitles);
    }

    [Fact]
    public void Argumentos_de_video_audio_y_recorte()
    {
        var video = DownloadOptions.Parse(Opt("""{"url":"https://www.youtube.com/watch?v=abc","kind":"video","format":"mp4","maxHeight":1080,"thumbnail":true,"subtitles":["es"]}""")).Options!;
        var args = video.BuildArgs("/tmp/out", "/tools/ffmpeg");
        // La URL siempre al final, después de "--": nunca se interpreta como opción
        Assert.Equal("--", args[^2]);
        Assert.Equal("https://www.youtube.com/watch?v=abc", args[^1]);
        Assert.Contains("res:1080,ext:mp4:m4a", args);
        Assert.Contains("--embed-subs", args);
        Assert.Contains("--embed-thumbnail", args);
        Assert.Equal("/tools/ffmpeg", args[args.IndexOf("--ffmpeg-location") + 1]);

        var audio = DownloadOptions.Parse(Opt("""{"url":"https://youtu.be/abc","kind":"audio","format":"mp3","audioQuality":"192","trimStart":10.5,"trimEnd":40}""")).Options!;
        var a = audio.BuildArgs("/tmp/out", null);
        Assert.Contains("-x", a);
        Assert.Equal("192K", a[a.IndexOf("--audio-quality") + 1]);
        Assert.Equal("*10.5-40", a[a.IndexOf("--download-sections") + 1]);
        Assert.DoesNotContain("--force-keyframes-at-cuts", a);   // solo hace falta en video
        Assert.DoesNotContain("--ffmpeg-location", a);
        Assert.DoesNotContain("--embed-subs", a);
    }

    [Fact]
    public void Lee_progreso_errores_y_sumas()
    {
        var p = DownloadRunner.ParseProgressLine("DLP| 42.3%|1.20MiB/s|00:12")!;
        Assert.Equal(42.3, p.Percent!.Value, 1);
        Assert.Equal("1.20MiB/s", p.Speed);
        Assert.Equal("00:12", p.Eta);
        Assert.Null(DownloadRunner.ParseProgressLine("[download] Destination: x.mp4"));
        Assert.Null(DownloadRunner.ParseProgressLine("DLP|  NA|Unknown B/s|NA")!.Speed);

        Assert.Equal("unsupported_site", DownloadRunner.ClassifyError("ERROR: Unsupported URL: https://x"));
        Assert.Equal("private", DownloadRunner.ClassifyError("ERROR: [youtube] x: Private video"));
        Assert.Equal("disk_full", DownloadRunner.ClassifyError("OSError: [Errno 28] No space left on device"));

        const string sums = "0f192b7ec147ab6288885d6351d9ab67367640029b4377576ef46dd79cf7b202  yt-dlp_macos\n66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a  yt-dlp.exe\n";
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a", ToolManager.ParseChecksum(sums, "yt-dlp.exe"));
        Assert.Null(ToolManager.ParseChecksum(sums, "yt-dlp_linux"));
    }

    [Fact]
    public void Analiza_el_json_de_yt_dlp()
    {
        const string json = """
        {"title":"Canción","uploader":"Artista","duration":213.4,"thumbnail":"https://i.ytimg.com/x.jpg","webpage_url":"https://www.youtube.com/watch?v=abc","extractor_key":"Youtube",
         "formats":[{"vcodec":"none","acodec":"opus"},{"vcodec":"avc1","height":1080},{"vcodec":"vp9","height":1072},{"vcodec":"avc1","height":720},{"vcodec":"avc1","height":360}],
         "subtitles":{"es":[],"en":[],"live_chat":[]}}
        """;
        var info = DownloadRunner.ParseProbe(json);
        Assert.Equal("Canción", info.Title);
        Assert.True(info.HasVideo);
        Assert.Equal(new[] { 1080, 720, 360 }, info.Heights);   // 1072 cuenta como 1080
        Assert.Equal(new[] { "es", "en" }, info.Subtitles);
        Assert.Equal(213.4, info.Duration);
    }

    /// <summary>Sirve archivos desde memoria como si fueran los releases de GitHub.</summary>
    private sealed class FakeReleases : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.Segments[^1];
            return Task.FromResult(Files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task Baja_yt_dlp_verificado_y_rechaza_uno_alterado()
    {
        if (OperatingSystem.IsWindows()) return;   // el yt-dlp falso es un script de shell
        var script = Encoding.UTF8.GetBytes("#!/bin/sh\necho 2099.01.01\n");

        var good = new FakeReleases();
        good.Files["yt-dlp_linux"] = script;
        good.Files["SHA2-256SUMS"] = Encoding.UTF8.GetBytes($"{Sha(script)}  yt-dlp_linux\n");
        var dir = Path.Combine(Path.GetTempPath(), "decatron-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tools = new ToolManager(NullLogger.Instance, new HttpClient(good), dir, ToolOs.Linux, "https://fake/yt/", "https://fake/ff/");
            Assert.True(await tools.EnsureAsync());
            Assert.Equal("2099.01.01", tools.YtDlpVersion);
            Assert.True(File.Exists(tools.YtDlpPath));

            var bad = new FakeReleases();
            bad.Files["yt-dlp_linux"] = Encoding.UTF8.GetBytes("#!/bin/sh\necho hackeado\n");
            bad.Files["SHA2-256SUMS"] = Encoding.UTF8.GetBytes($"{Sha(script)}  yt-dlp_linux\n");
            var dir2 = dir + "-bad";
            var tampered = new ToolManager(NullLogger.Instance, new HttpClient(bad), dir2, ToolOs.Linux, "https://fake/yt/", "https://fake/ff/");
            Assert.False(await tampered.EnsureAsync());
            Assert.False(File.Exists(tampered.YtDlpPath));
            Assert.Contains("SHA-256", tampered.Status.Error);
            Directory.Delete(dir2, true);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Protocolo_responde_errores_por_el_canal()
    {
        await using var server = new FakeDesktopServer();
        await using var conn = new DesktopConnection(server.WsUri, () => server.ValidToken, "0.1.0-test", NullLogger.Instance);
        conn.Start();
        await WaitUntil(() => conn.State == ConnectionState.Connected);

        // Herramientas que no se pueden bajar (404): la descarga termina en error, no se cuelga
        var dir = Path.Combine(Path.GetTempPath(), "decatron-tools-" + Guid.NewGuid().ToString("N"));
        var tools = new ToolManager(NullLogger.Instance, new HttpClient(new FakeReleases()), dir, ToolOs.Linux, "https://fake/yt/", "https://fake/ff/");
        using var client = new DownloadsClient(conn, tools, () => Path.Combine(dir, "out"), NullLogger.Instance);
        try
        {
            await server.SendAsync(new { ch = "downloads", type = "start", jobId = "j1", title = "x", options = new { url = "notaurl" } });
            await server.SendAsync(new { ch = "downloads", type = "start", jobId = "j2", title = "y", options = new { url = "https://youtu.be/abc", kind = "audio", format = "mp3" } });
            await server.SendAsync(new { ch = "downloads", type = "probe", requestId = "r1", url = "https://youtu.be/abc" });

            await WaitUntil(() => Progress(server, "j1") == "error" && Progress(server, "j2") == "error" && server.Received.Any(m => m["type"]?.GetValue<string>() == "probeResult"));
            Assert.Equal("invalid_url", Last(server, "j1")!["error"]!.GetValue<string>());
            Assert.Equal("tools_unavailable", Last(server, "j2")!["error"]!.GetValue<string>());
            var probe = server.Received.Last(m => m["type"]?.GetValue<string>() == "probeResult");
            Assert.False(probe["ok"]!.GetValue<bool>());
            Assert.Equal("tools_unavailable", probe["error"]!.GetValue<string>());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static JsonNode? Last(FakeDesktopServer s, string jobId) =>
        s.Received.LastOrDefault(m => m["ch"]?.GetValue<string>() == "downloads" && m["type"]?.GetValue<string>() == "progress" && m["jobId"]?.GetValue<string>() == jobId);

    private static string? Progress(FakeDesktopServer s, string jobId) => Last(s, jobId)?["state"]?.GetValue<string>();

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 10000)
    {
        var start = Environment.TickCount64;
        while (!cond())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException();
            await Task.Delay(50);
        }
    }
}
