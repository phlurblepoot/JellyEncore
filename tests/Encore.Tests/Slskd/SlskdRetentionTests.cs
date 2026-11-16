using System.Net;
using Encore.Slskd;

namespace Encore.Tests.Slskd;

public class SlskdRetentionTests
{
    private const string Key = "slskd-test-key-0123456789abcdef";

    private static SlskdClient Make(SlskdHandler fake)
    {
        var o = new SlskdClientOptions { Timeout = TimeSpan.FromMilliseconds(300), GetRetryDelay = TimeSpan.FromMilliseconds(1) };
        var http = new HttpClient(new SlskdAuthHandler(Key) { InnerHandler = fake })
        {
            BaseAddress = new Uri("http://gluetun-slskd:5030/"),
            Timeout = o.Timeout,
        };
        return new SlskdClient(http, o);
    }

    // data is a JSON string holding JSON (facts section 6.1).
    private const string Events = """
        [{"timestamp":"2026-11-16T20:01:02.123Z","type":"UploadFileComplete","id":"9f3e0000-0000-0000-0000-000000000001",
          "data":"{\"version\":0,\"localFilename\":\"/music-radio/soulseek/A/B (2001)/01 - T.flac\",\"remoteFilename\":\"@@abcde\\\\soulseek\\\\A\\\\B (2001)\\\\01 - T.flac\",\"transfer\":{\"id\":\"4c1d0000-0000-0000-0000-000000000001\",\"username\":\"peer1\",\"direction\":\"Upload\",\"filename\":\"x\",\"size\":31457280,\"state\":\"Completed, Succeeded\",\"bytesTransferred\":31457280,\"removed\":false}}"},
         {"timestamp":"2026-11-16T19:00:00Z","type":"DownloadFileComplete","id":"9f3e0000-0000-0000-0000-000000000002","data":"{\"transfer\":{\"id\":\"4c1d0000-0000-0000-0000-000000000009\",\"username\":\"p\"},\"localFilename\":\"/d/x.flac\"}"},
         {"timestamp":"2026-11-16T18:30:00.5Z","type":"UploadFileComplete","id":"9f3e0000-0000-0000-0000-000000000003",
          "data":"{\"version\":0,\"localFilename\":\"/music/Lib/Song.flac\",\"transfer\":{\"id\":\"4c1d0000-0000-0000-0000-000000000002\",\"username\":\"peer2\",\"requestedAt\":\"2026-11-16T18:00:00\",\"enqueuedAt\":\"2026-11-16T18:00:01Z\",\"endedAt\":\"2026-11-16T18:30:00Z\"}}"},
         {"timestamp":"2026-11-16T18:00:00Z","type":"UploadFileComplete","id":"9f3e0000-0000-0000-0000-000000000004","data":"not json at all"}]
        """;

    [Fact]
    public async Task Events_parse_the_data_string_in_a_second_step_and_read_only_four_fields()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, Events, "X-Total-Count", "1234");
        var page = await Make(fake).GetEventsAsync(200, 100, default);

        Assert.Equal("/api/v0/events?offset=200&limit=100", fake.Sent[0].PathAndQuery);
        Assert.Equal(Key, fake.Sent[0].Headers["X-API-Key"]);
        Assert.Equal(1234, page.Total);
        Assert.Equal(4, page.RecordCount);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T18:00:00Z"), page.OldestTimestamp);

        // The download event and the record with unreadable data are skipped; order is kept (newest first).
        Assert.Equal(2, page.Uploads.Count);
        var first = page.Uploads[0];
        Assert.Equal("/music-radio/soulseek/A/B (2001)/01 - T.flac", first.LocalFilename);
        Assert.Equal(Guid.Parse("4c1d0000-0000-0000-0000-000000000001"), first.TransferId);
        Assert.Equal("peer1", first.Username);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T20:01:02.123Z"), first.CompletedUtc);

        // Live shape: requestedAt has no Z and must not matter.
        Assert.Equal("peer2", page.Uploads[1].Username);
        Assert.Equal("/music/Lib/Song.flac", page.Uploads[1].LocalFilename);
    }

    [Fact]
    public async Task Events_without_the_total_header_and_an_empty_page()
    {
        var page = await Make(new SlskdHandler().Reply(HttpStatusCode.OK, "[]")).GetEventsAsync(0, 100, default);
        Assert.Empty(page.Uploads);
        Assert.Equal(0, page.RecordCount);
        Assert.Null(page.OldestTimestamp);
        Assert.Null(page.Total);
    }

    [Fact]
    public async Task Uploads_are_flattened_from_user_directory_and_files()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, """
            [{"username":"peer1","directories":[{"directory":"@@abcde\\soulseek\\A\\B (2001)","fileCount":2,
              "files":[{"id":"4c1d0000-0000-0000-0000-000000000001","username":"peer1","filename":"@@abcde\\soulseek\\A\\B (2001)\\01 - T.flac","state":"InProgress"},
                       {"id":"4c1d0000-0000-0000-0000-000000000002","username":"peer1","filename":"@@abcde\\music\\x.flac","state":"Completed, Succeeded"}]}]},
             {"username":"peer2","directories":[{"directory":"d","fileCount":1,"files":[{"id":"4c1d0000-0000-0000-0000-000000000003","filename":"f","state":"Queued, Remotely"}]}]}]
            """);
        var uploads = await Make(fake).GetUploadsAsync(default);

        Assert.Equal("/api/v0/transfers/uploads", fake.Sent[0].PathAndQuery);
        Assert.Equal(3, uploads.Count);
        Assert.Equal("@@abcde\\soulseek\\A\\B (2001)\\01 - T.flac", uploads[0].Filename);
        Assert.True(uploads[0].IsActive);
        Assert.False(uploads[1].IsActive);
        Assert.True(uploads[2].IsActive);
        Assert.Equal("peer2", uploads[2].Username);
    }

    [Fact]
    public async Task Shares_are_flattened_over_the_host_keys_with_isExcluded()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, """
            {"local":[{"id":"s1","alias":"music","isExcluded":false,"localPath":"/music","raw":"/music","remotePath":"@@abcde\\music","directories":120,"files":1528},
                      {"id":"s2","alias":"soulseek","isExcluded":true,"localPath":"/music-radio/soulseek","raw":"/music-radio/soulseek","remotePath":"@@abcde\\soulseek","directories":0,"files":0}],
             "other":[{"id":"s3","alias":"z","isExcluded":false,"localPath":"/z","remotePath":"@@q\\z","directories":1,"files":2}]}
            """);
        var shares = await Make(fake).GetSharesAsync(default);

        Assert.Equal("/api/v0/shares", fake.Sent[0].PathAndQuery);
        Assert.Equal(3, shares.Count);
        Assert.Equal(("local", "music", false, 1528), (shares[0].Host, shares[0].Alias, shares[0].IsExcluded, shares[0].Files));
        Assert.True(shares[1].IsExcluded);
        Assert.Equal("/music-radio/soulseek", shares[1].LocalPath);
        Assert.Equal("@@abcde\\soulseek", shares[1].RemotePath);
        Assert.Equal("other", shares[2].Host);
    }

    [Fact]
    public async Task Share_state_comes_from_application_and_rescan_is_one_put_never_retried()
    {
        var fake = new SlskdHandler()
            .Reply(HttpStatusCode.OK, """{"server":{"isLoggedIn":true},"shares":{"scanPending":true,"scanning":false,"ready":true,"faulted":false,"scanProgress":0,"hosts":1}}""")
            .Reply(HttpStatusCode.OK)
            .Reply(HttpStatusCode.InternalServerError);
        var client = Make(fake);

        var state = await client.GetShareStateAsync(default);
        await client.RescanSharesAsync(default);
        await Assert.ThrowsAsync<SlskdRequestException>(() => client.RescanSharesAsync(default));

        Assert.Equal("/api/v0/application", fake.Sent[0].PathAndQuery);
        Assert.Equal(new SlskdShareState(false, true, true, false), state);
        Assert.Equal(["PUT", "PUT"], fake.Sent.Skip(1).Select(s => s.Method));
        Assert.All(fake.Sent.Skip(1), s => Assert.Equal("/api/v0/shares", s.PathAndQuery));
        Assert.Equal(3, fake.Sent.Count);
    }

    [Fact]
    public async Task Keys_are_absent_from_urls_and_from_errors()
    {
        foreach (var status in new[] { 401, 403, 404, 500 })
        {
            var calls = new List<Func<SlskdClient, Task>>
            {
                c => c.GetEventsAsync(0, 100, default),
                c => c.GetUploadsAsync(default),
                c => c.GetSharesAsync(default),
                c => c.GetShareStateAsync(default),
                c => c.RescanSharesAsync(default),
            };
            foreach (var call in calls)
            {
                var fake = new SlskdHandler().Reply((HttpStatusCode)status, $"{{\"error\":\"{Key}\"}}").Reply((HttpStatusCode)status, $"{{\"error\":\"{Key}\"}}");
                var e = await Assert.ThrowsAnyAsync<Exception>(() => call(Make(fake)));
                var all = e.ToString();
                Assert.DoesNotContain(Key, all);
                Assert.DoesNotContain("gluetun-slskd", all);
                Assert.DoesNotContain("X-API-Key", all, StringComparison.OrdinalIgnoreCase);
                Assert.All(fake.Sent, s => Assert.DoesNotContain(Key, s.FullUri));
            }
        }

        var bad = new SlskdHandler().Reply(HttpStatusCode.OK, $"not json {Key}");
        var ex = await Assert.ThrowsAsync<SlskdRequestException>(() => Make(bad).GetEventsAsync(0, 10, default));
        Assert.DoesNotContain(Key, ex.ToString());
    }

    [Fact]
    public async Task Fake_slskd_implements_the_retention_calls()
    {
        var fake = new Encore.Tests.Fetch.FakeSlskd(TimeProvider.System, Path.GetTempPath());
        fake.UploadEvents.Add(new SlskdUploadEvent(Guid.NewGuid(), "u", "/music-radio/soulseek/a.flac", DateTimeOffset.UtcNow));
        IRetentionSlskd api = fake;
        Assert.Single((await api.GetEventsAsync(0, 10, default)).Uploads);
        await api.RescanSharesAsync(default);
        Assert.Equal(1, fake.RescanCalls);
    }
}
