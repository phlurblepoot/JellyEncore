using System.Net;
using Encore.Lidarr;

namespace Encore.Tests.Lidarr;

public class LidarrRetentionTests
{
    private const string Key = "LidarrKey0123456789abcdefAB";

    private static LidarrClient Make(FakeLidarr fake)
    {
        var o = new LidarrClientOptions { Timeout = TimeSpan.FromMilliseconds(300), GetRetryDelay = TimeSpan.FromMilliseconds(1) };
        var http = new HttpClient(new LidarrAuthHandler(Key) { InnerHandler = fake })
        {
            BaseAddress = new Uri("http://lidarr:8686/"),
            Timeout = o.Timeout,
        };
        return new LidarrClient(http, o);
    }

    [Fact]
    public async Task Commands_give_name_and_status_and_running_means_queued_or_started()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, """
            [{"id":812,"name":"RescanFolders","commandName":"Rescan Folders","status":"started","queued":"2026-11-16T20:00:00Z","trigger":"scheduled","body":{}},
             {"id":813,"name":"RenameArtist","status":"queued","body":{}},
             {"id":814,"name":"RefreshMonitoredDownloads","status":"completed"},
             {"id":815,"status":"started"}]
            """);
        var commands = await Make(fake).GetCommandsAsync(default);

        Assert.Equal("/api/v1/command", fake.Sent[0].PathAndQuery);
        Assert.Equal(Key, fake.Sent[0].Headers["X-Api-Key"]);
        Assert.Equal(3, commands.Count);
        Assert.Equal(new LidarrCommand(812, "RescanFolders", "started"), commands[0]);
        Assert.True(commands[0].IsRunning);
        Assert.True(commands[1].IsRunning);
        Assert.False(commands[2].IsRunning);
    }

    [Theory]
    [InlineData("no", false)]
    [InlineData("newFiles", false)]
    [InlineData("allFiles", true)]
    [InlineData("sync", true)]
    public async Task Write_audio_tags_values(string value, bool retags)
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, $$"""{"id":1,"writeAudioTags":"{{value}}","scrubAudioTags":false}""");
        var got = await Make(fake).GetWriteAudioTagsAsync(default);

        Assert.Equal("/api/v1/config/metadataprovider", fake.Sent[0].PathAndQuery);
        Assert.Equal(value, got);
        Assert.Equal(retags, LidarrWriteAudioTags.RetagsExistingFiles(got));
    }

    [Fact]
    public async Task Missing_writeAudioTags_is_an_error_not_a_guess()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, """{"id":1}""");
        await Assert.ThrowsAsync<LidarrRequestException>(() => Make(fake).GetWriteAudioTagsAsync(default));
    }

    [Fact]
    public async Task Keys_are_absent_from_urls_and_from_errors()
    {
        foreach (var status in new[] { 400, 401, 403, 404, 500 })
        {
            foreach (var call in new Func<LidarrClient, Task>[] { c => c.GetCommandsAsync(default), c => c.GetWriteAudioTagsAsync(default) })
            {
                var fake = new FakeLidarr().Reply((HttpStatusCode)status, $"{{\"error\":\"{Key}\"}}").Reply((HttpStatusCode)status, $"{{\"error\":\"{Key}\"}}");
                var e = await Assert.ThrowsAnyAsync<Exception>(() => call(Make(fake)));
                Assert.DoesNotContain(Key, e.ToString());
                Assert.DoesNotContain("lidarr:8686", e.ToString());
                Assert.All(fake.Sent, s => Assert.DoesNotContain(Key, s.FullUri));
            }
        }
    }
}
