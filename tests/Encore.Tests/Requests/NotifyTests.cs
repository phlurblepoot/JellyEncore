using System.Net;
using System.Text.Json;
using Encore.Jellyfin;
using Encore.Requests.Auth;
using Encore.Tests.Jellyfin;
using Encore.Requests.Notify;
using Encore.Requests.Songs;
using Encore.Store;
using Encore.Tests.EndToEnd;
using Encore.Tests.Watch;

namespace Encore.Tests.Requests;

/// <summary>v03 section 15, T88: the request notifier (who is told, the body, once per state, one retry on a 5xx, no session open).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class NotifyTests
{
    private static readonly Guid OwnedItem = Guid.Parse("33333333333333333333333333333333");

    private static SessionInfoDto Session(string id, Guid user, bool remote = true) =>
        new() { Id = id, UserId = user, SupportsRemoteControl = remote, Client = "Finamp", DeviceId = "dev-" + id };

    private static JellyfinRequestException Http(int status) => new("SendMessage", status);

    private static JellyfinClient ClientFor(ScriptedHandler script, string baseUrl = "http://jellyfin.test:8096/jf/")
    {
        var http = new HttpClient(new JellyfinAuthHandler(JellyfinAuth.BuildParameter("KEY0123456789abcdef", "encore-0123456789ab", "0.3.0")) { InnerHandler = script })
        {
            BaseAddress = new Uri(baseUrl),
        };
        return new JellyfinClient(http, new JellyfinClientOptions { RetryDelays = [TimeSpan.Zero], Jitter = 0 });
    }

    private sealed class Setup : IDisposable
    {
        public SongRig Rig { get; } = new();

        public FakeJellyfin Jellyfin { get; } = new();

        public ListLogger<RequestNotifier> Log { get; } = new();

        public List<TimeSpan> Delays { get; } = [];

        public RequestNotifier Notifier { get; }

        public Setup() =>
            Notifier = new RequestNotifier(Jellyfin, Rig.Store, Log, Rig.Clock, delay: (span, _) =>
            {
                Delays.Add(span);
                return Task.CompletedTask;
            });

        public async Task<RequestRow> ReadyRequestAsync(RequestUser? user = null)
        {
            Rig.Track(1, "Daft Punk", "One More Time");
            Rig.Own("Daft Punk", "One More Time", OwnedItem);
            return await Rig.CreateAsync(1, user);
        }

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public async Task N01_Only_the_requesters_remote_controllable_sessions_get_the_message()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync(SongRig.Kim);
        s.Jellyfin.Sessions =
        [
            Session("kim-phone", SongRig.Kim.Id),
            Session("kim-tv", SongRig.Kim.Id, remote: false),
            Session("lee-phone", SongRig.Lee.Id),
            Session("kim-web", SongRig.Kim.Id),
        ];

        await s.Notifier.NotifyAsync(row, CancellationToken.None);

        Assert.Equal(["kim-phone", "kim-web"], s.Jellyfin.Messages.Select(m => m.SessionId).ToArray());
        var message = s.Jellyfin.Messages.First();
        Assert.Equal(("Encore", "Ready: Daft Punk - One More Time", 10_000), (message.Header, message.Text, message.TimeoutMs));
        Assert.Equal(s.Rig.Clock.GetUtcNow(), s.Rig.Row(row.Id).NotifiedUtc);
        Assert.Single(s.Log.Named("notify.sent"));
    }

    [Fact]
    public async Task N02_Message_body_has_Header_Text_and_TimeoutMs_on_the_session_message_route()
    {
        var script = new ScriptedHandler().Reply(HttpStatusCode.NoContent);
        var client = ClientFor(script);

        await client.SendMessageAsync("e3a7f0c2", "Encore", "Ready: A - B \"quoted\" é", 10_000);

        var sent = Assert.Single(script.Sent);
        Assert.Equal(("POST", "/jf/Sessions/e3a7f0c2/Message"), (sent.Method, sent.PathAndQuery));
        Assert.StartsWith("application/json", sent.ContentType);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("Encore", body.RootElement.GetProperty("Header").GetString());
        Assert.Equal("Ready: A - B \"quoted\" é", body.RootElement.GetProperty("Text").GetString());
        Assert.Equal(10_000, body.RootElement.GetProperty("TimeoutMs").GetInt32());
    }

    [Fact]
    public async Task N03_SendMessage_is_not_retried_by_the_client_and_a_5xx_carries_its_status()
    {
        var script = new ScriptedHandler().Reply(HttpStatusCode.BadGateway).Reply(HttpStatusCode.NoContent);
        var client = ClientFor(script, "http://jellyfin.test:8096/");

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(() => client.SendMessageAsync("abc", "Encore", "x", 10_000));

        Assert.Equal(502, ex.StatusCode);
        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task N04_One_message_per_request_per_state()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        s.Jellyfin.Sessions = [Session("kim-phone", SongRig.Kim.Id)];

        await s.Notifier.NotifyAsync(row, CancellationToken.None);
        await s.Notifier.NotifyAsync(row, CancellationToken.None);
        await s.Notifier.NotifyAsync(s.Rig.Row(row.Id), CancellationToken.None);

        Assert.Single(s.Jellyfin.Messages);
    }

    [Fact]
    public async Task N05_Failed_sends_its_own_message_and_a_later_ready_after_retry_sends_another()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        s.Jellyfin.Sessions = [Session("kim-phone", SongRig.Kim.Id)];
        await s.Notifier.NotifyAsync(row, CancellationToken.None);

        s.Rig.Clock.Advance(TimeSpan.FromMinutes(1));
        s.Rig.Store.Update(row.Id, RequestState.Failed, s.Rig.Clock.GetUtcNow(), new RequestUpdate(FailReason: "no_results"));
        await s.Notifier.NotifyAsync(row, CancellationToken.None);

        s.Rig.Clock.Advance(TimeSpan.FromMinutes(1));
        s.Rig.Store.Update(row.Id, RequestState.Ready, s.Rig.Clock.GetUtcNow());
        await s.Notifier.NotifyAsync(row, CancellationToken.None);

        Assert.Equal(["Ready: Daft Punk - One More Time", "Could not get: Daft Punk - One More Time", "Ready: Daft Punk - One More Time"],
            s.Jellyfin.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task N06_A_5xx_is_tried_once_more_after_30_seconds_and_then_counts_as_sent()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        s.Jellyfin.Sessions = [Session("kim-phone", SongRig.Kim.Id)];
        s.Jellyfin.MessageFailures.Enqueue(Http(503));

        await s.Notifier.NotifyAsync(row, CancellationToken.None);
        await s.Notifier.IdleAsync();

        Assert.Equal(2, s.Jellyfin.Messages.Count);
        Assert.Equal([TimeSpan.FromSeconds(30)], s.Delays);
        Assert.NotNull(s.Rig.Row(row.Id).NotifiedUtc);
        Assert.Empty(s.Log.Named("notify.failed"));
        Assert.Single(s.Log.Named("notify.sent"));
    }

    [Fact]
    public async Task N07_A_second_5xx_logs_notify_failed_and_leaves_the_request_alone()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        var before = s.Rig.Row(row.Id);
        s.Jellyfin.Sessions = [Session("kim-phone", SongRig.Kim.Id)];
        s.Jellyfin.MessageFailures.Enqueue(Http(500));
        s.Jellyfin.MessageFailures.Enqueue(Http(500));

        await s.Notifier.NotifyAsync(row, CancellationToken.None);
        await s.Notifier.IdleAsync();

        Assert.Equal(2, s.Jellyfin.Messages.Count);
        Assert.Single(s.Log.Named("notify.failed"));
        Assert.Equal(before, s.Rig.Row(row.Id));
    }

    [Fact]
    public async Task N08_A_non_5xx_error_is_not_retried_and_does_not_throw()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        s.Jellyfin.Sessions = [Session("kim-phone", SongRig.Kim.Id)];
        s.Jellyfin.MessageFailures.Enqueue(Http(404));

        await s.Notifier.NotifyAsync(row, CancellationToken.None);
        await s.Notifier.IdleAsync();

        Assert.Single(s.Jellyfin.Messages);
        Assert.Single(s.Log.Named("notify.failed"));
        Assert.Null(s.Rig.Row(row.Id).NotifiedUtc);
    }

    [Fact]
    public async Task N09_No_session_open_sends_nothing_and_changes_nothing()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        var before = s.Rig.Row(row.Id);
        s.Jellyfin.Sessions = [Session("lee-phone", SongRig.Lee.Id), Session("kim-tv", SongRig.Kim.Id, remote: false)];

        await s.Notifier.NotifyAsync(row, CancellationToken.None);

        Assert.Empty(s.Jellyfin.Messages);
        Assert.Empty(s.Log.Entries);
        Assert.Equal(before, s.Rig.Row(row.Id));
    }

    [Fact]
    public async Task N10_A_request_that_is_still_active_is_not_announced()
    {
        using var s = new Setup();
        s.Rig.Track(2, "Air", "Sexy Boy");
        var row = await s.Rig.CreateAsync(2);
        Assert.Equal(RequestState.Queued, row.State);
        s.Jellyfin.Sessions = [Session("kim-phone", SongRig.Kim.Id)];

        await s.Notifier.NotifyAsync(row, CancellationToken.None);

        Assert.Empty(s.Jellyfin.Messages);
    }

    [Fact]
    public async Task N11_A_Jellyfin_error_listing_sessions_is_logged_not_thrown()
    {
        using var s = new Setup();
        var row = await s.ReadyRequestAsync();
        var script = new ScriptedHandler().Reply(HttpStatusCode.InternalServerError).Reply(HttpStatusCode.InternalServerError);
        var failing = new RequestNotifier(ClientFor(script), s.Rig.Store, s.Log, s.Rig.Clock);

        await failing.NotifyAsync(row, CancellationToken.None);

        Assert.Single(s.Log.Named("notify.failed"));
    }
}
