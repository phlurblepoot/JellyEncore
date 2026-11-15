using System.Net;
using System.Text.Json;
using Encore.Config;
using Encore.Slskd;
using Encore.Tests.Watch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Slskd;

public class SlskdClientTests
{
    private const string Key = "slskd-test-key-0123456789abcdef";
    private static readonly Guid SearchId = Guid.Parse("6f1c0000-0000-0000-0000-000000000001");
    private static readonly Guid TransferId = Guid.Parse("0d9e0000-0000-0000-0000-000000000003");

    private static SlskdClientOptions FastOptions() => new()
    {
        Timeout = TimeSpan.FromMilliseconds(300),
        GetRetryDelay = TimeSpan.FromMilliseconds(1),
        BusyDelay = TimeSpan.FromMilliseconds(1),
    };

    private static SlskdClient Make(SlskdHandler fake, SlskdClientOptions? options = null)
    {
        var o = options ?? FastOptions();
        var http = new HttpClient(new SlskdAuthHandler(Key) { InnerHandler = fake })
        {
            BaseAddress = new Uri("http://gluetun-slskd:5030/"),
            Timeout = o.Timeout,
        };
        return new SlskdClient(http, o);
    }

    [Fact]
    public async Task Every_call_sends_exactly_the_api_key_header_and_no_key_in_the_url()
    {
        var fake = new SlskdHandler()
            .Reply(HttpStatusCode.OK, """{ "server": { "isConnected": true, "isLoggedIn": true, "state": "Connected, LoggedIn" } }""")
            .Reply(HttpStatusCode.OK, SlskdJson.Search)
            .Reply(HttpStatusCode.OK, SlskdJson.Responses)
            .Reply(HttpStatusCode.NoContent);
        var client = Make(fake);

        await client.GetServerStateAsync(default);
        await client.GetSearchAsync(SearchId, default);
        await client.GetSearchResponsesAsync(SearchId, default);
        await client.DeleteSearchAsync(SearchId, default);

        Assert.Equal(4, fake.Sent.Count);
        foreach (var sent in fake.Sent)
        {
            Assert.Equal(Key, sent.Headers["X-API-Key"]);
            Assert.False(sent.Headers.ContainsKey("Authorization"));
            Assert.DoesNotContain(Key, sent.FullUri);
            Assert.DoesNotContain("api_key", sent.FullUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apikey", sent.FullUri, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Server_state_reads_login()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, """{ "server": { "isConnected": true, "isLoggedIn": false, "state": "Connected" } }""");
        var state = await Make(fake).GetServerStateAsync(default);
        Assert.Equal("/api/v0/application", fake.Sent[0].PathAndQuery);
        Assert.True(state.IsConnected);
        Assert.False(state.IsLoggedIn);
        Assert.Equal("Connected", state.State);
    }

    [Fact]
    public async Task Server_state_comes_from_application_which_any_key_may_read_not_from_the_admin_only_server_endpoint()
    {
        // Shape of slskd 0.26.2 GET /api/v0/application (Core/State.cs), trimmed. A readwrite key gets 403 from /api/v0/server.
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, """
            { "version": { "current": "0.26.2", "isUpdateAvailable": false }, "pendingReconnect": false, "pendingRestart": false,
              "server": { "address": "server.slsknet.org:2242", "state": "Connected, LoggedIn", "isConnected": true, "isLoggedIn": true, "isLoggingIn": false, "isTransitioning": false },
              "shares": { "ready": true } }
            """);
        var state = await Make(fake).GetServerStateAsync(default);
        Assert.Single(fake.Sent);
        Assert.Equal("/api/v0/application", fake.Sent[0].PathAndQuery);
        Assert.True(state.IsConnected);
        Assert.True(state.IsLoggedIn);
        Assert.Equal("Connected, LoggedIn", state.State);
    }

    [Fact]
    public async Task An_application_reply_without_a_server_part_is_a_request_failure()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, """{ "version": {} }""");
        await Assert.ThrowsAsync<SlskdRequestException>(() => Make(fake).GetServerStateAsync(default));
    }

    [Fact]
    public async Task Start_search_posts_the_fixed_options()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, SlskdJson.Search);
        var search = await Make(fake).StartSearchAsync(new SlskdSearchRequest(SearchId, "Radiohead Weird Fishes"), default);

        Assert.Equal("POST", fake.Sent[0].Method);
        Assert.Equal("/api/v0/searches", fake.Sent[0].PathAndQuery);
        var body = JsonDocument.Parse(fake.Sent[0].Body!).RootElement;
        Assert.Equal(SearchId, body.GetProperty("id").GetGuid());
        Assert.Equal("Radiohead Weird Fishes", body.GetProperty("searchText").GetString());
        Assert.Equal(8000, body.GetProperty("searchTimeout").GetInt32());
        Assert.Equal(100, body.GetProperty("responseLimit").GetInt32());
        Assert.Equal(10000, body.GetProperty("fileLimit").GetInt32());
        Assert.True(body.GetProperty("filterResponses").GetBoolean());
        Assert.Equal(1, body.GetProperty("minimumResponseFileCount").GetInt32());
        Assert.Equal(50, body.GetProperty("maximumPeerQueueLength").GetInt32());
        Assert.Equal(0, body.GetProperty("minimumPeerUploadSpeed").GetInt32());
        Assert.Equal("Completed, TimedOut", search.State);
        Assert.True(search.IsComplete);
        Assert.Equal(2, search.ResponseCount);
        Assert.Equal(3, search.FileCount);
        Assert.DoesNotContain("Radiohead", fake.Sent[0].FullUri);
    }

    [Fact]
    public async Task Get_search_is_null_on_404_and_asks_without_responses()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.NotFound);
        Assert.Null(await Make(fake).GetSearchAsync(SearchId, default));
        Assert.Equal($"/api/v0/searches/{SearchId:D}?includeResponses=false", fake.Sent[0].PathAndQuery);
    }

    [Fact]
    public async Task Responses_are_parsed_and_the_extension_comes_from_the_filename_when_empty()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, SlskdJson.Responses);
        var responses = await Make(fake).GetSearchResponsesAsync(SearchId, default);

        Assert.Equal($"/api/v0/searches/{SearchId:D}/responses", fake.Sent[0].PathAndQuery);
        var r = Assert.Single(responses);
        Assert.Equal("peer1", r.Username);
        Assert.True(r.HasFreeUploadSlot);
        Assert.Equal(1048576, r.UploadSpeed);
        Assert.Equal(0, r.QueueLength);
        Assert.Equal(2, r.Files.Count);
        var flac = r.Files[0];
        Assert.Equal("flac", flac.Extension);
        Assert.Equal(31457280, flac.Size);
        Assert.Equal(318, flac.Length);
        Assert.Equal(44100, flac.SampleRate);
        Assert.Equal(16, flac.BitDepth);
        Assert.Null(flac.BitRate);
        var mp3 = r.Files[1];
        Assert.Equal("mp3", mp3.Extension);
        Assert.Equal(320, mp3.BitRate);
        Assert.False(mp3.IsVariableBitRate);
    }

    [Theory]
    [InlineData(@"@@a\b\c.d\Song", "")]
    [InlineData(@"@@a\b\Song.Mp3", "mp3")]
    [InlineData("a/b/c.flac", "flac")]
    [InlineData("noext.", "")]
    public void Extension_of(string filename, string expected) => Assert.Equal(expected, SlskdClient.ExtensionOf(filename));

    [Fact]
    public async Task Cancel_and_delete_search_use_put_and_delete_and_accept_404()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.NotFound).Reply(HttpStatusCode.NotFound);
        var client = Make(fake);
        await client.CancelSearchAsync(SearchId, default);
        await client.DeleteSearchAsync(SearchId, default);
        Assert.Equal("PUT", fake.Sent[0].Method);
        Assert.Equal("DELETE", fake.Sent[1].Method);
        Assert.All(fake.Sent, s => Assert.Equal($"/api/v0/searches/{SearchId:D}", s.PathAndQuery));
    }

    [Fact]
    public async Task Enqueue_posts_a_one_file_batch_and_returns_the_transfer_id()
    {
        var batch = Guid.Parse("b7a00000-0000-0000-0000-000000000002");
        var fake = new SlskdHandler().Reply(HttpStatusCode.Created, SlskdJson.Enqueued);
        var result = await Make(fake).EnqueueAsync(
            new SlskdEnqueueRequest(batch, SearchId, "peer1", @"@@abc\Music\x\01 a.mp3", 5000000, $"encore/{batch}"), default);

        Assert.Equal(TransferId, result.TransferId);
        Assert.Empty(result.Failures);
        Assert.Equal("/api/v0/transfers/downloads/batches", fake.Sent[0].PathAndQuery);
        var body = JsonDocument.Parse(fake.Sent[0].Body!).RootElement;
        Assert.Equal(batch, body.GetProperty("id").GetGuid());
        Assert.Equal(SearchId, body.GetProperty("searchId").GetGuid());
        Assert.Equal("peer1", body.GetProperty("username").GetString());
        var file = Assert.Single(body.GetProperty("files").EnumerateArray());
        Assert.Equal(@"@@abc\Music\x\01 a.mp3", file.GetProperty("filename").GetString());
        Assert.Equal(5000000, file.GetProperty("size").GetInt64());
        Assert.Equal($"encore/{batch}", body.GetProperty("options").GetProperty("destination").GetString());
    }

    [Fact]
    public async Task Enqueue_that_all_failed_returns_the_reasons_and_no_id()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK,
            """{ "batch": { "transfers": [] }, "failures": [ { "filename": "f", "message": "Too many files" } ] }""");
        var result = await Make(fake).EnqueueAsync(Enqueue(), default);
        Assert.Null(result.TransferId);
        Assert.Equal(["Too many files"], result.Failures);
    }

    [Fact]
    public async Task Enqueue_404_means_peer_offline()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<SlskdPeerOfflineException>(() => Make(fake).EnqueueAsync(Enqueue(), default));
    }

    [Fact]
    public async Task Enqueue_429_waits_and_retries_then_gives_up_after_three_tries()
    {
        var ok = new SlskdHandler().Reply((HttpStatusCode)429).Reply((HttpStatusCode)429).Reply(HttpStatusCode.Created, SlskdJson.Enqueued);
        Assert.Equal(TransferId, (await Make(ok).EnqueueAsync(Enqueue(), default)).TransferId);
        Assert.Equal(3, ok.Sent.Count);

        var busy = new SlskdHandler().Reply((HttpStatusCode)429).Reply((HttpStatusCode)429).Reply((HttpStatusCode)429);
        await Assert.ThrowsAsync<SlskdBusyException>(() => Make(busy).EnqueueAsync(Enqueue(), default));
        Assert.Equal(3, busy.Sent.Count);
    }

    [Fact]
    public async Task Enqueue_is_not_retried_after_a_timeout()
    {
        var fake = new SlskdHandler().Hang();
        var e = await Assert.ThrowsAsync<SlskdRequestException>(() => Make(fake).EnqueueAsync(Enqueue(), default));
        Assert.Contains("timed out", e.Message);
        Assert.Single(fake.Sent);
    }

    [Fact]
    public async Task Start_search_429_and_409_have_their_own_exceptions_and_are_not_retried()
    {
        var busy = new SlskdHandler().Reply((HttpStatusCode)429);
        await Assert.ThrowsAsync<SlskdBusyException>(() => Make(busy).StartSearchAsync(new SlskdSearchRequest(SearchId, "x"), default));
        Assert.Single(busy.Sent);

        var down = new SlskdHandler().Reply(HttpStatusCode.Conflict);
        await Assert.ThrowsAsync<SlskdUnavailableException>(() => Make(down).StartSearchAsync(new SlskdSearchRequest(SearchId, "x"), default));
    }

    [Fact]
    public async Task Transfer_is_parsed_and_null_on_404()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.OK, SlskdJson.Transfer).Reply(HttpStatusCode.NotFound);
        var client = Make(fake);
        var t = await client.GetTransferAsync("peer1", TransferId, default);

        Assert.Equal($"/api/v0/transfers/downloads/peer1/{TransferId:D}", fake.Sent[0].PathAndQuery);
        Assert.NotNull(t);
        Assert.Equal("Completed, Succeeded", t.State);
        Assert.True(t.IsTerminal);
        Assert.True(t.IsSucceeded);
        Assert.False(t.IsQueued);
        Assert.Equal(5000000, t.BytesTransferred);
        Assert.Equal(524288.0, t.AverageSpeed);
        Assert.Null(t.PlaceInQueue);
        Assert.Equal(1, t.Attempts);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 22, 0, 0, TimeSpan.Zero), t.NextAttemptAt);

        Assert.Null(await client.GetTransferAsync("peer1", TransferId, default));
    }

    [Theory]
    [InlineData("Requested", false, true)]
    [InlineData("Queued, Remotely", false, true)]
    [InlineData("Initializing", false, true)]
    [InlineData("InProgress", false, false)]
    [InlineData("Completed, Errored", true, false)]
    [InlineData("Completed, Cancelled", true, false)]
    public void Transfer_state_helpers(string state, bool terminal, bool queued)
    {
        var t = new SlskdTransfer(TransferId, "u", "f", 1, state, 0, 0, null, 0, null);
        Assert.Equal(terminal, t.IsTerminal);
        Assert.Equal(queued, t.IsQueued);
        Assert.False(t.IsSucceeded);
    }

    [Fact]
    public async Task Username_is_escaped_in_the_path()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.NotFound);
        await Make(fake).GetTransferAsync("a b/c?d", TransferId, default);
        Assert.StartsWith("/api/v0/transfers/downloads/a%20b%2Fc%3Fd/", fake.Sent[0].PathAndQuery);
    }

    [Fact]
    public async Task Cancel_transfer_deletes_with_the_remove_flag_and_accepts_404()
    {
        var fake = new SlskdHandler().Reply(HttpStatusCode.NoContent).Reply(HttpStatusCode.NotFound);
        var client = Make(fake);
        await client.CancelTransferAsync("peer1", TransferId, remove: true, default);
        await client.CancelTransferAsync("peer1", TransferId, remove: false, default);
        Assert.Equal("DELETE", fake.Sent[0].Method);
        Assert.Equal($"/api/v0/transfers/downloads/peer1/{TransferId:D}?remove=true", fake.Sent[0].PathAndQuery);
        Assert.EndsWith("?remove=false", fake.Sent[1].PathAndQuery);
    }

    [Fact]
    public async Task Gets_retry_once_on_5xx_and_posts_do_not()
    {
        var get = new SlskdHandler().Reply(HttpStatusCode.BadGateway).Reply(HttpStatusCode.OK, """{ "server": { "isConnected": true, "isLoggedIn": true } }""");
        Assert.True((await Make(get).GetServerStateAsync(default)).IsLoggedIn);
        Assert.Equal(2, get.Sent.Count);

        var twice = new SlskdHandler().Reply(HttpStatusCode.InternalServerError).Reply(HttpStatusCode.InternalServerError);
        var e = await Assert.ThrowsAsync<SlskdRequestException>(() => Make(twice).GetServerStateAsync(default));
        Assert.Equal(500, e.StatusCode);

        var post = new SlskdHandler().Reply(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<SlskdRequestException>(() => Make(post).StartSearchAsync(new SlskdSearchRequest(SearchId, "x"), default));
        Assert.Single(post.Sent);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Auth_failures_are_not_retried(int status)
    {
        var fake = new SlskdHandler().Reply((HttpStatusCode)status);
        var e = await Assert.ThrowsAsync<SlskdAuthException>(() => Make(fake).GetServerStateAsync(default));
        Assert.Equal(status, e.StatusCode);
        Assert.Single(fake.Sent);
    }

    [Fact]
    public async Task Exception_text_never_holds_the_key_the_query_text_or_the_url()
    {
        var failures = new List<Func<Task>>();
        foreach (var status in new[] { 400, 401, 403, 404, 409, 429, 500 })
        {
            var fake = new SlskdHandler().Reply((HttpStatusCode)status, $"{{\"error\":\"{Key}\"}}").Reply((HttpStatusCode)status, $"{{\"error\":\"{Key}\"}}");
            failures.Add(() => Make(fake).StartSearchAsync(new SlskdSearchRequest(SearchId, "secret song"), default));
            failures.Add(() => Make(fake).EnqueueAsync(Enqueue(), default));
            failures.Add(() => Make(fake).GetServerStateAsync(default));
        }

        failures.Add(() => Make(new SlskdHandler().Fail(new HttpRequestException($"cannot reach http://gluetun-slskd:5030/?key={Key}", null, null))).GetServerStateAsync(default));
        failures.Add(() => Make(new SlskdHandler().Hang()).GetServerStateAsync(default));
        failures.Add(() => Make(new SlskdHandler().Reply(HttpStatusCode.OK, $"not json {Key}")).GetServerStateAsync(default));

        var seen = 0;
        foreach (var run in failures)
        {
            try
            {
                await run();
            }
            catch (Exception e)
            {
                seen++;
                var all = e.ToString();
                Assert.DoesNotContain(Key, all);
                Assert.DoesNotContain("secret song", all);
                Assert.DoesNotContain("gluetun-slskd", all);
                Assert.DoesNotContain("X-API-Key", all, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(seen >= 20, $"expected many failures, got {seen}");
    }

    [Fact]
    public async Task Cancelling_the_caller_token_is_not_wrapped()
    {
        using var cts = new CancellationTokenSource();
        var fake = new SlskdHandler().Hang();
        var task = Make(fake, FastOptions() with { Timeout = TimeSpan.FromSeconds(30) }).GetServerStateAsync(cts.Token);
        await Wait.Until(() => fake.Sent.Count == 1, "request sent");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Auth_handler_rejects_bad_keys_without_echoing_them()
    {
        var blank = Assert.Throws<ArgumentException>(() => new SlskdAuthHandler(" "));
        var space = Assert.Throws<ArgumentException>(() => new SlskdAuthHandler("abc def\r\nX-Evil: 1"));
        Assert.DoesNotContain("abc", space.Message);
        Assert.DoesNotContain("Evil", space.Message);
        Assert.NotNull(blank);
        Assert.DoesNotContain(Key, new SlskdAuthHandler(Key).ToString());
    }

    [Fact]
    public async Task Registered_client_sends_the_key_and_logs_never_show_it()
    {
        var logger = new CapturingLoggerProvider();
        var fake = new SlskdHandler()
            .Reply(HttpStatusCode.OK, """{ "server": { "isConnected": true, "isLoggedIn": true } }""")
            .Reply(HttpStatusCode.Unauthorized, $"{{\"error\":\"{Key}\"}}");
        var options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jf:8096"),
            JellyfinApiKey = "jellyfin-key-0123456789",
            AllowedUsers = [],
            DataDir = "/data",
            FetchMode = FetchMode.DryRun,
            SlskdUrl = new Uri("http://gluetun-slskd:5030"),
            SlskdApiKey = Key,
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logger));
        services.AddSlskdClient(options).ConfigurePrimaryHttpMessageHandler(() => fake);
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<ISlskdClient>();
        Assert.True((await client.GetServerStateAsync(default)).IsLoggedIn);
        await Assert.ThrowsAsync<SlskdAuthException>(() => client.GetServerStateAsync(default));

        Assert.Equal(Key, fake.Sent[0].Headers["X-API-Key"]);
        Assert.Equal("http://gluetun-slskd:5030/api/v0/application", fake.Sent[0].FullUri);
        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, l => l.Contains(Key));
        Assert.NotNull(provider.GetRequiredService<SearchBudget>());
    }

    [Fact]
    public void Add_slskd_client_needs_url_and_key()
    {
        var options = new EncoreOptions { JellyfinUrl = new Uri("http://jf:8096"), JellyfinApiKey = "k", AllowedUsers = [], DataDir = "/data" };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSlskdClient(options));
    }

    private static SlskdEnqueueRequest Enqueue() =>
        new(Guid.NewGuid(), SearchId, "peer1", @"@@abc\Music\x\01 a.mp3", 5000000, "encore/job");

    private sealed class CapturingLoggerProvider : ILoggerProvider
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
