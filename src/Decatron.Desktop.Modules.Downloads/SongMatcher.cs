using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Decatron.Desktop.Modules.Downloads;

/// <summary>Una canción de Spotify/Deezer/Apple Music que hay que encontrar en YouTube.</summary>
public sealed record SongQuery(string Title, string Artist, int? DurationSeconds);

/// <summary>Un resultado de búsqueda de YouTube (búsqueda plana de yt-dlp).</summary>
public sealed record SongCandidate(string Id, string Title, string Channel, string? ChannelId, int? DurationSeconds);

/// <summary>
/// Elige en YouTube la versión de una canción de otro servicio. Es el mismo criterio que usa el servidor
/// (Decatron: YouTubeTrackSource): primero YouTube Music (audio oficial) si calza la duración; si no, la
/// búsqueda normal y "… audio", descartando covers, en vivo, remixes, letras, etc., y prefiriendo lo más
/// oficial (canal "Topic" &gt; canal del artista &gt; título "official") con la duración más parecida.
/// </summary>
public static class SongMatcher
{
    public const int TightTolerance = 3;
    public const int Tolerance = 8;

    private static readonly Regex VideoIdRegex = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private static readonly string[] AlternateVersionWords =
    {
        "cover", "karaoke", "live", "en vivo", "8d", "slowed", "sped up", "nightcore",
        "reverb", "remix", "instrumental", "tutorial", "reaction", "letra", "lyrics",
        "subtitulado", "subtitulada", "sub español", "traducida", "traducción", "traduccion", "lyric video"
    };

    /// <summary>
    /// "Artista - Título" para buscar: solo el primer artista y sin lo que va después de " - "
    /// ("Remastered 2011", "Radio Edit"…), que YouTube no usa.
    /// </summary>
    public static string BuildQuery(SongQuery song)
    {
        var title = song.Title.Trim();
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) title = title[..dash];
        var artist = song.Artist.Split(',', '&')[0].Trim();
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        return query.Length <= 200 ? query : query[..200];
    }

    /// <summary>Las líneas de <c>yt-dlp --dump-json --flat-playlist</c>: una por resultado.</summary>
    public static List<SongCandidate> ParseSearch(string stdout)
    {
        var list = new List<SongCandidate>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var id = Str(r, "id");
                if (id == null || !VideoIdRegex.IsMatch(id)) continue;
                list.Add(new SongCandidate(id, Str(r, "title") ?? "", Str(r, "channel") ?? Str(r, "uploader") ?? "", Str(r, "channel_id"),
                    r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)Math.Round(d.GetDouble()) : null));
            }
            catch (JsonException) { /* aviso de yt-dlp, no es un resultado */ }
        }
        return list;
    }

    private static readonly Regex PlaylistIdRegex = new(@"[?&]list=([A-Za-z0-9_-]{10,64})", RegexOptions.Compiled);

    /// <summary>Un link de playlist de YouTube o YouTube Music (con list=).</summary>
    public static bool IsYouTubePlaylist(string? url)
    {
        if (url == null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        var host = uri.Host.ToLowerInvariant();
        return (host is "www.youtube.com" or "youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be")
               && PlaylistIdRegex.IsMatch(uri.Query);
    }

    /// <summary>
    /// La salida de <c>yt-dlp --flat-playlist --dump-single-json</c>: nombre de la playlist y sus videos.
    /// Se descartan los que no sirven para sonar: privados, borrados y sin duración (en vivo o estreno).
    /// </summary>
    public static (string? Name, List<SongCandidate> Entries) ParsePlaylist(string json)
    {
        var list = new List<SongCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entries.EnumerateArray())
                {
                    var id = Str(e, "id");
                    var title = Str(e, "title") ?? "";
                    if (id == null || !VideoIdRegex.IsMatch(id) || title is "[Private video]" or "[Deleted video]") continue;
                    int? duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)Math.Round(d.GetDouble()) : null;
                    if (duration is not > 0) continue;
                    list.Add(new SongCandidate(id, title, Str(e, "channel") ?? Str(e, "uploader") ?? "", Str(e, "channel_id"), duration));
                }
            }
            return (Str(r, "title"), list);
        }
        catch (JsonException) { return (null, list); }
    }

    /// <summary>Los primeros resultados de YouTube Music cuyo título coincide con el pedido (a verificar por duración).</summary>
    public static IEnumerable<SongCandidate> MusicCandidates(IEnumerable<SongCandidate> results, SongQuery song)
    {
        var query = BuildQuery(song);
        var dash = query.IndexOf(" - ", StringComparison.Ordinal);
        var wanted = Normalize(dash > 0 ? query[(dash + 3)..] : query);
        if (wanted.Length == 0) yield break;
        foreach (var c in results.Take(3))
        {
            var t = Normalize(c.Title);
            if (t.Length > 0 && (t == wanted || t.StartsWith(wanted) || wanted.StartsWith(t)))
                yield return c;
        }
    }

    public static bool DurationMatches(int? candidate, int? expected) =>
        expected is not > 0 || (candidate is > 0 && Math.Abs(candidate.Value - expected.Value) <= Tolerance);

    /// <summary>El mejor resultado de la búsqueda normal, o null si ninguno sirve.</summary>
    public static SongCandidate? Pick(IEnumerable<SongCandidate> candidates, SongQuery song)
    {
        var query = BuildQuery(song);
        // Sin duración en la búsqueda plana = en vivo o estreno: nunca sirve
        var playable = candidates.Where(c => c.DurationSeconds is > 0).GroupBy(c => c.Id).Select(g => g.First()).ToList();
        if (song.DurationSeconds is not > 0)
            return playable.FirstOrDefault();

        var expected = song.DurationSeconds.Value;
        var queryLower = query.ToLowerInvariant();
        var eligible = playable.Where(c => !IsAlternateVersion(c.Title, queryLower)).ToList();
        var artist = Normalize(query.Split(" - ")[0]);
        int Diff(SongCandidate c) => Math.Abs(c.DurationSeconds!.Value - expected);

        int Rank(SongCandidate c)
        {
            if (c.Channel.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) return 0;
            var channel = Normalize(c.Channel.Replace("VEVO", "", StringComparison.OrdinalIgnoreCase).Replace("Official", "", StringComparison.OrdinalIgnoreCase));
            if (artist.Length > 0 && channel.Length > 0 && (channel.Contains(artist) || artist.Contains(channel))) return 1;
            var title = c.Title.ToLowerInvariant();
            if (title.Contains("official") || title.Contains("oficial")) return 2;
            return 3;
        }

        return eligible.Where(c => Diff(c) <= TightTolerance).OrderBy(Rank).ThenBy(Diff).FirstOrDefault()
               ?? eligible.Where(c => Diff(c) <= Tolerance).OrderBy(Rank).ThenBy(Diff).FirstOrDefault();
    }

    /// <summary>Minúsculas, sin tildes ni signos: "Beyoncé" y "beyonce" son lo mismo.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static bool IsAlternateVersion(string title, string queryLower)
    {
        var titleLower = title.ToLowerInvariant();
        return AlternateVersionWords.Any(w => ContainsWord(titleLower, w) && !ContainsWord(queryLower, w));
    }

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(word)}($|[^\p{{L}}\p{{N}}])");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
