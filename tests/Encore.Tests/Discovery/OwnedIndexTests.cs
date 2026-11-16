using System.Net;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Tests.Jellyfin;
using Encore.Tests.Watch;
using static Encore.Tests.Discovery.Songs;

namespace Encore.Tests.Discovery;

public sealed class OwnedIndexTests
{
    private static (OwnedIndex Index, ListLogger<OwnedIndex> Log) Build(IJellyfinClient jellyfin, TimeProvider? time = null, int pageSize = OwnedIndex.PageSize)
    {
        var log = new ListLogger<OwnedIndex>();
        return (new OwnedIndex(jellyfin, RadioDir, log, time, pageSize), log);
    }

    private static TrackKey K(string artist, string title) => TrackKey.Create(artist, title)!.Value;

    private static (JellyfinClient Client, ScriptedHandler Script) Client()
    {
        var script = new ScriptedHandler();
        var http = new HttpClient(script) { BaseAddress = new Uri("http://jellyfin.test:8096/"), Timeout = TimeSpan.FromSeconds(10) };
        return (new JellyfinClient(http, new JellyfinClientOptions { RetryDelays = [], Jitter = 0 }), script);
    }

    private static string AudioJson(int id, string artist, string title, string path) =>
        $$"""{"Id":"{{Id(id):N}}","Name":"{{title}}","Type":"Audio","RunTimeTicks":2000000000,"Path":"{{path}}","DateCreated":"2026-11-01T10:00:00.0000000Z","ArtistItems":[{"Id":"{{Id(500 + id):N}}","Name":"{{artist}}"}]}""";

    [Fact]
    public async Task OI01_loads_two_full_pages_and_a_partial_one()
    {
        var (client, script) = Client();
        string Page(params int[] ids) =>
            "{\"Items\":[" + string.Join(",", ids.Select(i => AudioJson(i, "Artist " + i, "Song " + i, "/music/a/" + i + ".flac"))) + "],\"TotalRecordCount\":5}";
        script.Reply(HttpStatusCode.OK, Page(1, 2)).Reply(HttpStatusCode.OK, Page(3, 4)).Reply(HttpStatusCode.OK, Page(5));
        var (index, log) = Build(client, pageSize: 2);

        Assert.True(await index.RefreshAsync(CancellationToken.None));

        Assert.Equal(5, index.Count);
        Assert.Equal(3, script.Sent.Count);
        Assert.All(script.Sent, s => Assert.Contains("fields=Path,DateCreated", s.PathAndQuery, StringComparison.Ordinal));
        Assert.Equal(["startIndex=0", "startIndex=2", "startIndex=4"], script.Sent.Select(s => s.PathAndQuery.Split('&').Single(p => p.StartsWith("startIndex=", StringComparison.Ordinal))));
        var loaded = Assert.Single(log.Named("owned_index.loaded"));
        Assert.Equal(5, loaded.Fields["Items"]);
    }

    [Fact]
    public async Task OI02_OI04_an_owned_candidate_resolves_to_its_item_id_and_an_unowned_one_to_null()
    {
        var lib = new FakeLibrary { Audio = [Audio(1, "Radiohead", "Reckoner", 290, artistId: 50)] };
        var (index, _) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);

        Assert.Equal(Id(1), index.Resolve(K("RADIOHEAD", "Reckoner (2007 Remaster)"), 290)!.ItemId);
        Assert.Null(index.Resolve(K("Radiohead", "Nude"), 255));
        Assert.Null(index.Resolve(default, 255));
    }

    [Fact]
    public async Task OI03_a_music_radio_song_counts_as_owned()
    {
        var lib = new FakeLibrary
        {
            Audio = [Audio(7, "Phoebe Bridgers", "Motion Sickness", 230, path: "/music-radio/soulseek/Phoebe Bridgers/Motion Sickness.flac", artistId: 51)],
        };
        var (index, log) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);

        var entry = index.Resolve(K("Phoebe Bridgers", "Motion Sickness"), 230);
        Assert.NotNull(entry);
        Assert.Equal(Id(7), entry.ItemId);
        Assert.True(entry.InRadioDir);
        Assert.Equal(1, Assert.Single(log.Named("owned_index.loaded")).Fields["RadioItems"]);

        // A folder that only starts with the same letters is not music-radio.
        Assert.False(OwnedIndex.CreateEntry(Audio(8, "A", "B", path: "/music-radio2/x.flac"), RadioDir)!.InRadioDir);
    }

    [Fact]
    public async Task OI05_same_key_twice_closest_duration_then_main_library_then_newest()
    {
        var old = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lib = new FakeLibrary
        {
            Audio =
            [
                Audio(1, "Smashing Pumpkins", "Luna", 200, created: old, artistId: 52),
                Audio(2, "The Smashing Pumpkins", "Luna (2011 Remaster)", 210, path: "/music-radio/soulseek/x.flac", created: old, artistId: 52),
                Audio(3, "Smashing Pumpkins", "Luna", 210, created: old.AddDays(1), artistId: 52),
                Audio(4, "Smashing Pumpkins", "Luna", 210, created: old, artistId: 52),
            ],
        };
        var (index, _) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);
        var key = K("Smashing Pumpkins", "Luna");

        Assert.Equal(Id(1), index.Resolve(key, 201)!.ItemId);   // closest duration
        Assert.Equal(Id(3), index.Resolve(key, 210)!.ItemId);   // 2, 3 and 4 tie: main library, then newest
        Assert.Equal(Id(3), index.Resolve(key, null)!.ItemId);  // no duration: main library, then newest
    }

    [Fact]
    public async Task OI06_Add_after_an_ingest_resolves_at_once()
    {
        var lib = new FakeLibrary();
        var (index, _) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);
        var entry = OwnedIndex.CreateEntry(Audio(9, "Mitski", "Nobody", 190, path: "/music-radio/soulseek/m.flac"), RadioDir)!;

        index.Add(entry);

        Assert.Equal(Id(9), index.Resolve(K("Mitski", "Nobody"), 190)!.ItemId);
        Assert.Equal(K("Mitski", "Nobody"), index.KeyOf(Id(9)));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public async Task OI07_refresh_every_30_min_replaces_the_index_and_a_failed_refresh_keeps_it()
    {
        var clock = new ManualClock();
        var lib = new FakeLibrary { Audio = [Audio(1, "A", "One")] };
        var (index, log) = Build(lib, clock);
        Assert.False(index.IsLoaded);
        Assert.Null(index.RefreshedUtc);

        using var cts = new CancellationTokenSource();
        var run = index.RunAsync(cts.Token);
        await Wait.Until(() => index.IsLoaded, "first load");
        Assert.Equal(clock.GetUtcNow(), index.RefreshedUtc);
        Assert.NotNull(index.Resolve(K("A", "One"), null));

        lib.Audio = [Audio(2, "B", "Two")];
        await Wait.Until(() => clock.PendingTimers > 0, "the refresh wait");
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Single(lib.PageCalls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Wait.Until(() => index.Resolve(K("B", "Two"), null) is not null, "the second load");
        Assert.Null(index.Resolve(K("A", "One"), null));

        lib.Failure = new JellyfinRequestException("GetAudioPage", 500);
        await Wait.Until(() => clock.PendingTimers > 0, "the refresh wait");
        clock.Advance(TimeSpan.FromMinutes(30));
        await Wait.Until(() => log.Named("owned_index.failed").Any(), "the failure log");
        Assert.True(index.IsLoaded);
        Assert.NotNull(index.Resolve(K("B", "Two"), null));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task OI07_a_failed_first_load_is_tried_again_after_a_minute_not_30()
    {
        var clock = new ManualClock();
        var lib = new FakeLibrary { Audio = [Audio(1, "A", "One")], Failure = new JellyfinRequestException("GetAudioPage", 503) };
        var (index, log) = Build(lib, clock);

        using var cts = new CancellationTokenSource();
        var run = index.RunAsync(cts.Token);
        await Wait.Until(() => log.Named("owned_index.failed").Any() && clock.PendingTimers > 0, "the failed first load");
        Assert.False(index.IsLoaded);

        lib.Failure = null;
        clock.Advance(OwnedIndex.RetryUntilLoaded);
        await Wait.Until(() => index.IsLoaded, "the retry");
        Assert.NotNull(index.Resolve(K("A", "One"), null));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task OI07_a_first_load_that_fails_leaves_the_index_not_loaded()
    {
        var lib = new FakeLibrary { Failure = new JellyfinRequestException("GetAudioPage", 503) };
        var (index, log) = Build(lib);

        Assert.False(await index.RefreshAsync(CancellationToken.None));

        Assert.False(index.IsLoaded);
        Assert.Single(log.Named("owned_index.failed"));
    }

    [Fact]
    public async Task OI08_KeyOf_returns_the_key_of_an_item()
    {
        var lib = new FakeLibrary { Audio = [Audio(3, "Beyoncé", "Halo (feat. Nobody)")] };
        var (index, _) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);

        Assert.Equal("beyonce|halo", index.KeyOf(Id(3))!.Value.Value);
        Assert.Null(index.KeyOf(Id(4)));
    }

    [Fact]
    public async Task Items_without_a_key_are_left_out()
    {
        var noArtist = Audio(1, "x", "Song") with { ArtistItems = [], AlbumArtists = [] };
        var lib = new FakeLibrary { Audio = [noArtist, Audio(2, "A", "?!"), Audio(3, "A", "Fine")] };
        var (index, log) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, index.Count);
        Assert.Equal(3, Assert.Single(log.Named("owned_index.loaded")).Fields["Items"]);
    }

    [Fact]
    public async Task OI09_audio_page_and_items_by_ids_queries_are_exact_and_parse()
    {
        var (client, script) = Client();
        script.Reply(HttpStatusCode.OK, "{\"Items\":[" + AudioJson(1, "Radiohead", "Reckoner", "/music-radio/soulseek/r.flac") + "],\"TotalRecordCount\":1}");
        script.Reply(HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"" + Id(70).ToString("N") + "\",\"Name\":\"Radiohead\",\"Type\":\"MusicArtist\",\"ProviderIds\":{\"MusicBrainzArtist\":\"a74b1b7f-71a5-4011-9441-d0b5e4122711\"}}],\"TotalRecordCount\":1}");

        var page = await client.GetAudioPageAsync(500, 500);
        var artists = await client.GetItemsByIdsAsync([Id(70), Id(71)], "ProviderIds");

        Assert.Equal("/Items?recursive=true&includeItemTypes=Audio&fields=Path,DateCreated&startIndex=500&limit=500&enableTotalRecordCount=true", script.Sent[0].PathAndQuery);
        Assert.Equal($"/Items?ids={Id(70):N},{Id(71):N}&fields=ProviderIds", script.Sent[1].PathAndQuery);
        var song = Assert.Single(page.Items);
        Assert.Equal("/music-radio/soulseek/r.flac", song.Path);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), song.DateCreated);
        Assert.Equal("a74b1b7f-71a5-4011-9441-d0b5e4122711", Assert.Single(artists).ProviderIds!["MusicBrainzArtist"]);
    }

    [Fact]
    public async Task OI20_a_removed_item_is_not_brought_back_by_a_refresh_that_still_reads_it_from_Jellyfin()
    {
        // Review v0.4 issue 7: Jellyfin lists the old item until its rescan drops it, so a reload after the removal (or one queued
        // before it) must not put it back.
        var lib = new FakeLibrary { Audio = [Audio(1, "A", "One"), Audio(2, "B", "Two")] };
        var (index, _) = Build(lib);
        await index.RefreshAsync(CancellationToken.None);

        index.Remove(Id(1));
        Assert.True(await index.RefreshAsync(CancellationToken.None));

        Assert.Null(index.KeyOf(Id(1)));
        Assert.Null(index.Resolve(K("A", "One"), null));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public async Task OI21_a_removed_item_comes_back_only_when_added_again_or_after_the_hold()
    {
        var clock = new ManualClock();
        var lib = new FakeLibrary { Audio = [Audio(1, "A", "One")] };
        var (index, _) = Build(lib, clock);
        await index.RefreshAsync(CancellationToken.None);
        index.Remove(Id(1));

        clock.Advance(OwnedIndex.RemovedHold + TimeSpan.FromMinutes(1));
        await index.RefreshAsync(CancellationToken.None);

        Assert.NotNull(index.KeyOf(Id(1))); // Jellyfin still lists it an hour later: it is real after all
    }
}
