using Encore.Discovery;

namespace Encore.Tests.Discovery;

public sealed class TrackKeyTests
{
    private static string Key(string artist, string title) => TrackKey.Create(artist, title)!.Value.Value;

    private static string Title(string title) => KeyText.TitleKey(title);

    [Fact]
    public void TK01_accents_and_case_are_folded()
    {
        Assert.Equal("beyonce", KeyText.ArtistKey("Beyoncé"));
        Assert.Equal(Key("beyonce", "HALO"), Key("BEYONCÉ", "Halo"));
        Assert.Equal("beyonce|halo", Key("Beyoncé", "Halo"));
    }

    [Theory]
    [InlineData("The Trooper (2015 Remaster) (Remastered 2015)", "The Trooper")]
    [InlineData("Song - 2012 Remaster", "Song")]
    [InlineData("Song (Deluxe Edition)", "Song")]
    [InlineData("Song (Live) (2015 Remaster)", "Song (Live)")]
    [InlineData("Song (feat. X) - Remastered 2009", "Song (feat. X)")]
    [InlineData("Song - Live - 2012 Remaster", "Song - Live")]
    [InlineData("Song (Remix)", "Song (Remix)")]
    [InlineData("(Remastered)", "(Remastered)")]
    public void TK02b_version_notes_are_removed_but_live_and_remix_stay(string title, string expected)
    {
        Assert.Equal(expected, KeyText.WithoutVersionNotes(title));
    }

    [Fact]
    public void TK02_feat_credits_are_dropped()
    {
        Assert.Equal("song", Title("Song (feat. X)"));
        Assert.Equal("song", Title("Song [ft. X]"));
        Assert.Equal("song", Title("Song feat. X"));
        Assert.Equal("song", Title("Song (with X)"));
        Assert.Equal("a", KeyText.ArtistKey("A feat. B"));
        Assert.Equal(Key("A", "Song"), Key("A feat. B", "Song (feat. B)"));
    }

    [Theory]
    [InlineData("Luna (2011 Remaster)")]
    [InlineData("Luna - 2011 Remaster")]
    [InlineData("Luna - Remastered 2009")]
    [InlineData("Luna (Mono Version)")]
    [InlineData("Luna [Deluxe Edition]")]
    [InlineData("Luna (Original Mix)")]
    public void TK03_version_only_brackets_and_tails_are_dropped(string title) =>
        Assert.Equal("luna", Title(title));

    [Theory]
    [InlineData("Song (Live)")]
    [InlineData("Song (Acoustic)")]
    [InlineData("Song (Remix)")]
    [InlineData("Song - Demo")]
    [InlineData("Song (Instrumental)")]
    [InlineData("Song - Live at Wembley")]
    public void TK04_live_acoustic_remix_and_demo_stay_distinct(string title) =>
        Assert.NotEqual(Title("Song"), Title(title));

    [Fact]
    public void TK05_ampersand_and_apostrophes()
    {
        Assert.Equal(KeyText.ArtistKey("Simon and Garfunkel"), KeyText.ArtistKey("Simon & Garfunkel"));
        Assert.Equal(Title("Dont Stop"), Title("Don't Stop"));
        Assert.Equal(Title("Dont Stop"), Title("Don’t Stop"));
        Assert.Equal("simon and garfunkel", KeyText.ArtistKey("Simon + Garfunkel"));
    }

    [Fact]
    public void TK06_a_leading_the_is_dropped_from_the_artist()
    {
        Assert.Equal(KeyText.ArtistKey("Smashing Pumpkins"), KeyText.ArtistKey("The Smashing Pumpkins"));
        Assert.Equal("the", KeyText.ArtistKey("The"));
        Assert.Equal("the song", Title("The Song"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    [InlineData("-- ...")]
    public void TK07_empty_or_punctuation_only_title_gives_no_key(string title)
    {
        Assert.False(TrackKey.TryCreate("Artist", title, out _));
        Assert.False(TrackKey.TryCreate(title, "Song", out _));
        Assert.Null(TrackKey.Create(null, "Song"));
    }

    [Fact]
    public void TK08_non_latin_letters_are_kept()
    {
        var key = TrackKey.Create("YOASOBI", "夜に駆ける")!.Value;
        Assert.Equal("夜に駆ける", key.TitleKey);
        Assert.Equal("yoasobi", key.ArtistKey);

        // Kana voicing marks survive (が is not folded to か), so different words stay different.
        Assert.NotEqual(Title("が"), Title("か"));
        Assert.Equal("ほしがき", Title("ほしがき"));
    }

    [Fact]
    public void Keys_compare_ordinally_and_parse_back()
    {
        var a = TrackKey.Create("The Smashing Pumpkins", "Luna (2011 Remaster)")!.Value;
        var b = TrackKey.Create("smashing pumpkins", "LUNA")!.Value;
        Assert.Equal(a, b);
        Assert.Equal("smashing pumpkins|luna", a.ToString());
        Assert.True(TrackKey.TryParse(a.Value, out var parsed));
        Assert.Equal(a, parsed);
        Assert.False(TrackKey.TryParse("nobar", out _));
        Assert.False(TrackKey.TryParse("a|b|c", out _));
        Assert.False(TrackKey.TryParse("|b", out _));
    }
}
