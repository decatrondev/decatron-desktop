using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.Downloads;

/// <summary>Cómo va una importación, para la pantalla de la app.</summary>
public sealed record ImportProgress(string JobId, int Total, int Done, int Found, bool Running);

/// <summary>
/// Canal <c>songimport</c>: todo lo que toca a YouTube al importar playlists pasa por acá, con la IP del
/// streamer, para que el servidor no le haga consultas a YouTube (se las bloquea).
/// <list type="bullet">
/// <item><c>match</c>: el servidor leyó una playlist de Spotify, Deezer o Apple Music; acá se busca cada
/// canción en YouTube y se devuelve el video elegido por <see cref="SongMatcher"/>.</item>
/// <item><c>list</c>: una playlist de YouTube; acá se lee (lista plana de yt-dlp) y se devuelven sus videos.</item>
/// </list>
/// </summary>
public sealed class SongImportClient : IDisposable
{
    public const string Channel = "songimport";
    /// <summary>1: buscar canciones. 2: también leer playlists de YouTube.</summary>
    public const int ProtocolVersion = 2;
    public const int MaxListItems = 5000;
    private const int MaxParallel = 2;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    private readonly IDesktopConnection _conn;
    private readonly ToolManager _tools;
    private readonly ILogger _log;
    private readonly IDisposable _sub;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobs = new();

    public event Action<ImportProgress>? ProgressChanged;

    public SongImportClient(IDesktopConnection conn, ToolManager tools, ILogger log)
    {
        _conn = conn;
        _tools = tools;
        _log = log;
        _sub = conn.Subscribe(Channel, OnMessage);
        // Anuncia que esta versión sabe buscar (las viejas no lo dicen y el dashboard pide actualizar)
        conn.StateChanged += st => { if (st == ConnectionState.Connected) _ = SafeSend("ready", new { version = ProtocolVersion }); };
        conn.HelloReceived += () => _ = SafeSend("ready", new { version = ProtocolVersion });
    }

    private void OnMessage(string type, JsonNode msg)
    {
        switch (type)
        {
            case "match": _ = RunAsync(msg); break;
            case "list": _ = ListAsync(msg); break;
            case "cancel":
                if (msg["jobId"]?.GetValue<string>() is { } id && _jobs.TryGetValue(id, out var cts)) cts.Cancel();
                break;
        }
    }

    private async Task RunAsync(JsonNode msg)
    {
        var jobId = msg["jobId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 || msg["items"] is not JsonArray items) return;
        var cts = new CancellationTokenSource();
        if (!_jobs.TryAdd(jobId, cts)) return;

        var queue = new ConcurrentQueue<(int I, SongQuery Song)>();
        foreach (var it in items)
        {
            if (it?["i"]?.GetValue<int>() is not { } i) continue;
            var title = it["title"]?.GetValue<string>() ?? "";
            if (title.Length == 0) continue;
            int? duration = it["duration"] is JsonValue d && d.TryGetValue<int>(out var secs) && secs > 0 ? secs : null;
            queue.Enqueue((i, new SongQuery(title, it["artist"]?.GetValue<string>() ?? "", duration)));
        }

        var total = queue.Count;
        var done = 0;
        var found = 0;
        ProgressChanged?.Invoke(new ImportProgress(jobId, total, 0, 0, true));
        try
        {
            if (!await _tools.EnsureAsync(cts.Token))
            {
                while (queue.TryDequeue(out var x))
                    await SafeSend("matched", new { jobId, i = x.I, ok = false, error = "tools_unavailable" });
                return;
            }

            async Task Worker()
            {
                while (!cts.IsCancellationRequested && queue.TryDequeue(out var x))
                {
                    var match = await FindAsync(x.Song, cts.Token);
                    if (cts.IsCancellationRequested) break;
                    if (match != null) Interlocked.Increment(ref found);
                    await SafeSend("matched", match == null
                        ? new { jobId, i = x.I, ok = false, error = "no_match" } as object
                        : new { jobId, i = x.I, ok = true, videoId = match.Id, title = match.Title, channel = match.Channel, channelId = match.ChannelId, duration = match.DurationSeconds });
                    ProgressChanged?.Invoke(new ImportProgress(jobId, total, Interlocked.Increment(ref done), found, true));
                }
            }
            await Task.WhenAll(Enumerable.Range(0, MaxParallel).Select(_ => Worker()));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "importación {Job} falló", jobId); }
        finally
        {
            await SafeSend("done", new { jobId, canceled = cts.IsCancellationRequested });
            ProgressChanged?.Invoke(new ImportProgress(jobId, total, done, found, false));
            _jobs.TryRemove(jobId, out _);
            cts.Dispose();
        }
    }

    /// <summary>Lee una playlist de YouTube y devuelve sus videos (sin los privados, borrados ni en vivo).</summary>
    private async Task ListAsync(JsonNode msg)
    {
        var jobId = msg["jobId"]?.GetValue<string>();
        var url = msg["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64) return;
        var max = Math.Clamp(msg["max"]?.GetValue<int>() ?? MaxListItems, 1, MaxListItems);
        if (!SongMatcher.IsYouTubePlaylist(url))
        {
            await SafeSend("listed", new { jobId, ok = false, error = "not_a_playlist" });
            return;
        }
        ProgressChanged?.Invoke(new ImportProgress(jobId, 0, 0, 0, true));
        try
        {
            if (!await _tools.EnsureAsync())
            {
                await SafeSend("listed", new { jobId, ok = false, error = "tools_unavailable" });
                return;
            }
            var (code, stdout, stderr) = await ToolManager.RunAsync(_tools.YtDlpPath,
                new[] { "--flat-playlist", "--dump-single-json", "--no-warnings", "--playlist-end", max.ToString(), "--", url! },
                TimeSpan.FromMinutes(3), CancellationToken.None);
            var (name, entries) = code == 0 ? SongMatcher.ParsePlaylist(stdout) : (null, new List<SongCandidate>());
            await SafeSend("listed", entries.Count == 0
                ? new { jobId, ok = false, error = code == 0 ? "empty" : "list_failed" } as object
                : new
                {
                    jobId, ok = true, name,
                    items = entries.Select(e => new { videoId = e.Id, title = e.Title, channel = e.Channel, channelId = e.ChannelId, duration = e.DurationSeconds })
                });
            ProgressChanged?.Invoke(new ImportProgress(jobId, entries.Count, entries.Count, entries.Count, false));
            if (code != 0) _log.LogWarning("no se pudo leer la playlist: {Err}", stderr.Length > 300 ? stderr[..300] : stderr);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "no se pudo leer la playlist {Url}", url);
            await SafeSend("listed", new { jobId, ok = false, error = "list_failed" });
            ProgressChanged?.Invoke(new ImportProgress(jobId, 0, 0, 0, false));
        }
    }

    /// <summary>YouTube Music (verificando duración) en paralelo con la búsqueda normal y "… audio".</summary>
    private async Task<SongCandidate?> FindAsync(SongQuery song, CancellationToken ct)
    {
        try
        {
            var query = SongMatcher.BuildQuery(song);
            var music = SearchAsync(new[] { "--dump-json", "--flat-playlist", "--no-warnings", "--playlist-end", "5", "--",
                $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}#songs" }, ct);
            var video = SearchAsync(new[] { "--dump-json", "--skip-download", "--flat-playlist", "--no-warnings", $"ytsearch8:{query}" }, ct);
            var audio = song.DurationSeconds is > 0
                ? SearchAsync(new[] { "--dump-json", "--skip-download", "--flat-playlist", "--no-warnings", $"ytsearch8:{query} audio" }, ct)
                : Task.FromResult(new List<SongCandidate>());
            await Task.WhenAll(music, video, audio);

            // YouTube Music no trae la duración en la búsqueda: se lee del video antes de elegirlo
            if (song.DurationSeconds is > 0)
            {
                foreach (var c in SongMatcher.MusicCandidates(music.Result, song))
                {
                    var full = await VideoAsync(c.Id, ct);
                    if (full != null && SongMatcher.DurationMatches(full.DurationSeconds, song.DurationSeconds))
                        return full;
                }
            }
            return SongMatcher.Pick(video.Result.Concat(audio.Result), song);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "no se pudo buscar {Title}", song.Title);
            return null;
        }
    }

    private async Task<List<SongCandidate>> SearchAsync(string[] args, CancellationToken ct)
    {
        var (code, stdout, _) = await ToolManager.RunAsync(_tools.YtDlpPath, args, SearchTimeout, ct);
        return code == 0 || stdout.Length > 0 ? SongMatcher.ParseSearch(stdout) : new List<SongCandidate>();
    }

    private async Task<SongCandidate?> VideoAsync(string id, CancellationToken ct)
    {
        var (code, stdout, _) = await ToolManager.RunAsync(_tools.YtDlpPath,
            new[] { "--dump-json", "--skip-download", "--no-playlist", "--no-warnings", "--", $"https://www.youtube.com/watch?v={id}" }, SearchTimeout, ct);
        if (code != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new SongCandidate(id, S("title") ?? "", S("channel") ?? S("uploader") ?? "", S("channel_id"),
                r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)Math.Round(d.GetDouble()) : null);
        }
        catch (JsonException) { return null; }
    }

    private async Task SafeSend(string type, object payload)
    {
        try { await _conn.SendAsync(Channel, type, payload); }
        catch (Exception ex) { _log.LogDebug(ex, "no se pudo mandar {Type}", type); }
    }

    public void Dispose()
    {
        foreach (var cts in _jobs.Values) cts.Cancel();
        _sub.Dispose();
    }
}
