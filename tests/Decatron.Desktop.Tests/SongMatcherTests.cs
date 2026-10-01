using Decatron.Desktop.Modules.Downloads;
using Xunit;

namespace Decatron.Desktop.Tests;

public class SongMatcherTests
{
    [Fact]
    public void BuildQuery_uses_first_artist_and_drops_suffix()
    {
        Assert.Equal("Queen - Bohemian Rhapsody", SongMatcher.BuildQuery(new SongQuery("Bohemian Rhapsody - Remastered 2011", "Queen, David Bowie", 355)));
        Assert.Equal("Solo título", SongMatcher.BuildQuery(new SongQuery("Solo título", "", null)));
    }

    [Fact]
    public void Pick_prefers_official_audio_with_matching_duration_and_skips_covers()
    {
        var song = new SongQuery("Blinding Lights", "The Weeknd", 200);
        var candidates = new[]
        {
            new SongCandidate("aaaaaaaaaaa", "Blinding Lights (cover)", "Someone", null, 200),
            new SongCandidate("bbbbbbbbbbb", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO", null, 263),
            new SongCandidate("ccccccccccc", "Blinding Lights", "The Weeknd - Topic", "UC1", 201),
            new SongCandidate("ddddddddddd", "Blinding Lights live", "Fan", null, 200),
        };
        Assert.Equal("ccccccccccc", SongMatcher.Pick(candidates, song)!.Id);
    }

    [Fact]
    public void Pick_returns_null_when_no_duration_is_close()
    {
        var song = new SongQuery("Song", "Artist", 180);
        Assert.Null(SongMatcher.Pick(new[] { new SongCandidate("aaaaaaaaaaa", "Artist - Song", "Artist", null, 240) }, song));
    }

    [Fact]
    public void Live_is_allowed_when_the_song_itself_is_live()
    {
        var song = new SongQuery("Song (Live)", "Artist", 300);
        var pick = SongMatcher.Pick(new[] { new SongCandidate("aaaaaaaaaaa", "Artist - Song (Live)", "Artist", null, 302) }, song);
        Assert.NotNull(pick);
    }

    [Fact]
    public void ParseSearch_reads_flat_json_lines_and_ignores_noise()
    {
        var stdout = "WARNING: algo\n{\"id\":\"ccccccccccc\",\"title\":\"T\",\"channel\":\"A - Topic\",\"channel_id\":\"UC1\",\"duration\":201.0}\n{\"id\":\"no\"}\n";
        var list = SongMatcher.ParseSearch(stdout);
        Assert.Single(list);
        Assert.Equal(201, list[0].DurationSeconds);
        Assert.Equal("UC1", list[0].ChannelId);
    }

    [Fact]
    public void MusicCandidates_match_by_title_only()
    {
        var song = new SongQuery("Jolene", "Dolly Parton", 160);
        var results = new[] { new SongCandidate("aaaaaaaaaaa", "Jolene", "", null, null), new SongCandidate("bbbbbbbbbbb", "Otra", "", null, null) };
        Assert.Equal(new[] { "aaaaaaaaaaa" }, SongMatcher.MusicCandidates(results, song).Select(c => c.Id));
    }
}

public class YouTubePlaylistTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", true)]
    [InlineData("https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://evil.com/playlist?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", false)]
    [InlineData("http://www.youtube.com/playlist?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", false)]
    public void IsYouTubePlaylist(string url, bool expected) => Assert.Equal(expected, SongMatcher.IsYouTubePlaylist(url));

    [Fact]
    public void ParsePlaylist_skips_private_deleted_and_live()
    {
        var json = """
        {"title":"Mi lista","entries":[
          {"id":"aaaaaaaaaaa","title":"Buena","channel":"Artista - Topic","channel_id":"UC1","duration":200},
          {"id":"bbbbbbbbbbb","title":"[Private video]","duration":null},
          {"id":"ccccccccccc","title":"[Deleted video]"},
          {"id":"ddddddddddd","title":"En vivo","channel":"X"}
        ]}
        """;
        var (name, entries) = SongMatcher.ParsePlaylist(json);
        Assert.Equal("Mi lista", name);
        Assert.Single(entries);
        Assert.Equal("UC1", entries[0].ChannelId);
    }
}
