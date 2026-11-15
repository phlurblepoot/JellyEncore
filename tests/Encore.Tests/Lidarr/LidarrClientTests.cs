using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encore.Config;
using Encore.Lidarr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.Lidarr;

public class LidarrClientTests
{
    private const string Key = "LidarrKey0123456789abcdefAB";
    private const string AlbumMbid = "b1392450-e666-3926-a536-22c65f834433";
    private const string ArtistMbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private static LidarrClientOptions FastOptions() => new()
    {
        Timeout = TimeSpan.FromMilliseconds(300),
        GetRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private static LidarrClient Make(FakeLidarr fake, LidarrClientOptions? options = null)
    {
        var o = options ?? FastOptions();
        var http = new HttpClient(new LidarrAuthHandler(Key) { InnerHandler = fake })
        {
            BaseAddress = new Uri("http://lidarr:8686/"),
            Timeout = o.Timeout,
        };
        return new LidarrClient(http, o);
    }

    [Fact]
    public async Task Every_call_sends_exactly_the_X_Api_Key_header_and_no_key_in_the_url()
    {
        var fake = new FakeLidarr()
            .Reply(HttpStatusCode.OK, LidarrJson.Status)
            .Reply(HttpStatusCode.OK, LidarrJson.RootFolders)
            .Reply(HttpStatusCode.OK, LidarrJson.Lookup)
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Artist}]")
            .Reply(HttpStatusCode.OK, LidarrJson.Artist)
            .Reply(HttpStatusCode.OK, LidarrJson.Artist).Reply(HttpStatusCode.Accepted, LidarrJson.Artist)
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]")
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]")
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]")
            .Reply(HttpStatusCode.OK, LidarrJson.Album)
            .Reply(HttpStatusCode.Created, LidarrJson.Album)
            .Reply(HttpStatusCode.Accepted, $"[{LidarrJson.Album}]");
        var client = Make(fake);

        await client.GetStatusAsync(default);
        await client.GetRootFoldersAsync(default);
        await client.LookupAlbumsAsync($"lidarr:{AlbumMbid}", default);
        await client.FindArtistByForeignIdAsync(ArtistMbid, default);
        await client.GetArtistAsync(42, default);
        await client.SetArtistMonitoredAsync(42, true, default);
        await client.GetAlbumsByArtistAsync(42, default);
        await client.GetAlbumsByForeignIdAsync(AlbumMbid, false, default);
        await client.GetAlbumsByIdsAsync([314, 315], default);
        await client.GetAlbumAsync(314, default);
        await client.AddAlbumAsync(new LidarrAddAlbum(AlbumMbid, "OK Computer", LidarrArtistBodies.ForNewArtist(ArtistMbid, "Radiohead", 3, 2, "/media/music")), default);
        await client.SetAlbumsMonitoredAsync([314], true, default);

        Assert.Equal(13, fake.Sent.Count);
        foreach (var sent in fake.Sent)
        {
            Assert.Equal(Key, sent.Headers["X-Api-Key"]);
            Assert.Single(sent.Headers.Keys, k => k.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase));
            Assert.False(sent.Headers.ContainsKey("Authorization"));
            Assert.DoesNotContain(Key, sent.FullUri);
            Assert.DoesNotContain("apikey", sent.FullUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api_key", sent.FullUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Key, sent.Body ?? "");
        }
    }

    [Fact]
    public async Task Calls_use_the_documented_paths_and_queries()
    {
        var fake = new FakeLidarr()
            .Reply(HttpStatusCode.OK, LidarrJson.Status)
            .Reply(HttpStatusCode.OK, LidarrJson.RootFolders)
            .Reply(HttpStatusCode.OK, LidarrJson.Lookup)
            .Reply(HttpStatusCode.OK, "[]")
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]")
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]")
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]")
            .Reply(HttpStatusCode.OK, $"[{LidarrJson.Album}]");
        var client = Make(fake);

        await client.GetStatusAsync(default);
        await client.GetRootFoldersAsync(default);
        await client.LookupAlbumsAsync("OK Computer & more", default);
        await client.FindArtistByForeignIdAsync(ArtistMbid, default);
        await client.GetAlbumsByArtistAsync(42, default);
        await client.GetAlbumsByForeignIdAsync(AlbumMbid, false, default);
        await client.GetAlbumsByForeignIdAsync(AlbumMbid, true, default);
        await client.GetAlbumsByIdsAsync([314, 315], default);

        Assert.Equal(
            [
                "/api/v1/system/status",
                "/api/v1/rootfolder",
                "/api/v1/album/lookup?term=OK%20Computer%20%26%20more",
                $"/api/v1/artist?mbId={ArtistMbid}",
                "/api/v1/album?artistId=42",
                $"/api/v1/album?foreignAlbumId={AlbumMbid}",
                $"/api/v1/album?foreignAlbumId={AlbumMbid}&includeAllArtistAlbums=true",
                "/api/v1/album?albumIds=314&albumIds=315",
            ],
            fake.Sent.Select(s => s.PathAndQuery).ToArray());
        Assert.All(fake.Sent, s => Assert.Equal("GET", s.Method));
    }

    [Fact]
    public async Task Status_and_root_folders_are_read()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, LidarrJson.Status).Reply(HttpStatusCode.OK, LidarrJson.RootFolders);
        var client = Make(fake);

        var status = await client.GetStatusAsync(default);
        var folders = await client.GetRootFoldersAsync(default);

        Assert.Equal("Lidarr", status.AppName);
        Assert.Equal("3.1.6.5079", status.Version);
        var folder = Assert.Single(folders);
        Assert.Equal("/media/music", folder.Path);
        Assert.Equal(3, folder.DefaultQualityProfileId);
        Assert.Equal(2, folder.DefaultMetadataProfileId);
        Assert.True(folder.Accessible);
    }

    [Fact]
    public async Task Lookup_maps_an_unknown_album_with_id_zero_and_no_statistics()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, LidarrJson.Lookup);
        var album = Assert.Single(await Make(fake).LookupAlbumsAsync($"lidarr:{AlbumMbid}", default));

        Assert.Equal($"/api/v1/album/lookup?term=lidarr%3A{AlbumMbid}", fake.Sent[0].PathAndQuery);
        Assert.Equal(0, album.Id);
        Assert.False(album.Monitored);
        Assert.Equal(AlbumMbid, album.ForeignAlbumId);
        Assert.Equal("Album", album.AlbumType);
        Assert.Equal(["Live"], album.SecondaryTypes);
        Assert.Equal(new DateTimeOffset(1997, 5, 21, 0, 0, 0, TimeSpan.Zero), album.ReleaseDate);
        Assert.Equal("https://example.invalid/cover.jpg", album.RemoteCover);
        Assert.Equal(12, Assert.Single(album.Releases).TrackCount);
        Assert.Equal(ArtistMbid, album.Artist!.ForeignArtistId);
        Assert.Equal("Radiohead", album.Artist.ArtistName);
        Assert.Null(album.Statistics);
    }

    [Fact]
    public async Task Album_detail_carries_statistics_and_the_artist()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, LidarrJson.Album);
        var album = await Make(fake).GetAlbumAsync(314, default);

        Assert.NotNull(album);
        Assert.Equal(314, album.Id);
        Assert.True(album.Artist!.Monitored);
        Assert.Equal(42, album.Artist.Id);
        Assert.Equal(new LidarrAlbumStatistics(5, 12, 12, 1000, 41.5), album.Statistics);
    }

    [Fact]
    public async Task A_missing_artist_or_album_is_null_and_an_unknown_foreign_id_is_an_empty_list()
    {
        var fake = new FakeLidarr()
            .Reply(HttpStatusCode.NotFound)
            .Reply(HttpStatusCode.NotFound)
            .Reply(HttpStatusCode.OK, "[]")
            .Reply(HttpStatusCode.OK, "[]");
        var client = Make(fake);

        Assert.Null(await client.GetArtistAsync(1, default));
        Assert.Null(await client.GetAlbumAsync(1, default));
        Assert.Empty(await client.GetAlbumsByForeignIdAsync(AlbumMbid, false, default));
        Assert.Null(await client.FindArtistByForeignIdAsync(ArtistMbid, default));
    }

    [Fact]
    public async Task An_empty_id_list_makes_no_call()
    {
        var fake = new FakeLidarr();
        Assert.Empty(await Make(fake).GetAlbumsByIdsAsync([], default));
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task Setting_the_artist_monitored_puts_the_whole_resource_back_with_only_monitored_changed()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, LidarrJson.Artist).Reply(HttpStatusCode.Accepted, LidarrJson.Artist.Replace("\"monitored\":false", "\"monitored\":true"));
        var result = await Make(fake).SetArtistMonitoredAsync(42, true, default);

        Assert.Equal(["GET", "PUT"], fake.Sent.Select(s => s.Method).ToArray());
        var put = Assert.Single(fake.Writes);
        Assert.Equal("/api/v1/artist/42", put.PathAndQuery);

        var sent = JsonNode.Parse(put.Body!)!.AsObject();
        var original = JsonNode.Parse(LidarrJson.Artist)!.AsObject();
        Assert.True(sent["monitored"]!.GetValue<bool>());
        Assert.Equal("/media/music/Radiohead", sent["path"]!.GetValue<string>());
        Assert.Equal("none", sent["monitorNewItems"]!.GetValue<string>());
        original["monitored"] = true;
        Assert.True(JsonNode.DeepEquals(original, sent), "the PUT body must equal the GET body except monitored");
        Assert.True(result!.Monitored);
    }

    [Fact]
    public async Task Setting_a_gone_artist_monitored_makes_no_write()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.NotFound);
        Assert.Null(await Make(fake).SetArtistMonitoredAsync(42, true, default));
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task Add_album_posts_the_design_body_with_a_new_artist_and_no_search()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.Created, LidarrJson.Album);
        var added = await Make(fake).AddAlbumAsync(
            new LidarrAddAlbum(AlbumMbid, "OK Computer", LidarrArtistBodies.ForNewArtist(ArtistMbid, "Radiohead", 3, 2, "/media/music")), default);

        var post = Assert.Single(fake.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal("/api/v1/album", post.PathAndQuery);
        var body = JsonNode.Parse(post.Body!)!.AsObject();
        Assert.Equal(AlbumMbid, body["foreignAlbumId"]!.GetValue<string>());
        Assert.Equal("OK Computer", body["title"]!.GetValue<string>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.True(body["anyReleaseOk"]!.GetValue<bool>());
        Assert.Equal("manual", body["addOptions"]!["addType"]!.GetValue<string>());
        Assert.False(body["addOptions"]!["searchForNewAlbum"]!.GetValue<bool>());
        var artist = body["artist"]!.AsObject();
        Assert.Equal(ArtistMbid, artist["foreignArtistId"]!.GetValue<string>());
        Assert.Equal("Radiohead", artist["artistName"]!.GetValue<string>());
        Assert.Equal(3, artist["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(2, artist["metadataProfileId"]!.GetValue<int>());
        Assert.Equal("/media/music", artist["rootFolderPath"]!.GetValue<string>());
        Assert.True(artist["monitored"]!.GetValue<bool>());
        Assert.Equal("none", artist["monitorNewItems"]!.GetValue<string>());
        Assert.False(artist.ContainsKey("addOptions"));
        Assert.Equal(314, added.Id);
    }

    [Fact]
    public async Task Add_album_for_an_existing_artist_sends_the_artist_as_returned()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, $"[{LidarrJson.Artist}]").Reply(HttpStatusCode.Created, LidarrJson.Album);
        var client = Make(fake);
        var artist = await client.FindArtistByForeignIdAsync(ArtistMbid, default);

        await client.AddAlbumAsync(new LidarrAddAlbum(AlbumMbid, "OK Computer", artist!.Raw), default);

        var body = JsonNode.Parse(fake.Writes.Single().Body!)!.AsObject();
        Assert.Equal("/media/music/Radiohead", body["artist"]!["path"]!.GetValue<string>());
        Assert.Equal(42, body["artist"]!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task Set_albums_monitored_puts_ids_and_flag()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.Accepted, $"[{LidarrJson.Album}]");
        var result = await Make(fake).SetAlbumsMonitoredAsync([314], false, default);

        var put = Assert.Single(fake.Writes);
        Assert.Equal("PUT", put.Method);
        Assert.Equal("/api/v1/album/monitor", put.PathAndQuery);
        var body = JsonNode.Parse(put.Body!)!.AsObject();
        Assert.Equal([314], body["albumIds"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
        Assert.False(body["monitored"]!.GetValue<bool>());
        Assert.Equal(314, Assert.Single(result).Id);
    }

    [Fact]
    public async Task There_is_no_artist_post_anywhere_in_the_client()
    {
        var methods = typeof(ILidarrClient).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(methods, n => n.Contains("AddArtist", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_400_validation_array_is_parsed_with_PascalCase_names()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.BadRequest, LidarrJson.AlreadyAdded);
        var e = await Assert.ThrowsAsync<LidarrValidationException>(() =>
            Make(fake).AddAlbumAsync(new LidarrAddAlbum(AlbumMbid, "x", LidarrArtistBodies.ForNewArtist(ArtistMbid, "R", 1, 1, "/m")), default));

        var failure = Assert.Single(e.Failures);
        Assert.Equal("ForeignAlbumId", failure.PropertyName);
        Assert.Equal("This album has already been added.", failure.ErrorMessage);
        Assert.True(e.IsAlreadyAdded);
        Assert.Equal(400, e.StatusCode);
        Assert.Equal("AddAlbum", e.Operation);
    }

    [Fact]
    public async Task A_400_with_several_failures_and_a_400_of_another_shape()
    {
        var fake = new FakeLidarr()
            .Reply(HttpStatusCode.BadRequest, """[{"propertyName":"Artist.RootFolderPath","errorMessage":"Root folder '/x' does not exist"},{"propertyName":"Artist.QualityProfileId","errorMessage":"Quality Profile does not exist"}]""")
            .Reply(HttpStatusCode.BadRequest, """{"message":"Request body can't be empty"}""")
            .Reply(HttpStatusCode.BadRequest);
        var client = Make(fake);

        var many = await Assert.ThrowsAsync<LidarrValidationException>(() => client.SetAlbumsMonitoredAsync([1], true, default));
        Assert.Equal(["Artist.RootFolderPath", "Artist.QualityProfileId"], many.Failures.Select(f => f.PropertyName).ToArray());
        Assert.False(many.IsAlreadyAdded);

        var other = await Assert.ThrowsAsync<LidarrValidationException>(() => client.SetAlbumsMonitoredAsync([1], true, default));
        Assert.Empty(other.Failures);
        var empty = await Assert.ThrowsAsync<LidarrValidationException>(() => client.SetAlbumsMonitoredAsync([1], true, default));
        Assert.Empty(empty.Failures);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_rejected_key_is_an_auth_exception_and_is_not_retried(int status)
    {
        var fake = new FakeLidarr().Reply((HttpStatusCode)status).Reply((HttpStatusCode)status);
        var e = await Assert.ThrowsAsync<LidarrAuthException>(() => Make(fake).GetStatusAsync(default));

        Assert.Equal(status, e.StatusCode);
        Assert.Equal("GetStatus", e.Operation);
        Assert.Single(fake.Sent);
    }

    [Fact]
    public async Task A_get_that_gets_5xx_is_tried_once_more_and_a_write_is_not()
    {
        var fake = new FakeLidarr()
            .Reply(HttpStatusCode.BadGateway)
            .Reply(HttpStatusCode.OK, LidarrJson.Status)
            .Reply(HttpStatusCode.InternalServerError)
            .Reply(HttpStatusCode.OK, LidarrJson.Album);
        var client = Make(fake);

        Assert.Equal("3.1.6.5079", (await client.GetStatusAsync(default)).Version);
        var e = await Assert.ThrowsAsync<LidarrRequestException>(() => client.AddAlbumAsync(
            new LidarrAddAlbum(AlbumMbid, "x", LidarrArtistBodies.ForNewArtist(ArtistMbid, "R", 1, 1, "/m")), default));
        Assert.Equal(500, e.StatusCode);
        Assert.Equal(3, fake.Sent.Count);
    }

    [Fact]
    public async Task A_get_that_keeps_getting_5xx_fails_after_two_tries()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.BadGateway).Reply(HttpStatusCode.BadGateway);
        var e = await Assert.ThrowsAsync<LidarrRequestException>(() => Make(fake).GetStatusAsync(default));
        Assert.Equal(502, e.StatusCode);
        Assert.Equal(2, fake.Sent.Count);
    }

    [Fact]
    public async Task Failures_never_put_the_key_url_or_body_in_exception_text()
    {
        var failures = new List<Func<Task>>();
        foreach (var status in new[] { 400, 401, 403, 404, 409, 500 })
        {
            var body = $"{{\"error\":\"{Key}\"}}";
            failures.Add(() => Make(new FakeLidarr().Reply((HttpStatusCode)status, body).Reply((HttpStatusCode)status, body)).GetStatusAsync(default));
            failures.Add(() => Make(new FakeLidarr().Reply((HttpStatusCode)status, body)).SetAlbumsMonitoredAsync([1], true, default));
        }

        failures.Add(() => Make(new FakeLidarr().Fail(new HttpRequestException($"cannot reach http://lidarr:8686/?apikey={Key}", null, null))).GetStatusAsync(default));
        failures.Add(() => Make(new FakeLidarr().Reply(HttpStatusCode.OK, $"not json {Key}")).GetStatusAsync(default));
        failures.Add(() => Make(new FakeLidarr().Hang()).GetStatusAsync(default));

        foreach (var failure in failures)
        {
            var e = await Assert.ThrowsAnyAsync<LidarrException>(failure);
            var all = e + " " + e.Message + " " + e.StackTrace + " " + e.InnerException;
            Assert.DoesNotContain(Key, all);
            Assert.DoesNotContain("lidarr:8686", all);
            Assert.DoesNotContain("X-Api-Key", all, StringComparison.OrdinalIgnoreCase);
            Assert.Null(e.InnerException);
            if (e is LidarrValidationException v)
            {
                Assert.DoesNotContain(Key, string.Join(" ", v.Failures.Select(f => f.ErrorMessage + f.PropertyName)));
            }
        }
    }

    [Fact]
    public async Task A_timeout_is_a_request_exception_but_the_callers_cancellation_is_not_swallowed()
    {
        var e = await Assert.ThrowsAsync<LidarrRequestException>(() => Make(new FakeLidarr().Hang()).GetStatusAsync(default));
        Assert.Null(e.StatusCode);

        using var cts = new CancellationTokenSource();
        var pending = Make(new FakeLidarr().Hang(), new LidarrClientOptions { Timeout = TimeSpan.FromSeconds(30) }).GetStatusAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void The_auth_handler_rejects_a_blank_or_malformed_key_without_echoing_it()
    {
        Assert.Throws<ArgumentException>(() => new LidarrAuthHandler(" "));
        var e = Assert.Throws<ArgumentException>(() => new LidarrAuthHandler("bad key\n"));
        Assert.DoesNotContain("bad key", e.Message);
        Assert.DoesNotContain(Key, new LidarrAuthHandler(Key).ToString());
    }

    [Fact]
    public async Task Registered_client_sends_the_key_and_logs_never_show_it()
    {
        var logger = new CapturingLoggerProvider();
        var fake = new FakeLidarr()
            .Reply(HttpStatusCode.OK, LidarrJson.Status)
            .Reply(HttpStatusCode.Unauthorized, $"{{\"error\":\"{Key}\"}}");
        var options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jf:8096"),
            JellyfinApiKey = "jellyfin-key-0123456789",
            AllowedUsers = [],
            DataDir = "/data",
            LidarrUrl = new Uri("http://lidarr:8686"),
            LidarrApiKey = Key,
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logger));
        services.AddLidarrClient(options).ConfigurePrimaryHttpMessageHandler(() => fake);
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<ILidarrClient>();
        Assert.Equal("3.1.6.5079", (await client.GetStatusAsync(default)).Version);
        await Assert.ThrowsAsync<LidarrAuthException>(() => client.GetStatusAsync(default));

        Assert.Equal(Key, fake.Sent[0].Headers["X-Api-Key"]);
        Assert.Equal("http://lidarr:8686/api/v1/system/status", fake.Sent[0].FullUri);
        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, l => l.Contains(Key));
        Assert.NotNull(provider.GetRequiredService<LidarrStatusMonitor>());
    }

    [Fact]
    public void Add_lidarr_client_needs_url_and_key()
    {
        var options = new EncoreOptions { JellyfinUrl = new Uri("http://jf:8096"), JellyfinApiKey = "k", AllowedUsers = [], DataDir = "/data" };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLidarrClient(options));
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + " " + exception;
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    text += " " + string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"));
                }

                lock (owner.Lines)
                {
                    owner.Lines.Add(text);
                }
            }
        }
    }
}
