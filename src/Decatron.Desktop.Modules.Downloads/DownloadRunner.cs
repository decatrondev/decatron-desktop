using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Decatron.Desktop.Modules.Downloads;

/// <summary>queued | preparing | downloading | processing | done | error | canceled</summary>
public sealed record JobProgress(string State, double? Percent, string? Speed, string? Eta, string? FilePath, string? Error);

/// <summary>Lo que se sabe de un link antes de descargarlo (para ofrecer calidades y subtítulos).</summary>
public sealed record ProbeInfo(string Title, string? Uploader, double? Duration, string? Thumbnail, string WebpageUrl,
    IReadOnlyList<int> Heights, bool HasVideo, IReadOnlyList<string> Subtitles, string Extractor);

/// <summary>Corre yt-dlp: analizar un link y descargar con progreso. Un proceso por trabajo.</summary>
public static class DownloadRunner
{
    public static async Task<(ProbeInfo? Info, string? Error)> ProbeAsync(string ytDlp, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return (null, "invalid_url");
        var (code, stdout, stderr) = await ToolManager.RunAsync(ytDlp, new[] { "-J", "--no-playlist", "--no-warnings", "--", uri.ToString() }, TimeSpan.FromSeconds(60), ct);
        if (code != 0 || !stdout.TrimStart().StartsWith('{'))
            return (null, ClassifyError(stderr + stdout));
        try
        {
            return (ParseProbe(stdout), null);
        }
        catch (JsonException)
        {
            return (null, "probe_failed");
        }
    }

    public static ProbeInfo ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        var hasVideo = false;
        if (r.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = Str(f, "vcodec");
                if (vcodec == null || vcodec == "none") continue;
                if (f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                {
                    hasVideo = true;
                    // Se ofrece la altura estándar más cercana por encima (1080 cubre 1072, etc.)
                    var std = DownloadOptions.Heights.Where(x => x >= h.GetInt32() - 16).DefaultIfEmpty(h.GetInt32()).Min();
                    heights.Add(std);
                }
            }
        }
        var subs = r.TryGetProperty("subtitles", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().Select(p => p.Name).Where(n => n != "live_chat").Take(40).ToList()
            : new List<string>();

        return new ProbeInfo(
            Str(r, "title") ?? "",
            Str(r, "uploader") ?? Str(r, "channel"),
            r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
            Str(r, "thumbnail"),
            Str(r, "webpage_url") ?? "",
            heights.ToList(),
            hasVideo,
            subs,
            Str(r, "extractor_key") ?? "");
    }

    /// <summary>Descarga. Avisa cada cambio de estado o de porcentaje; termina con done, error o canceled.</summary>
    public static async Task RunAsync(string ytDlp, DownloadOptions options, string folder, string? ffmpegDir, Action<JobProgress> report, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var psi = new ProcessStartInfo(ytDlp) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in options.BuildArgs(folder, ffmpegDir)) psi.ArgumentList.Add(a);

        report(new JobProgress("downloading", 0, null, null, null, null));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar yt-dlp");
        string? filePath = null;
        var errors = new System.Text.StringBuilder();
        var lastPercent = -1.0;

        var stdout = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("DLPATH|")) { filePath = line[7..].Trim(); continue; }
                var progress = ParseProgressLine(line);
                if (progress != null)
                {
                    // Un aviso por cada punto entero: yt-dlp manda decenas por segundo
                    if (Math.Floor(progress.Percent ?? 0) != Math.Floor(lastPercent) || progress.Percent >= 100)
                    {
                        lastPercent = progress.Percent ?? 0;
                        report(progress);
                    }
                    continue;
                }
                if (IsPostProcessing(line)) report(new JobProgress("processing", 100, null, null, null, null));
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                // El progreso del posprocesado (unir, convertir) sale por stderr, a diferencia del de descarga
                if (line.StartsWith("DLPP|")) { report(new JobProgress("processing", 100, null, null, null, null)); continue; }
                lock (errors) errors.AppendLine(line);
            }
        });

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ya terminó */ }
            report(new JobProgress("canceled", null, null, null, null, null));
            return;
        }
        await Task.WhenAll(stdout, stderr);

        if (p.ExitCode == 0 && filePath != null)
            report(new JobProgress("done", 100, null, null, filePath, null));
        else
            report(new JobProgress("error", null, null, null, null, ClassifyError(errors.ToString())));
    }

    /// <summary>"DLP| 42.3%|1.20MiB/s|00:12" → descargando 42.3 %.</summary>
    public static JobProgress? ParseProgressLine(string line)
    {
        if (!line.StartsWith("DLP|")) return null;
        var parts = line.Split('|');
        if (parts.Length < 4) return null;
        double? percent = double.TryParse(parts[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        static string? Clean(string s) { s = s.Trim(); return s.Length == 0 || s == "NA" || s.StartsWith("Unknown") ? null : s; }
        return new JobProgress("downloading", percent, Clean(parts[2]), Clean(parts[3]), null, null);
    }

    private static bool IsPostProcessing(string line) =>
        line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[VideoConvertor]")
        || line.StartsWith("[EmbedSubtitle]") || line.StartsWith("[EmbedThumbnail]") || line.StartsWith("[Metadata]")
        || line.StartsWith("[FixupM3u8]") || line.StartsWith("[ThumbnailsConvertor]");

    /// <summary>El error de yt-dlp (en inglés) → una clave que el dashboard traduce.</summary>
    public static string ClassifyError(string output)
    {
        var s = output.ToLowerInvariant();
        if (s.Contains("unsupported url")) return "unsupported_site";
        if (s.Contains("private video") || s.Contains("sign in to confirm your age")) return "private";
        if (s.Contains("video unavailable") || s.Contains("not available") || s.Contains("404")) return "not_found";
        if (s.Contains("not a bot") || s.Contains("429")) return "blocked";
        if (s.Contains("ffmpeg") && (s.Contains("not found") || s.Contains("not installed"))) return "ffmpeg_missing";
        if (s.Contains("no space left") || s.Contains("disk full")) return "disk_full";
        if (s.Contains("permission denied") || s.Contains("access is denied")) return "no_permission";
        if (s.Contains("requested format is not available")) return "format_unavailable";
        return "download_failed";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
