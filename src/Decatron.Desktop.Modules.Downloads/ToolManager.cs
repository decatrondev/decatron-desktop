using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.Downloads;

public enum ToolOs { Windows, MacOs, Linux }

/// <summary>Estado de las herramientas para la vista y el dashboard.</summary>
public sealed record ToolStatus(bool Ready, bool Preparing, string? YtDlpVersion, bool FfmpegReady, string? Stage, int? Percent, string? Error);

/// <summary>
/// yt-dlp y ffmpeg: no van en el instalador (sumarían ~110 MB a cada descarga de la app). Se bajan
/// la primera vez que se usa el módulo, se verifican con el SHA-256 que publica cada proyecto y quedan
/// en %LocalAppData%\Decatron Desktop\tools, fuera de la carpeta que Velopack reemplaza al actualizar.
/// yt-dlp se actualiza solo una vez por día (YouTube lo rompe seguido).
/// En macOS/Linux se usa el ffmpeg del sistema si existe; en macOS, si no, se baja de evermeet.
/// </summary>
public sealed class ToolManager
{
    public const string YtDlpBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    public const string FfmpegBase = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
    private const string FfmpegWinZip = "ffmpeg-master-latest-win64-gpl-shared.zip";
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _ytDlpBase;
    private readonly string _ffmpegBase;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolOs Os { get; }
    public string ToolsDir { get; }
    public string YtDlpPath => Path.Combine(ToolsDir, Os == ToolOs.Windows ? "yt-dlp.exe" : "yt-dlp");
    private string OwnFfmpegDir => Path.Combine(ToolsDir, "ffmpeg");

    /// <summary>Carpeta de ffmpeg para --ffmpeg-location, o null si se usa el del sistema (PATH).</summary>
    public string? FfmpegDir { get; private set; }
    public bool FfmpegReady { get; private set; }
    public string? YtDlpVersion { get; private set; }

    public ToolStatus Status { get; private set; } = new(false, false, null, false, null, null, null);
    public event Action<ToolStatus>? StatusChanged;

    public ToolManager(ILogger log, HttpClient? http = null, string? toolsDir = null, ToolOs? os = null, string? ytDlpBase = null, string? ffmpegBase = null)
    {
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("DecatronDesktop");
        Os = os ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ToolOs.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ToolOs.MacOs : ToolOs.Linux);
        ToolsDir = toolsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Decatron Desktop", "tools");
        _ytDlpBase = ytDlpBase ?? YtDlpBase;
        _ffmpegBase = ffmpegBase ?? FfmpegBase;
    }

    private string YtDlpAsset => Os switch { ToolOs.Windows => "yt-dlp.exe", ToolOs.MacOs => "yt-dlp_macos", _ => "yt-dlp_linux" };

    /// <summary>Deja todo listo (baja lo que falte, actualiza yt-dlp si toca). Seguro de llamar varias veces.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ToolsDir);

            if (!File.Exists(YtDlpPath))
            {
                Report("ytdlp", 0);
                await DownloadVerifiedAsync(_ytDlpBase + YtDlpAsset, _ytDlpBase + "SHA2-256SUMS", YtDlpAsset, YtDlpPath, "ytdlp", ct);
                MakeExecutable(YtDlpPath);
                Touch();
            }
            else if (DueForUpdate())
            {
                Report("update", null);
                await RunAsync(YtDlpPath, new[] { "-U" }, TimeSpan.FromMinutes(3), ct);
                Touch();
            }

            await EnsureFfmpegAsync(ct);

            var (code, version, _) = await RunAsync(YtDlpPath, new[] { "--version" }, TimeSpan.FromSeconds(30), ct);
            YtDlpVersion = code == 0 ? version.Trim() : null;
            SetStatus(new ToolStatus(YtDlpVersion != null, false, YtDlpVersion, FfmpegReady, null, null,
                YtDlpVersion == null ? "yt-dlp no arranca" : FfmpegReady ? null : FfmpegMissingMessage));
            return YtDlpVersion != null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "no se pudieron preparar yt-dlp/ffmpeg");
            SetStatus(new ToolStatus(false, false, YtDlpVersion, FfmpegReady, null, null, ex.Message));
            return false;
        }
        finally { _gate.Release(); }
    }

    private string FfmpegMissingMessage => Os == ToolOs.Linux
        ? "Falta ffmpeg: instálalo con el gestor de paquetes (ej. sudo apt install ffmpeg). Sin él no se pueden unir video y audio ni convertir a mp3."
        : "Falta ffmpeg: sin él no se pueden unir video y audio ni convertir a mp3.";

    private async Task EnsureFfmpegAsync(CancellationToken ct)
    {
        var ownExe = Path.Combine(OwnFfmpegDir, Os == ToolOs.Windows ? "ffmpeg.exe" : "ffmpeg");
        if (File.Exists(ownExe)) { FfmpegDir = OwnFfmpegDir; FfmpegReady = true; return; }

        if (Os != ToolOs.Windows && FindInPath("ffmpeg") != null) { FfmpegDir = null; FfmpegReady = true; return; }

        if (Os == ToolOs.Windows)
        {
            var zip = Path.Combine(ToolsDir, FfmpegWinZip);
            Report("ffmpeg", 0);
            await DownloadVerifiedAsync(_ffmpegBase + FfmpegWinZip, _ffmpegBase + "checksums.sha256", FfmpegWinZip, zip, "ffmpeg", ct);
            Report("unpack", null);
            ExtractFfmpeg(zip, OwnFfmpegDir);
            File.Delete(zip);
        }
        else if (Os == ToolOs.MacOs)
        {
            // evermeet no publica sumas; se baja por https desde su dominio y se valida que arranque
            Directory.CreateDirectory(OwnFfmpegDir);
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                Report("ffmpeg", 0);
                var zip = Path.Combine(ToolsDir, tool + ".zip");
                await DownloadAsync($"https://evermeet.cx/ffmpeg/getrelease/{tool}/zip", zip, "ffmpeg", ct);
                using (var archive = ZipFile.OpenRead(zip))
                    archive.Entries.First(e => e.Name == tool).ExtractToFile(Path.Combine(OwnFfmpegDir, tool), true);
                File.Delete(zip);
                MakeExecutable(Path.Combine(OwnFfmpegDir, tool));
            }
        }

        FfmpegReady = File.Exists(ownExe);
        FfmpegDir = FfmpegReady ? OwnFfmpegDir : null;
    }

    /// <summary>Del zip de FFmpeg-Builds solo hace falta bin/: ffmpeg, ffprobe y sus DLL (no ffplay).</summary>
    public static void ExtractFfmpeg(string zipPath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[^2] != "bin" || entry.Name.Length == 0) continue;
            if (entry.Name.StartsWith("ffplay", StringComparison.OrdinalIgnoreCase)) continue;
            entry.ExtractToFile(Path.Combine(targetDir, entry.Name), true);
        }
    }

    private async Task DownloadVerifiedAsync(string url, string sumsUrl, string assetName, string target, string stage, CancellationToken ct)
    {
        var sums = await _http.GetStringAsync(sumsUrl, ct);
        var expected = ParseChecksum(sums, assetName) ?? throw new InvalidOperationException($"No hay suma SHA-256 publicada para {assetName}");
        var tmp = target + ".part";
        await DownloadAsync(url, tmp, stage, ct);
        string actual;
        await using (var fs = File.OpenRead(tmp))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"La descarga de {assetName} no coincide con su suma SHA-256; se descartó");
        }
        File.Move(tmp, target, true);
    }

    /// <summary>Línea "hash  nombre" (formato de sha256sum) del archivo pedido.</summary>
    public static string? ParseChecksum(string sums, string assetName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == assetName && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private async Task DownloadAsync(string url, string target, string stage, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(target);
        var buffer = new byte[81920];
        long read = 0; int lastPercent = -1; int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is > 0)
            {
                var p = (int)(read * 100 / total.Value);
                if (p != lastPercent) { lastPercent = p; Report(stage, p); }
            }
        }
    }

    private string StampPath => Path.Combine(ToolsDir, ".yt-dlp-checked");
    private bool DueForUpdate() => !File.Exists(StampPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(StampPath) > UpdateEvery;
    private void Touch() { File.WriteAllText(StampPath, DateTime.UtcNow.ToString("O")); }

    private void MakeExecutable(string path)
    {
        if (Os == ToolOs.Windows || OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin" }))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await stdout, await stderr);
    }

    private void Report(string stage, int? percent) => SetStatus(Status with { Preparing = true, Stage = stage, Percent = percent, Error = null });

    private void SetStatus(ToolStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
