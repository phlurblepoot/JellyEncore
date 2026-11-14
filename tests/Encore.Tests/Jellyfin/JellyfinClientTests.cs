using System.Net;
using Encore.Jellyfin;

namespace Encore.Tests.Jellyfin;

public sealed class JellyfinClientTests
{
    private const string Key = "SECRETKEY0123456789abcdef";
    private const string DeviceId = "encore-0123456789ab";
    private const string Version = "0.1.0";
    private static readonly Guid UserId = Guid.Parse("4f1c2d3e-5a6b-4c7d-8e9f-0a1b2c3d4e5f");
    private static readonly Guid SongA = Guid.Parse("9d8c7b6a-5f4e-3d2c-1b0a-998877665544");
    private static readonly Guid SongB = Guid.Parse("11223344-5566-7788-990a-abbccddeeff0");
    private static readonly Guid ArtistSeed = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");

    private static (JellyfinClient Client, ScriptedHandler Script) Build(TimeSpan? timeout = null)
    {
        var script = new ScriptedHandler();
        var auth = new JellyfinAuthHandler(JellyfinAuth.BuildParameter(Key, DeviceId, Version)) { InnerHandler = script };
        var http = new HttpClient(auth)
        {
            BaseAddress = new Uri("http://jellyfin.test:8096/jf/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        // Zero delays and no jitter keep the retry tests fast and exact.
        var options = new JellyfinClientOptions { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], Jitter = 0 };
        return (new JellyfinClient(http, options), script);
    }

    // ---- Auth header -------------------------------------------------------------------------

    [Fact]
    public async Task Every_request_carries_the_exact_MediaBrowser_header()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.Sessions).Reply(HttpStatusCode.NoContent);

        await client.GetSessionsAsync();
        await client.PlayLastAsync("e3a7f0c2", [SongA]);

        var expected = $"MediaBrowser Token=\"{Key}\", Client=\"Encore\", Device=\"Encore\", DeviceId=\"{DeviceId}\", Version=\"{Version}\"";
        Assert.All(script.Sent, sent => Assert.Equal(expected, sent.Authorization));
    }

    [Fact]
    public async Task No_request_sends_X_Emby_headers_or_an_api_key_query()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.Sessions)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Users)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.SimilarArtists)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Songs)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Songs)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Songs)
            .Reply(HttpStatusCode.NoContent);

        await client.GetSessionsAsync();
        await client.GetUsersAsync();
        await client.GetSimilarArtistsAsync(ArtistSeed, UserId);
        await client.GetSongsByArtistsAsync([ArtistSeed], UserId, 20);
        await client.GetSimilarSongsAsync(SongA, UserId, excludeArtistIds: [ArtistSeed]);
        await client.GetInstantMixAsync(SongA, UserId);
        await client.PlayLastAsync("e3a7f0c2", [SongA, SongB]);

        Assert.Equal(7, script.Sent.Count);
        foreach (var sent in script.Sent)
        {
            Assert.DoesNotContain(sent.HeaderNames, h => h.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                || h.Contains("emby", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("api_key", sent.PathAndQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apikey", sent.PathAndQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Key, sent.PathAndQuery);
        }
    }

    [Fact]
    public void Auth_parameter_rejects_quotes_and_control_characters_without_echoing_the_value()
    {
        var bad = Assert.Throws<ArgumentException>(() => JellyfinAuth.BuildParameter("abc\"def", DeviceId, Version));
        Assert.Equal("apiKey", bad.ParamName);
        Assert.DoesNotContain("abc", bad.Message);

        Assert.Throws<ArgumentException>(() => JellyfinAuth.BuildParameter("", DeviceId, Version));
        Assert.Throws<ArgumentException>(() => JellyfinAuth.BuildParameter(Key, "dev\nid", Version));
    }

    // ---- Typed DTOs from fixtures ------------------------------------------------------------

    [Fact]
    public async Task Sessions_parse_playing_session_and_encore_session()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.Sessions);

        var sessions = await client.GetSessionsAsync();

        Assert.Equal(2, sessions.Count);
        var web = sessions[0];
        Assert.Equal("e3a7f0c2b1d94a5e8f6c7b2a1d0e9f8c", web.Id);
        Assert.Equal(UserId, web.UserId);
        Assert.Equal("nathan", web.UserName);
        Assert.Equal("Jellyfin Web", web.Client);
        Assert.True(web.SupportsRemoteControl);
        Assert.Equal(SongA, web.NowPlayingItem!.Id);
        Assert.Equal("Song A", web.NowPlayingItem.Name);
        Assert.Equal(2, web.NowPlayingQueue!.Count);
        Assert.Equal("playlistItem1", web.NowPlayingQueue[1].PlaylistItemId);
        Assert.Equal("playlistItem0", web.PlaylistItemId);
        Assert.Equal(JellyfinRepeatMode.RepeatAll, web.PlayState!.RepeatMode);
        Assert.Equal(JellyfinPlaybackOrder.Shuffle, web.PlayState.PlaybackOrder);
        Assert.Equal(123450000L, web.PlayState.PositionTicks);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 18, 0, 0, TimeSpan.Zero), web.LastActivityDate);

        var encore = sessions[1];
        Assert.Equal("f00dbabe00000000000000000000beef", encore.Id);
        Assert.Equal(Guid.Empty, encore.UserId);
        Assert.Null(encore.NowPlayingItem);
        Assert.NotNull(encore.NowPlayingQueue);
        Assert.Empty(encore.NowPlayingQueue);
        Assert.Equal(JellyfinRepeatMode.RepeatNone, encore.PlayState!.RepeatMode);
    }

    [Fact]
    public async Task Finamp_empty_queue_is_an_empty_list_and_a_missing_queue_is_null()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.FinampLastSong)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.SessionWithoutQueue);

        var last = (await client.GetSessionsAsync())[0];
        Assert.NotNull(last.NowPlayingQueue);
        Assert.Empty(last.NowPlayingQueue);
        Assert.Equal(new Guid("9d8c7b6a-5f4e-3d2c-1b0a-998877665544"), last.NowPlayingItem!.Id);
        Assert.Equal("Artist One", last.NowPlayingItem.ArtistItems![0].Name);
        Assert.Equal(new[] { "Rock" }, last.NowPlayingItem.Genres);

        var none = (await client.GetSessionsAsync())[0];
        Assert.Null(none.NowPlayingQueue);
        Assert.Null(none.PlayState);
    }

    [Fact]
    public async Task Finamp_mid_queue_entry_keeps_its_queue_type_as_PlaylistItemId()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.FinampMidQueue);

        var session = (await client.GetSessionsAsync())[0];

        Assert.Single(session.NowPlayingQueue!);
        Assert.Equal("queue", session.NowPlayingQueue![0].PlaylistItemId);
    }

    [Fact]
    public async Task Unknown_enum_values_map_to_Unknown_instead_of_failing_the_poll()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.FutureEnumValues);

        var session = (await client.GetSessionsAsync())[0];

        Assert.Equal(JellyfinRepeatMode.Unknown, session.PlayState!.RepeatMode);
        Assert.Equal(JellyfinPlaybackOrder.Unknown, session.PlayState.PlaybackOrder);
        Assert.True(session.PlayState.IsPaused);
    }

    [Fact]
    public async Task Users_parse_with_policy()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.Users);

        var users = await client.GetUsersAsync();

        Assert.Equal(4, users.Count);
        Assert.Equal("nathan", users[0].Name);
        Assert.True(users[0].Policy!.IsAdministrator);
        Assert.True(users[1].Policy!.IsDisabled);
    }

    [Fact]
    public async Task Songs_query_result_parses_items_and_total()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.Songs);

        var result = await client.GetInstantMixAsync(SongA, UserId);

        Assert.Equal(1, result.TotalRecordCount);
        Assert.Equal("Song A", result.Items[0].Name);
        Assert.Equal("Album One", result.Items[0].Album);
        Assert.Equal(new[] { "Rock", "Indie" }, result.Items[0].Genres);
    }

    // ---- Owned-pick calls --------------------------------------------------------------------

    [Fact]
    public async Task Owned_pick_calls_use_the_T3_routes_and_parameters()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.SimilarArtists)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Songs)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Songs)
            .Reply(HttpStatusCode.OK, JellyfinFixtures.Songs);

        await client.GetSimilarArtistsAsync(ArtistSeed, UserId, limit: 10);
        await client.GetSongsByArtistsAsync([ArtistSeed, SongB], UserId, limit: 20);
        await client.GetSimilarSongsAsync(SongA, UserId, limit: 30, excludeArtistIds: [ArtistSeed]);
        await client.GetInstantMixAsync(SongA, UserId, limit: 50);

        var u = UserId.ToString("N");
        Assert.Equal($"/jf/Artists/{ArtistSeed:N}/Similar?userId={u}&limit=10", script.Sent[0].PathAndQuery);
        Assert.Equal(
            $"/jf/Items?artistIds={ArtistSeed:N},{SongB:N}&includeItemTypes=Audio&recursive=true&sortBy=Random&userId={u}&limit=20",
            script.Sent[1].PathAndQuery);
        Assert.Equal($"/jf/Items/{SongA:N}/Similar?userId={u}&limit=30&excludeArtistIds={ArtistSeed:N}", script.Sent[2].PathAndQuery);
        Assert.Equal($"/jf/Items/{SongA:N}/InstantMix?userId={u}&limit=50", script.Sent[3].PathAndQuery);
        Assert.All(script.Sent, sent => Assert.Equal("GET", sent.Method));
    }

    [Fact]
    public async Task Similar_songs_without_exclusions_omits_excludeArtistIds()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, JellyfinFixtures.Songs);

        await client.GetSimilarSongsAsync(SongA, UserId);

        Assert.DoesNotContain("excludeArtistIds", script.Sent[0].PathAndQuery);
    }

    [Fact]
    public async Task PlayLast_posts_with_playCommand_and_item_ids()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.NoContent);

        await client.PlayLastAsync("e3a7f0c2b1d94a5e8f6c7b2a1d0e9f8c", [SongA, SongB]);

        var sent = Assert.Single(script.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(
            $"/jf/Sessions/e3a7f0c2b1d94a5e8f6c7b2a1d0e9f8c/Playing?playCommand=PlayLast&itemIds={SongA:N},{SongB:N}",
            sent.PathAndQuery);
    }

    [Fact]
    public async Task PlayLast_rejects_an_empty_item_list_before_sending()
    {
        var (client, script) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => client.PlayLastAsync("abc", []));
        Assert.Empty(script.Sent);
    }

    // ---- GET retries -------------------------------------------------------------------------

    [Fact]
    public async Task GET_retries_on_5xx_and_then_succeeds()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.InternalServerError).Reply(HttpStatusCode.BadGateway).Reply(HttpStatusCode.OK, JellyfinFixtures.Users);

        var users = await client.GetUsersAsync();

        Assert.Equal("nathan", users[0].Name);
        Assert.Equal(3, script.Sent.Count);
    }

    [Fact]
    public async Task GET_gives_up_after_two_retries_on_5xx()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.GetSessionsAsync());

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("GetSessions", ex.Operation);
        Assert.Equal(3, script.Sent.Count);
    }

    [Fact]
    public async Task GET_does_not_retry_4xx_other_than_auth()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.GetUsersAsync());

        Assert.Equal(404, ex.StatusCode);
        Assert.Single(script.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failures_raise_JellyfinAuthException_once_without_retry(HttpStatusCode status)
    {
        var (client, script) = Build();
        script.Reply(status);

        var ex = await Assert.ThrowsAsync<JellyfinAuthException>(() => client.GetSessionsAsync());

        Assert.Equal((int)status, ex.StatusCode);
        Assert.Single(script.Sent);
    }

    // ---- Timeouts and transport errors -------------------------------------------------------

    [Fact]
    public async Task Timeout_is_reported_and_never_retried()
    {
        var (client, script) = Build(timeout: TimeSpan.FromMilliseconds(50));
        script.Hang();

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.GetSessionsAsync());

        Assert.Contains("timed out", ex.Message);
        Assert.Null(ex.StatusCode);
        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task Transport_error_is_reported_and_never_retried()
    {
        var (client, script) = Build();
        script.Fail(new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.GetSessionsAsync());

        Assert.Contains("connection failed", ex.Message);
        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_as_a_Jellyfin_failure()
    {
        var (client, script) = Build();
        script.Hang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSessionsAsync(cts.Token));

        Assert.IsNotType<JellyfinException>(thrown);
        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task Unreadable_body_is_a_request_failure()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "<html>not json</html>");

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.GetUsersAsync());

        Assert.Contains("not the expected JSON", ex.Message);
    }

    [Fact]
    public async Task Exception_text_never_contains_the_key_or_headers()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<JellyfinAuthException>(() => client.GetSessionsAsync());

        Assert.DoesNotContain(Key, ex.ToString());
        Assert.DoesNotContain("MediaBrowser", ex.ToString());
    }

    // ---- PlayLast retries --------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task PlayLast_retries_once_on_502_503_504(HttpStatusCode status)
    {
        var (client, script) = Build();
        script.Reply(status).Reply(HttpStatusCode.NoContent);

        await client.PlayLastAsync("abc", [SongA]);

        Assert.Equal(2, script.Sent.Count);
    }

    [Fact]
    public async Task PlayLast_gives_up_after_one_retry()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.BadGateway).Reply(HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.PlayLastAsync("abc", [SongA]));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(2, script.Sent.Count);
    }

    [Fact]
    public async Task PlayLast_does_not_retry_a_plain_500()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<JellyfinRequestException>(() => client.PlayLastAsync("abc", [SongA]));

        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task PlayLast_does_not_retry_a_timeout_or_transport_error()
    {
        var (timeoutClient, timeoutScript) = Build(timeout: TimeSpan.FromMilliseconds(50));
        timeoutScript.Hang();
        await Assert.ThrowsAsync<JellyfinRequestException>(() => timeoutClient.PlayLastAsync("abc", [SongA]));
        Assert.Single(timeoutScript.Sent);

        var (transportClient, transportScript) = Build();
        transportScript.Fail(new HttpRequestException("reset"));
        await Assert.ThrowsAsync<JellyfinRequestException>(() => transportClient.PlayLastAsync("abc", [SongA]));
        Assert.Single(transportScript.Sent);
    }

    [Fact]
    public async Task PlayLast_auth_failure_is_not_retried()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<JellyfinAuthException>(() => client.PlayLastAsync("abc", [SongA]));

        Assert.Single(script.Sent);
    }
}
