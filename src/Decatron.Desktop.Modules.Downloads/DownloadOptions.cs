using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Decatron.Desktop.Modules.Downloads;

/// <summary>
/// Lo que pidió el dashboard para una descarga. Llega del servidor, pero se valida acá de nuevo:
/// esto termina en los argumentos de un proceso que corre en la PC del streamer.
/// </summary>
public sealed record DownloadOptions(
    string Url,
    string Kind,               // video | audio
    string Format,             // video: mp4 | webm · audio: mp3 | m4a | opus | wav
    int? MaxHeight,            // video: 2160, 1440, 1080, 720, 480, 360… null = la mejor
    string AudioQuality,       // audio: best | 320 | 256 | 192 | 128
    double? TrimStart,         // segundos
    double? TrimEnd,
    bool Thumbnail,
    IReadOnlyList<string> Subtitles)
{
    public static readonly string[] VideoFormats = { "mp4", "webm" };
    public static readonly string[] AudioFormats = { "mp3", "m4a", "opus", "wav" };
    public static readonly string[] AudioQualities = { "best", "320", "256", "192", "128" };
    public static readonly int[] Heights = { 4320, 2160, 1440, 1080, 720, 480, 360, 240, 144 };
    private static readonly Regex LangRegex = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})?$", RegexOptions.Compiled);

    public bool IsAudio => Kind == "audio";
    public bool IsTrimmed => TrimStart != null || TrimEnd != null;

    /// <summary>null + motivo si algo no es válido.</summary>
    public static (DownloadOptions? Options, string? Error) Parse(JsonNode? node)
    {
        if (node is not JsonObject o) return (null, "invalid_request");
        var url = Str(o, "url");
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return (null, "invalid_url");

        var kind = Str(o, "kind") == "audio" ? "audio" : "video";
        var format = Str(o, "format") ?? (kind == "audio" ? "mp3" : "mp4");
        if (!(kind == "audio" ? AudioFormats : VideoFormats).Contains(format))
            return (null, "invalid_format");

        int? height = o["maxHeight"] is JsonValue hv && hv.TryGetValue<int>(out var h) ? h : null;
        if (height != null && !Heights.Contains(height.Value))
            return (null, "invalid_quality");

        var audioQuality = Str(o, "audioQuality") ?? "best";
        if (!AudioQualities.Contains(audioQuality))
            return (null, "invalid_quality");

        var start = Num(o, "trimStart");
        var end = Num(o, "trimEnd");
        if (start is < 0 || end is < 0 || (start != null && end != null && end <= start))
            return (null, "invalid_trim");

        var subs = o["subtitles"] is JsonArray arr
            ? arr.Select(x => x?.GetValue<string>()).Where(x => x != null && LangRegex.IsMatch(x)).Select(x => x!).Distinct().Take(5).ToList()
            : new List<string>();

        return (new DownloadOptions(uri.ToString(), kind, format, kind == "video" ? height : null, audioQuality,
            start, end, o["thumbnail"]?.GetValue<bool>() == true, kind == "video" ? subs : new List<string>()), null);
    }

    /// <summary>
    /// Argumentos de yt-dlp. La URL va después de "--" para que nunca se lea como una opción.
    /// El progreso y la ruta final salen en líneas con prefijo propio (DLP| y DLPATH|) para leerlos sin adivinar.
    /// </summary>
    public List<string> BuildArgs(string folder, string? ffmpegDir)
    {
        var args = new List<string>
        {
            "--no-playlist", "--newline", "--no-warnings", "--progress",
            "--progress-template", "download:DLP|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            // En modo silencioso yt-dlp no avisa cuando une/convierte: esta plantilla sí lo hace
            "--progress-template", "postprocess:DLPP|%(progress.postprocessor)s|%(progress.status)s",
            "--print", "after_move:DLPATH|%(filepath)s",
            "--windows-filenames", "--embed-metadata",
            "-P", folder,
            "-o", IsTrimmed ? "%(title).150B [%(id)s] (recorte).%(ext)s" : "%(title).150B [%(id)s].%(ext)s",
        };
        if (!string.IsNullOrEmpty(ffmpegDir))
            args.AddRange(new[] { "--ffmpeg-location", ffmpegDir });

        if (IsAudio)
        {
            args.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", Format,
                "--audio-quality", AudioQuality == "best" ? "0" : AudioQuality + "K" });
        }
        else
        {
            var res = MaxHeight is { } h ? $"res:{h}," : "";
            var ext = Format == "mp4" ? "ext:mp4:m4a" : "ext:webm:webm";
            args.AddRange(new[] { "-f", "bv*+ba/b", "-S", res + ext, "--merge-output-format", Format });
            if (Subtitles.Count > 0)
                args.AddRange(new[] { "--write-subs", "--sub-langs", string.Join(",", Subtitles), "--embed-subs" });
        }

        if (Thumbnail)
        {
            // Siempre queda la imagen aparte; en los formatos que lo aceptan, además va adentro del archivo
            args.AddRange(new[] { "--write-thumbnail", "--convert-thumbnails", "jpg" });
            if (Format is "mp4" or "mp3" or "m4a")
                args.Add("--embed-thumbnail");
        }

        if (IsTrimmed)
        {
            var from = TrimStart is { } s ? s.ToString("0.###", CultureInfo.InvariantCulture) : "0";
            var to = TrimEnd is { } e ? e.ToString("0.###", CultureInfo.InvariantCulture) : "inf";
            args.AddRange(new[] { "--download-sections", $"*{from}-{to}" });
            if (!IsAudio) args.Add("--force-keyframes-at-cuts");
        }

        args.Add("--");
        args.Add(Url);
        return args;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
