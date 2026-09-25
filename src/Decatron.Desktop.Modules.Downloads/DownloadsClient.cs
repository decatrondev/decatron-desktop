using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.Downloads;

/// <summary>Un trabajo tal como lo muestran la app y el dashboard.</summary>
public sealed record DownloadJob(string Id, string Title, string Kind, string Format, JobProgress Progress, DateTime CreatedAt);

/// <summary>
/// Protocolo del canal <c>downloads</c>. El dashboard pide (analizar, descargar, cancelar, abrir la
/// carpeta) a través del servidor; acá se corre yt-dlp en la PC del streamer — con su IP, así YouTube
/// no bloquea — y se devuelve el progreso. Hasta 2 descargas a la vez; el resto espera en cola.
/// </summary>
public sealed class DownloadsClient : IDisposable
{
    public const string Channel = "downloads";
    private const int MaxParallel = 2;

    private readonly IDesktopConnection _conn;
    private readonly ToolManager _tools;
    private readonly Func<string> _folder;
    private readonly ILogger _log;
    private readonly IDisposable _sub;
    private readonly SemaphoreSlim _slots = new(MaxParallel, MaxParallel);
    private readonly ConcurrentDictionary<string, (DownloadJob Job, CancellationTokenSource Cts)> _jobs = new();

    public event Action<DownloadJob>? JobChanged;
    /// <summary>Se borró de la lista lo terminado (desde la app o desde el dashboard).</summary>
    public event Action? Cleared;

    public IReadOnlyList<DownloadJob> Jobs => _jobs.Values.Select(j => j.Job).OrderByDescending(j => j.CreatedAt).ToList();

    public DownloadsClient(IDesktopConnection conn, ToolManager tools, Func<string> folder, ILogger log)
    {
        _conn = conn;
        _tools = tools;
        _folder = folder;
        _log = log;
        _sub = conn.Subscribe(Channel, OnMessage);
        _tools.StatusChanged += status => { _ = SendStatusAsync(); };
        // El servidor no guarda nada entre reinicios: al (re)conectar se le cuenta todo de nuevo
        conn.StateChanged += st => { if (st == ConnectionState.Connected) _ = SyncAsync(); };
        conn.HelloReceived += () => _ = SyncAsync();
    }

    private void OnMessage(string type, JsonNode msg)
    {
        switch (type)
        {
            case "probe": _ = ProbeAsync(msg["requestId"]?.GetValue<string>(), msg["url"]?.GetValue<string>()); break;
            case "start": Start(msg); break;
            case "cancel": Cancel(msg["jobId"]?.GetValue<string>()); break;
            case "openFolder": OpenFolder(msg["jobId"]?.GetValue<string>()); break;
            case "sync": _ = SyncAsync(); break;
            case "clear": ClearFinished(); break;
        }
    }

    public async Task SyncAsync()
    {
        await SendStatusAsync();
        foreach (var job in Jobs) await SendJobAsync(job);
    }

    public Task SendStatusAsync()
    {
        var s = _tools.Status;
        return SafeSend("status", new
        {
            ready = s.Ready, preparing = s.Preparing, stage = s.Stage, percent = s.Percent, error = s.Error,
            ytDlpVersion = s.YtDlpVersion, ffmpegReady = s.FfmpegReady, folder = _folder(), os = _tools.Os.ToString().ToLowerInvariant(),
        });
    }

    private async Task ProbeAsync(string? requestId, string? url)
    {
        if (requestId == null) return;
        try
        {
            if (url == null) { await SafeSend("probeResult", new { requestId, ok = false, error = "invalid_url" }); return; }
            if (!await _tools.EnsureAsync()) { await SafeSend("probeResult", new { requestId, ok = false, error = "tools_unavailable" }); return; }
            var (info, error) = await DownloadRunner.ProbeAsync(_tools.YtDlpPath, url, CancellationToken.None);
            await SafeSend("probeResult", new { requestId, ok = info != null, error, info });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "no se pudo analizar {Url}", url);
            await SafeSend("probeResult", new { requestId, ok = false, error = "probe_failed" });
        }
    }

    private void Start(JsonNode msg)
    {
        var jobId = msg["jobId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 || _jobs.ContainsKey(jobId)) return;
        var title = msg["title"]?.GetValue<string>() ?? "";
        var (options, error) = DownloadOptions.Parse(msg["options"]);
        var job = new DownloadJob(jobId, title.Length > 200 ? title[..200] : title, options?.Kind ?? "video", options?.Format ?? "",
            new JobProgress(error == null ? "queued" : "error", null, null, null, null, error), DateTime.UtcNow);
        var cts = new CancellationTokenSource();
        _jobs[jobId] = (job, cts);
        Publish(job);
        if (options != null) _ = RunAsync(jobId, options, cts.Token);
    }

    private async Task RunAsync(string jobId, DownloadOptions options, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct);
        }
        catch (OperationCanceledException) { Update(jobId, new JobProgress("canceled", null, null, null, null, null)); return; }

        try
        {
            Update(jobId, new JobProgress("preparing", null, null, null, null, null));
            if (!await _tools.EnsureAsync(ct)) { Update(jobId, new JobProgress("error", null, null, null, null, "tools_unavailable")); return; }
            // YouTube da video y audio por separado: sin ffmpeg casi nada se puede armar
            if (!_tools.FfmpegReady)
            {
                Update(jobId, new JobProgress("error", null, null, null, null, "ffmpeg_missing"));
                return;
            }
            await DownloadRunner.RunAsync(_tools.YtDlpPath, options, _folder(), _tools.FfmpegDir, p => Update(jobId, p), ct);
        }
        catch (OperationCanceledException) { Update(jobId, new JobProgress("canceled", null, null, null, null, null)); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "descarga {Job} falló", jobId);
            Update(jobId, new JobProgress("error", null, null, null, null, "download_failed"));
        }
        finally { _slots.Release(); }
    }

    public void Cancel(string? jobId)
    {
        if (jobId != null && _jobs.TryGetValue(jobId, out var entry) && entry.Job.Progress.State is "queued" or "preparing" or "downloading" or "processing")
            entry.Cts.Cancel();
    }

    /// <summary>Borra de la lista lo que ya terminó (no toca los archivos).</summary>
    public void ClearFinished()
    {
        foreach (var (id, entry) in _jobs)
            if (entry.Job.Progress.State is "done" or "error" or "canceled")
                _jobs.TryRemove(id, out _);
        Cleared?.Invoke();
        _ = SafeSend("cleared", new { });
    }

    /// <summary>Muestra el archivo en el explorador (o la carpeta de descargas si no hay archivo).</summary>
    public void OpenFolder(string? jobId)
    {
        string? file = jobId != null && _jobs.TryGetValue(jobId, out var entry) ? entry.Job.Progress.FilePath : null;
        var folder = _folder();
        try
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", file != null && File.Exists(file) ? $"/select,\"{file}\"" : $"\"{folder}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", file != null && File.Exists(file) ? new[] { "-R", file } : new[] { folder });
            else
                Process.Start("xdg-open", folder);
        }
        catch (Exception ex) { _log.LogWarning(ex, "no se pudo abrir la carpeta de descargas"); }
    }

    private void Update(string jobId, JobProgress progress)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) return;
        // Una vez terminado, un aviso tardío del proceso no lo revive
        if (entry.Job.Progress.State is "done" or "error" or "canceled") return;
        var job = entry.Job with { Progress = progress };
        _jobs[jobId] = (job, entry.Cts);
        Publish(job);
    }

    private void Publish(DownloadJob job)
    {
        JobChanged?.Invoke(job);
        _ = SendJobAsync(job);
    }

    private Task SendJobAsync(DownloadJob job) => SafeSend("progress", new
    {
        jobId = job.Id, title = job.Title, kind = job.Kind, format = job.Format,
        state = job.Progress.State, percent = job.Progress.Percent, speed = job.Progress.Speed, eta = job.Progress.Eta,
        // Solo el nombre: la ruta completa de la PC del streamer no tiene por qué viajar
        fileName = job.Progress.FilePath != null ? Path.GetFileName(job.Progress.FilePath) : null,
        error = job.Progress.Error, createdAt = job.CreatedAt,
    });

    private async Task SafeSend(string type, object payload)
    {
        if (_conn.State != ConnectionState.Connected) return;
        try { await _conn.SendAsync(Channel, type, payload); }
        catch (Exception ex) { _log.LogDebug(ex, "no se pudo mandar {Type}", type); }
    }

    public void Dispose()
    {
        _sub.Dispose();
        foreach (var (_, entry) in _jobs) entry.Cts.Cancel();
    }
}
