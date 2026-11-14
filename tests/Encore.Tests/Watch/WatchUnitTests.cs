using Encore.Config;
using Encore.Tests.Jellyfin;
using Encore.Watch;

namespace Encore.Tests.Watch;

public sealed class SessionMessageWatchTests
{
    [Fact]
    public void Sessions_message_parses_to_session_dtos()
    {
        var message = SessionMessage.Parse($$"""{"MessageType":"Sessions","Data":{{JellyfinFixtures.Sessions}},"MessageId":"abc"}""");

        Assert.Equal(SessionMessageKind.Sessions, message.Kind);
        Assert.Equal(2, message.Sessions!.Count);
        Assert.Equal("Jellyfin Web", message.Sessions[0].Client);
        Assert.Equal(Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f"), message.Sessions[0].UserId);
        Assert.Equal("playlistItem0", message.Sessions[0].PlaylistItemId);
        Assert.Empty(message.Sessions[1].NowPlayingQueue!);
    }

    [Fact]
    public void A_missing_queue_stays_null()
    {
        var message = SessionMessage.Parse($$"""{"MessageType":"Sessions","Data":{{JellyfinFixtures.SessionWithoutQueue}}}""");

        Assert.Equal(SessionMessageKind.Sessions, message.Kind);
        Assert.Null(message.Sessions!.Single().NowPlayingQueue);
    }

    [Theory]
    [InlineData("""{"MessageType":"KeepAlive"}""", SessionMessageKind.KeepAlive, "KeepAlive")]
    [InlineData("""{"MessageType":"ForceKeepAlive","Data":60}""", SessionMessageKind.ForceKeepAlive, "ForceKeepAlive")]
    [InlineData("""{"messageType":"ForceKeepAlive"}""", SessionMessageKind.ForceKeepAlive, "ForceKeepAlive")]
    [InlineData("""{"MessageType":"LibraryChanged","Data":{}}""", SessionMessageKind.Other, "LibraryChanged")]
    [InlineData("""{"MessageType":"Sessions","Data":"0,1500"}""", SessionMessageKind.Invalid, "Sessions")]
    [InlineData("""{"MessageType":"Sessions","Data":[{"Id":5,"UserId":"not-a-guid"}]}""", SessionMessageKind.Invalid, "Sessions")]
    [InlineData("""{"Data":[]}""", SessionMessageKind.Invalid, "unparseable")]
    [InlineData("""[1,2,3]""", SessionMessageKind.Invalid, "unparseable")]
    [InlineData("""{"MessageType":"Sess""", SessionMessageKind.Invalid, "unparseable")]
    [InlineData("", SessionMessageKind.Invalid, "unparseable")]
    public void Message_kinds(string text, SessionMessageKind kind, string type)
    {
        var message = SessionMessage.Parse(text);

        Assert.Equal(kind, message.Kind);
        Assert.Equal(type, message.MessageType);
    }

    [Fact]
    public void A_very_long_message_type_is_trimmed_for_logging()
    {
        var message = SessionMessage.Parse($$"""{"MessageType":"{{new string('x', 500)}}"}""");

        Assert.Equal(SessionMessageKind.Other, message.Kind);
        Assert.Equal(64, message.MessageType.Length);
    }
}

public sealed class ReconnectBackoffWatchTests
{
    private static double[] Seconds(ReconnectBackoff backoff, int count) =>
        Enumerable.Range(0, count).Select(_ => backoff.Next(authFailed: false, TimeSpan.Zero).TotalSeconds).ToArray();

    [Fact]
    public void Default_sequence_is_1_2_5_10_30_then_60()
    {
        var backoff = new ReconnectBackoff(jitter: 0);

        Assert.Equal([1, 2, 5, 10, 30, 60, 60, 60], Seconds(backoff, 8));
    }

    [Fact]
    public void A_connection_up_for_60s_resets_the_sequence()
    {
        var backoff = new ReconnectBackoff(jitter: 0);
        Seconds(backoff, 4);

        Assert.Equal(1, backoff.Next(false, TimeSpan.FromSeconds(60)).TotalSeconds);
        Assert.Equal(2, backoff.Next(false, TimeSpan.Zero).TotalSeconds);
    }

    [Fact]
    public void A_short_connection_does_not_reset()
    {
        var backoff = new ReconnectBackoff(jitter: 0);
        Seconds(backoff, 3);

        Assert.Equal(10, backoff.Next(false, TimeSpan.FromSeconds(59)).TotalSeconds);
    }

    [Fact]
    public void Auth_failure_waits_the_cap()
    {
        var backoff = new ReconnectBackoff(jitter: 0);

        Assert.Equal(60, backoff.Next(authFailed: true, TimeSpan.Zero).TotalSeconds);
        Assert.Equal(60, backoff.Next(authFailed: false, TimeSpan.Zero).TotalSeconds);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.999999, 1.2)]
    public void Jitter_is_plus_or_minus_20_percent(double random, double factor)
    {
        var backoff = new ReconnectBackoff(random: () => random);

        Assert.Equal(factor, backoff.Next(false, TimeSpan.Zero).TotalSeconds, precision: 3);
    }
}

public sealed class WatcherSettingsWatchTests
{
    private static EncoreOptions Options(string url) => new()
    {
        JellyfinUrl = new Uri(url),
        JellyfinApiKey = "secret-api-key",
        AllowedUsers = ["nathan"],
        DataDir = "/data",
        DeviceId = "encore-0123456789ab",
        PollSeconds = 7,
    };

    [Theory]
    [InlineData("http://binhex-jellyfin:8096", "ws://binhex-jellyfin:8096/socket")]
    [InlineData("https://jf.example.org/jf", "wss://jf.example.org/jf/socket")]
    public void Socket_url_and_poll_interval_come_from_the_options(string url, string expected)
    {
        var settings = WatcherSettings.FromOptions(Options(url));

        Assert.Equal(new Uri(expected), settings.SocketUrl);
        Assert.Equal(TimeSpan.FromSeconds(7), settings.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.KeepAliveInterval);
    }

    [Fact]
    public void Authorization_is_the_MediaBrowser_header_and_never_printed()
    {
        var settings = WatcherSettings.FromOptions(Options("http://jf:8096"));

        Assert.StartsWith("MediaBrowser Token=\"secret-api-key\", Client=\"Encore\", Device=\"Encore\", DeviceId=\"encore-0123456789ab\"", settings.Authorization);
        Assert.DoesNotContain("secret-api-key", settings.ToString());
    }
}
