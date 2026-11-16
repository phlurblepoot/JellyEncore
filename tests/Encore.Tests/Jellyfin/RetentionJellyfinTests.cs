using System.Net;
using Encore.Jellyfin;

namespace Encore.Tests.Jellyfin;

public sealed class RetentionJellyfinTests
{
    private const string Key = "SECRETKEY0123456789abcdef";
    private const string DeviceId = "encore-0123456789ab";
    private static readonly Guid User = Guid.Parse("4f1c2d3e-5a6b-4c7d-8e9f-0a1b2c3d4e5f");
    private static readonly Guid Song = Guid.Parse("9d8c7b6a-5f4e-3d2c-1b0a-998877665544");
    private static readonly Guid Song2 = Guid.Parse("11223344-5566-7788-990a-abbccddeeff0");
    private static readonly Guid Playlist = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
    private static readonly Guid Library = Guid.Parse("0a0b0c0d-0e0f-1011-1213-141516171819");

    private static string N(Guid id) => id.ToString("N");

    private static (JellyfinClient Client, ScriptedHandler Script) Build()
    {
        var script = new ScriptedHandler();
        var auth = new JellyfinAuthHandler(JellyfinAuth.BuildParameter(Key, DeviceId, "0.4.0")) { InnerHandler = script };
        var http = new HttpClient(auth) { BaseAddress = new Uri("http://jellyfin.test:8096/jf/") };
        return (new JellyfinClient(http, new JellyfinClientOptions { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], Jitter = 0 }), script);
    }

    private static string Items(int total, params Guid[] ids) =>
        "{\"Items\":[" + string.Join(",", ids.Select(i => $"{{\"Id\":\"{N(i)}\",\"Name\":\"n\",\"Path\":\"/m/{N(i)}.flac\"}}")) + $"],\"TotalRecordCount\":{total}}}";

    private static string Items(params Guid[] ids) => Items(ids.Length, ids);

    [Fact]
    public async Task Favorites_use_the_exact_route_with_userId_and_parent()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, Items(Song, Song2));

        var favorites = await client.GetFavoriteAudioAsync(User, Library);

        Assert.Equal([Song, Song2], favorites.Select(f => f.Id));
        var sent = Assert.Single(script.Sent);
        Assert.Equal("GET", sent.Method);
        Assert.Equal(
            $"/jf/Items?userId={N(User)}&isFavorite=true&includeItemTypes=Audio&recursive=true&parentId={N(Library)}&fields=Path&enableUserData=true&startIndex=0&limit=500&enableTotalRecordCount=true",
            sent.PathAndQuery);
    }

    [Fact]
    public async Task Favorites_are_paged_by_500()
    {
        var (client, script) = Build();
        var first = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray();
        script.Reply(HttpStatusCode.OK, Items(501, first)).Reply(HttpStatusCode.OK, Items(501, Song));

        var favorites = await client.GetFavoriteAudioAsync(User);

        Assert.Equal(501, favorites.Count);
        Assert.Equal(2, script.Sent.Count);
        Assert.Contains("startIndex=500", script.Sent[1].PathAndQuery);
        Assert.All(script.Sent, s => Assert.Contains($"userId={N(User)}", s.PathAndQuery));
        Assert.All(script.Sent, s => Assert.DoesNotContain("parentId", s.PathAndQuery));
    }

    [Fact]
    public async Task Playlists_and_entries_routes()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"" + N(Playlist) + "\",\"Name\":\"Mix\"}],\"TotalRecordCount\":1}")
            .Reply(HttpStatusCode.OK, Items(Song2, Song, Song2));

        var playlists = await client.GetPlaylistsAsync(User);
        var entries = await client.GetPlaylistEntriesAsync(Playlist, User);

        Assert.Equal(new PlaylistRef(Playlist, "Mix"), Assert.Single(playlists));
        Assert.Equal([Song2, Song, Song2], entries);
        Assert.Equal($"/jf/Items?userId={N(User)}&includeItemTypes=Playlist&recursive=true", script.Sent[0].PathAndQuery);
        Assert.Equal($"/jf/Playlists/{N(Playlist)}/Items?userId={N(User)}", script.Sent[1].PathAndQuery);
    }

    [Fact]
    public async Task Add_sends_position_and_userId_and_maps_204_403_404_to_results()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.NoContent).Reply(HttpStatusCode.Forbidden).Reply(HttpStatusCode.NotFound);

        Assert.Equal(PlaylistWriteResult.Ok, await client.AddToPlaylistAsync(Playlist, Song, User, 3));
        Assert.Equal(PlaylistWriteResult.Forbidden, await client.AddToPlaylistAsync(Playlist, Song, User, 0));
        Assert.Equal(PlaylistWriteResult.NotFound, await client.AddToPlaylistAsync(Playlist, Song, User, 0));

        Assert.All(script.Sent, s => Assert.Equal("POST", s.Method));
        Assert.Equal($"/jf/Playlists/{N(Playlist)}/Items?ids={N(Song)}&userId={N(User)}&position=3", script.Sent[0].PathAndQuery);
        Assert.Equal(3, script.Sent.Count);
    }

    [Fact]
    public async Task Add_is_not_retried_on_a_server_error_and_throws()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<JellyfinRequestException>(() => client.AddToPlaylistAsync(Playlist, Song, User, 0));

        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task Add_with_a_rejected_key_401_still_throws()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<JellyfinAuthException>(() => client.AddToPlaylistAsync(Playlist, Song, User, 0));
    }

    [Fact]
    public async Task Remove_sends_entryIds_and_no_userId()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.NoContent).Reply(HttpStatusCode.NotFound);

        Assert.Equal(PlaylistWriteResult.Ok, await client.RemovePlaylistEntriesAsync(Playlist, [Song, Song2]));
        Assert.Equal(PlaylistWriteResult.NotFound, await client.RemovePlaylistEntriesAsync(Playlist, [Song]));

        Assert.Equal("DELETE", script.Sent[0].Method);
        Assert.Equal($"/jf/Playlists/{N(Playlist)}/Items?entryIds={N(Song)},{N(Song2)}", script.Sent[0].PathAndQuery);
        Assert.DoesNotContain("userId", script.Sent[0].PathAndQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkFavorite_posts_with_userId()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "{\"IsFavorite\":true}");

        await client.MarkFavoriteAsync(Song, User);

        Assert.Equal("POST", script.Sent[0].Method);
        Assert.Equal($"/jf/UserFavoriteItems/{N(Song)}?userId={N(User)}", script.Sent[0].PathAndQuery);
    }

    [Fact]
    public async Task UserItemData_reads_favorite_and_play_count_or_null()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"" + N(Song) + "\",\"UserData\":{\"IsFavorite\":true,\"PlayCount\":4}}],\"TotalRecordCount\":1}")
            .Reply(HttpStatusCode.OK, "{\"Items\":[],\"TotalRecordCount\":0}");

        Assert.Equal(new UserItemData(true, 4), await client.GetUserItemDataAsync(Song, User));
        Assert.Null(await client.GetUserItemDataAsync(Song, User));

        Assert.Equal($"/jf/Items?ids={N(Song)}&userId={N(User)}&enableUserData=true", script.Sent[0].PathAndQuery);
    }

    [Fact]
    public async Task ItemExists_is_false_for_empty_items_and_sends_no_userId()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "{\"Items\":[],\"TotalRecordCount\":0}").Reply(HttpStatusCode.OK, Items(Song));

        Assert.False(await client.ItemExistsAsync(Song));
        Assert.True(await client.ItemExistsAsync(Song));

        Assert.Equal($"/jf/Items?ids={N(Song)}", script.Sent[0].PathAndQuery);
    }

    [Fact]
    public async Task SmartLists_not_installed_makes_no_route_call()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "[{\"Name\":\"Playback Reporting\"},{\"Name\":\"AudioDB\"}]");

        var info = await client.GetSmartListsAsync();

        Assert.False(info.Installed);
        Assert.True(info.ReadOk);
        Assert.Empty(info.PlaylistIds);
        Assert.Equal("/jf/Plugins", Assert.Single(script.Sent).PathAndQuery);
    }

    [Fact]
    public async Task SmartLists_installed_reads_ids_from_both_fields()
    {
        var (client, script) = Build();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        script.Reply(HttpStatusCode.OK, "[{\"Name\":\"smartlists\",\"Version\":\"12.2.1.0\"}]")
            .Reply(HttpStatusCode.OK,
                $"[{{\"Name\":\"x\",\"Type\":\"Playlist\",\"JellyfinPlaylistId\":\"{N(a)}\"}}," +
                $"{{\"Name\":\"y\",\"UserPlaylists\":[{{\"UserId\":\"u\",\"JellyfinPlaylistId\":\"{N(b)}\"}},{{\"UserId\":\"v\",\"JellyfinPlaylistId\":\"{N(c)}\"}},{{\"UserId\":\"w\"}}]}}," +
                "{\"Name\":\"z\"}]");

        var info = await client.GetSmartListsAsync();

        Assert.True(info.Installed);
        Assert.True(info.ReadOk);
        Assert.Equal(new HashSet<Guid> { a, b, c }, info.PlaylistIds);
        Assert.Equal("/jf/Plugins", script.Sent[0].PathAndQuery);
        Assert.Equal("/jf/Plugins/SmartLists?type=Playlist", script.Sent[1].PathAndQuery);
    }

    [Theory]
    [InlineData(404, null)]
    [InlineData(500, null)]
    [InlineData(403, null)]
    [InlineData(200, "not json")]
    [InlineData(200, "{\"a\":1}")]
    [InlineData(200, "[{\"JellyfinPlaylistId\":\"nope\"}]")]
    public async Task SmartLists_installed_but_route_unreadable_is_a_failed_read(int status, string? body)
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "[{\"Name\":\"SmartLists\"}]");
        // A 500 is retried twice before it counts as failed.
        var replies = status == 500 ? 3 : 1;
        for (var i = 0; i < replies; i++)
        {
            script.Reply((HttpStatusCode)status, body);
        }

        var info = await client.GetSmartListsAsync();

        Assert.True(info.Installed);
        Assert.False(info.ReadOk);
        Assert.Empty(info.PlaylistIds);
    }

    [Fact]
    public async Task SmartLists_connection_failure_on_the_route_is_a_failed_read_but_a_plugins_failure_throws()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "[{\"Name\":\"SmartLists\"}]").Fail(new HttpRequestException(HttpRequestError.ConnectionError, "x"));
        Assert.False((await client.GetSmartListsAsync()).ReadOk);

        var (client2, script2) = Build();
        script2.Reply(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<JellyfinRequestException>(() => client2.GetSmartListsAsync());
    }

    [Fact]
    public async Task Every_retention_call_carries_the_exact_header_and_no_api_key_query()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, Items(Song))
            .Reply(HttpStatusCode.OK, Items(Playlist))
            .Reply(HttpStatusCode.OK, Items(Song))
            .Reply(HttpStatusCode.NoContent)
            .Reply(HttpStatusCode.NoContent)
            .Reply(HttpStatusCode.NoContent)
            .Reply(HttpStatusCode.OK, Items(Song))
            .Reply(HttpStatusCode.OK, Items(Song))
            .Reply(HttpStatusCode.OK, "[{\"Name\":\"SmartLists\"}]")
            .Reply(HttpStatusCode.OK, "[]");

        await client.GetFavoriteAudioAsync(User);
        await client.GetPlaylistsAsync(User);
        await client.GetPlaylistEntriesAsync(Playlist, User);
        await client.AddToPlaylistAsync(Playlist, Song, User, 0);
        await client.RemovePlaylistEntriesAsync(Playlist, [Song]);
        await client.MarkFavoriteAsync(Song, User);
        await client.GetUserItemDataAsync(Song, User);
        await client.ItemExistsAsync(Song);
        await client.GetSmartListsAsync();

        var expected = $"MediaBrowser Token=\"{Key}\", Client=\"Encore\", Device=\"Encore\", DeviceId=\"{DeviceId}\", Version=\"0.4.0\"";
        Assert.Equal(10, script.Sent.Count);
        Assert.All(script.Sent, s =>
        {
            Assert.Equal(expected, s.Authorization);
            Assert.DoesNotContain(s.HeaderNames, h => h.StartsWith("X-", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("api_key", s.PathAndQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/Move/", s.PathAndQuery, StringComparison.Ordinal);
        });
    }
}
