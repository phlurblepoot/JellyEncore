using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Encore.Config;
using Encore.Jellyfin;
using Encore.Retention;
using Encore.Store;
using Encore.Tests.EndToEnd;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Tests.Retention;

/// <summary>Shared setup: real stores in a temp folder, the fake Jellyfin, a manual clock and a radio folder with real files.</summary>
internal sealed class PassRig : IDisposable
{
    public static readonly Guid User = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");
    public static readonly Guid Ann = Guid.Parse("11111111000000000000000000000001");

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "encore-pass-" + Guid.NewGuid().ToString("N"));

    public string Radio => Path.Combine(Dir, "radio");

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero));

    public EncoreStore Store { get; }

    public FetchStore Fetch { get; }

    public RetentionStore Retention { get; }

    public FakeJellyfin Jellyfin { get; } = new() { Users = [new() { Id = Ann, Name = "ann" }] };

    public ListLogger<RetentionPass> PassLog { get; } = new();

    public ListLogger<RetentionSignals> SignalsLog { get; } = new();

    public RetentionPass Pass { get; }

    public RetentionMode Mode { get; }

    public PassRig(RetentionMode mode = RetentionMode.DryRun, IReadOnlyList<IPNetwork>? proxies = null)
    {
        Mode = mode;
        Directory.CreateDirectory(Radio);
        var path = Path.Combine(Dir, "encore.db");
        Store = EncoreStore.Open(path, Time);
        Fetch = FetchStore.Open(path);
        Retention = RetentionStore.Open(path);
        var options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jellyfin.test:8096"),
            JellyfinApiKey = "test-key-not-real",
            AllowedUsers = ["ann"],
            DataDir = Dir,
            RadioDir = Radio,
            RetentionMode = mode,
            TrustedProxies = proxies ?? [],
        };
        Pass = new RetentionPass(Fetch, Retention, new RetentionSignals(Jellyfin, Jellyfin, options, Time, SignalsLog), options, Time, PassLog,
            r => "/library/" + r.Artist + "/" + r.Title + ".flac");
    }

    /// <summary>A ready row with a real file under the radio folder.</summary>
    public (long Id, Guid Item, string Path) Ready(string title, double ageDays = 1, string artist = "Artist")
    {
        var item = Guid.NewGuid();
        var file = System.IO.Path.Combine(Radio, "soulseek", artist, "Album", title + ".flac");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "audio " + title);
        var at = Time.GetUtcNow() - TimeSpan.FromDays(ageDays);
        var id = Fetch.InsertFetched(new NewFetchedFile("job-" + title, "key-" + title, artist, title, "deezer_radio", "soulseek", "run-1", "s1", User, at));
        Fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: file, JellyfinItemId: item.ToString("N"), ReadyUtc: at));
        return (id, item, file);
    }

    public string Snapshot() => string.Join("|", Directory.EnumerateFiles(Radio, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    public void Dispose()
    {
        Retention.Dispose();
        Fetch.Dispose();
        Store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>RetentionPass and the report (v04 §8, §14 T127).</summary>
public sealed class RetentionPassTests : IDisposable
{
    private readonly PassRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task RP01_a_dry_run_writes_decisions_and_changes_no_file()
    {
        var old = _rig.Ready("Old", ageDays: 45);
        var young = _rig.Ready("Young", ageDays: 2);
        var fav = _rig.Ready("Fav", ageDays: 45);
        _rig.Jellyfin.Favorites[PassRig.Ann] = [new BaseItemDto { Id = fav.Item }];
        var before = _rig.Snapshot();

        var result = await _rig.Pass.RunAsync();

        Assert.Equal(before, _rig.Snapshot());
        Assert.True(result.DryRun);
        var rows = _rig.Retention.Decisions(result.PassId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.DryRun));
        Assert.Equal(("delete", "age"), (rows.Single(r => r.FetchedFileId == old.Id).Decision, rows.Single(r => r.FetchedFileId == old.Id).Reason));
        Assert.Equal("wait", rows.Single(r => r.FetchedFileId == young.Id).Decision);
        var promoted = rows.Single(r => r.FetchedFileId == fav.Id);
        Assert.Equal(("promote", "favorite", "/library/Artist/Fav.flac"), (promoted.Decision, promoted.Reason, promoted.TargetPath));
        Assert.Equal(45, promoted.AgeDays!.Value, 1);
    }

    [Fact]
    public async Task RP02_a_keep_signal_is_recorded_at_once_in_dry_run_and_stays_after_the_favorite_goes()
    {
        var song = _rig.Ready("Fav", ageDays: 45);
        _rig.Jellyfin.Favorites[PassRig.Ann] = [new BaseItemDto { Id = song.Item }];

        await _rig.Pass.RunAsync();

        var kept = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal("favorite", kept.KeptReason);
        Assert.NotNull(kept.KeptUtc);
        Assert.Single(_rig.PassLog.Named("retention.kept"));

        _rig.Jellyfin.Favorites.Clear();
        _rig.Time.Advance(TimeSpan.FromDays(1));
        var second = await _rig.Pass.RunAsync();

        var row = Assert.Single(second.Rows);
        Assert.Equal(("keep", "favorite"), (row.Decision, row.Reason));
        Assert.Single(_rig.PassLog.Named("retention.kept"));
    }

    [Fact]
    public async Task RP03_a_requested_song_is_kept_and_never_a_delete()
    {
        var song = _rig.Ready("Asked", ageDays: 99);
        _rig.Fetch.MarkRequested("key-Asked");

        var result = await _rig.Pass.RunAsync();

        Assert.Equal(("promote", "requested"), (result.Rows[0].Decision, result.Rows[0].Reason));
        Assert.Equal("requested", _rig.Fetch.GetTracked(song.Id)!.KeptReason);
        Assert.NotNull(_rig.Fetch.GetTracked(song.Id)!.KeptUtc);
    }

    [Fact]
    public async Task RP04_a_promotion_in_progress_resumes()
    {
        var song = _rig.Ready("Moving", ageDays: 99);
        _rig.Retention.StartPromotion(new NewPromotion(song.Id, song.Item.ToString("N"), _rig.Time.GetUtcNow()));

        var result = await _rig.Pass.RunAsync();

        Assert.Equal(("promote", "resume"), (result.Rows[0].Decision, result.Rows[0].Reason));
    }

    [Fact]
    public async Task RP05_a_failed_signal_read_makes_due_songs_skips_and_still_records_keeps()
    {
        var due = _rig.Ready("Due", ageDays: 45);
        var asked = _rig.Ready("Asked", ageDays: 45);
        _rig.Fetch.MarkRequested("key-Asked");
        _rig.Jellyfin.SmartLists = new SmartListsInfo(true, false, new HashSet<Guid>());

        var result = await _rig.Pass.RunAsync();

        Assert.Equal("skip", result.Rows.Single(r => r.Row.Id == due.Id).Decision);
        Assert.Equal("promote", result.Rows.Single(r => r.Row.Id == asked.Id).Decision);
        Assert.False(result.Signals.DeletesAllowed);
        Assert.Equal(1, result.Count("skip"));
        Assert.Equal(0, result.Count("delete"));
    }

    [Fact]
    public async Task RP06_untracked_audio_is_listed_logged_and_stored_and_left_on_disk()
    {
        _rig.Ready("Tracked");
        var stray = Path.Combine(_rig.Radio, "soulseek", "Nobody", "stray.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "x");

        var result = await _rig.Pass.RunAsync();

        Assert.Equal([stray.Replace('\\', '/')], result.Untracked);
        Assert.True(File.Exists(stray));
        Assert.Single(_rig.PassLog.Named("retention.untracked"));
        var line = Assert.Single(_rig.Retention.Decisions(result.PassId), d => d.Decision == "untracked");
        Assert.Equal(stray.Replace('\\', '/'), line.Path);
    }

    [Fact]
    public async Task RP07_a_deleted_rows_old_path_is_not_untracked_but_a_failed_rows_file_is()
    {
        var gone = _rig.Ready("Gone", ageDays: 1);
        _rig.Fetch.UpdateFetched(gone.Id, FetchState.Deleted);
        var failed = _rig.Ready("Failed", ageDays: 1);
        _rig.Fetch.UpdateFetched(failed.Id, FetchState.Failed);

        var result = await _rig.Pass.RunAsync();

        Assert.Equal([failed.Path.Replace('\\', '/')], result.Untracked);
    }

    [Fact]
    public async Task RP08_the_pass_logs_start_decisions_for_non_waits_and_one_summary()
    {
        _rig.Ready("Old", ageDays: 45);
        _rig.Ready("Young", ageDays: 2);

        var result = await _rig.Pass.RunAsync();

        Assert.Single(_rig.PassLog.Named("retention.pass_started"));
        var decision = Assert.Single(_rig.PassLog.Named("retention.decision"));
        Assert.Equal("delete", decision.Fields["Decision"]);
        Assert.Equal(8120, decision.EventId.Id);
        var done = Assert.Single(_rig.PassLog.Named("retention.pass_done"));
        Assert.Equal(8110, done.EventId.Id);
        Assert.Equal((1, 1, 0), (Convert.ToInt32(done.Fields["Delete"]), Convert.ToInt32(done.Fields["Wait"]), Convert.ToInt32(done.Fields["Promote"])));
        Assert.Equal("dry_run", done.Fields["Mode"]);
        Assert.Same(result, _rig.Pass.Latest);
    }

    [Fact]
    public void RP09_there_is_no_latest_result_before_the_first_pass()
    {
        Assert.Null(_rig.Pass.Latest);
    }

    [Fact]
    public async Task RP10_the_live_mode_marks_decisions_as_not_dry_run()
    {
        using var live = new PassRig(RetentionMode.Live);
        live.Ready("Old", ageDays: 45);

        var result = await live.Pass.RunAsync();

        Assert.False(result.DryRun);
        Assert.All(live.Retention.Decisions(result.PassId), d => Assert.False(d.DryRun));
        Assert.Equal("live", Assert.Single(live.PassLog.Named("retention.pass_done")).Fields["Mode"]);
    }

    // ---- the report ----

    [Fact]
    public async Task RR01_the_json_model_has_counts_songs_playlists_and_untracked()
    {
        var old = _rig.Ready("Old", ageDays: 45);
        var inList = _rig.Ready("Listed", ageDays: 5);
        var playlist = Guid.Parse("ddddddd100000000000000000000000d");
        var smart = Guid.Parse("ddddddd200000000000000000000000d");
        _rig.Jellyfin.Playlists[playlist] = new PlaylistRef(playlist, "Road trip");
        _rig.Jellyfin.PlaylistViewers[playlist] = [PassRig.Ann];
        _rig.Jellyfin.PlaylistEntries[playlist] = [inList.Item];
        _rig.Jellyfin.Playlists[smart] = new PlaylistRef(smart, "Recent [Smart]");
        _rig.Jellyfin.PlaylistViewers[smart] = [PassRig.Ann];
        var stray = Path.Combine(_rig.Radio, "soulseek", "x", "stray.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "x");

        var model = RetentionReport.Build(await _rig.Pass.RunAsync());

        Assert.True(model.DryRun);
        Assert.Equal("dry_run", model.Mode);
        Assert.Equal((1, 1, 0, 1), (model.Counts["delete"], model.Counts["promote"], model.Counts["wait"], model.Counts["untracked"]));
        Assert.Equal("playlist", model.Songs.Single(s => s.Id == inList.Id).Reason);
        Assert.Equal("/library/Artist/Listed.flac", model.Songs.Single(s => s.Id == inList.Id).TargetPath);
        Assert.Equal("age", model.Songs.Single(s => s.Id == old.Id).Reason);
        Assert.Equal("Road trip", Assert.Single(model.PlaylistsCounted).Name);
        var skipped = Assert.Single(model.PlaylistsSkipped);
        Assert.Equal((smart.ToString("N"), PlaylistSkipReason.SmartSuffix), (skipped.Id, skipped.Why));
        Assert.False(model.DeletesSkipped);
        Assert.Single(model.Untracked);
    }

    [Fact]
    public async Task RR02_the_text_report_groups_by_decision_and_ends_with_untracked_and_playlists()
    {
        _rig.Ready("Old", ageDays: 45, artist: "Band");
        var fav = _rig.Ready("Fav", ageDays: 10, artist: "Duo");
        _rig.Ready("Young", ageDays: 2, artist: "Solo");
        _rig.Jellyfin.Favorites[PassRig.Ann] = [new BaseItemDto { Id = fav.Item }];
        var smart = Guid.Parse("ddddddd200000000000000000000000d");
        _rig.Jellyfin.Playlists[smart] = new PlaylistRef(smart, "Fresh [Smart]");
        _rig.Jellyfin.PlaylistViewers[smart] = [PassRig.Ann];
        var plain = Guid.Parse("ddddddd100000000000000000000000d");
        _rig.Jellyfin.Playlists[plain] = new PlaylistRef(plain, "Mine");
        _rig.Jellyfin.PlaylistViewers[plain] = [PassRig.Ann];
        _rig.Jellyfin.PlaylistEntries[plain] = [Guid.NewGuid(), Guid.NewGuid()];

        var text = RetentionReport.ToText(RetentionReport.Build(await _rig.Pass.RunAsync()));

        Assert.Contains("dry run: nothing is deleted or moved", text);
        Assert.Contains("PROMOTE (1)\n  Fav - Duo | age 10.0 d | uploads 0 | plays 0 | kept: favorite | to /library/Duo/Fav.flac\n", text);
        Assert.Contains("DELETE (1)\n  Old - Band | age 45.0 d | uploads 0 | plays 0 | delete: age\n", text);
        Assert.Contains("WAIT (1)\n  Young - Solo | age 2.0 d | uploads 0 | plays 0 | 28.0 days, 3 uploads, 3 plays left\n", text);
        Assert.Contains("UNTRACKED (0)\n", text);
        Assert.Contains("PLAYLISTS COUNTED (1)\n  Mine (" + plain.ToString("N") + ") | 2 songs\n", text);
        Assert.Contains("PLAYLISTS SKIPPED (1)\n  Fresh [Smart] (" + smart.ToString("N") + ") | name ends with [Smart]\n", text);
        Assert.True(text.IndexOf("PROMOTE", StringComparison.Ordinal) < text.IndexOf("DELETE", StringComparison.Ordinal));
        Assert.True(text.IndexOf("WAIT", StringComparison.Ordinal) < text.IndexOf("UNTRACKED", StringComparison.Ordinal));
        Assert.True(text.IndexOf("UNTRACKED", StringComparison.Ordinal) < text.IndexOf("PLAYLISTS COUNTED", StringComparison.Ordinal));
        Assert.True(text.IndexOf("PLAYLISTS COUNTED", StringComparison.Ordinal) < text.IndexOf("PLAYLISTS SKIPPED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RR03_the_text_report_says_when_deletes_were_skipped()
    {
        _rig.Ready("Due", ageDays: 45);
        _rig.Jellyfin.SmartLists = new SmartListsInfo(true, false, new HashSet<Guid>());

        var text = RetentionReport.ToText(RetentionReport.Build(await _rig.Pass.RunAsync()));

        Assert.Contains("DELETES SKIPPED this pass", text);
        Assert.Contains("SmartLists installed but its list is UNREADABLE", text);
        Assert.Contains("SKIP (1)", text);
        Assert.Contains("would delete: age (skipped, signals failed)", text);
    }

    // ---- the endpoint ----

    private static async Task<(WebApplication App, HttpClient Client)> Serve(PassRig rig, IPAddress? peer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(rig.Pass);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = peer;
            return next(context);
        });
        RetentionReportEndpoint.Map(app);
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task EP01_a_lan_client_gets_json_and_text()
    {
        _rig.Ready("Old", ageDays: 45);
        await _rig.Pass.RunAsync();
        var (app, client) = await Serve(_rig, IPAddress.Parse("192.168.86.20"));
        await using var _ = app;

        var json = await client.GetAsync("/retention/report");
        using var doc = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Equal("dry_run", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("counts").GetProperty("delete").GetInt32());
        Assert.Equal("Old", doc.RootElement.GetProperty("songs")[0].GetProperty("title").GetString());

        var text = await client.GetAsync("/retention/report?format=text");
        Assert.Equal("text/plain", text.Content.Headers.ContentType!.MediaType);
        Assert.Contains("DELETE (1)", await text.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EP02_before_the_first_pass_the_report_says_so()
    {
        var (app, client) = await Serve(_rig, IPAddress.Loopback);
        await using var _ = app;

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/retention/report"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("passId").ValueKind);
        Assert.Contains("No retention pass", await client.GetStringAsync("/retention/report?format=text"));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    public async Task EP03_a_public_address_gets_404(string peer)
    {
        await _rig.Pass.RunAsync();
        var (app, client) = await Serve(_rig, IPAddress.Parse(peer));
        await using var _ = app;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/retention/report")).StatusCode);
    }

    [Fact]
    public async Task EP04_a_trusted_proxy_peer_never_reaches_the_report_even_on_the_lan()
    {
        using var rig = new PassRig(proxies: [IPNetwork.Parse("172.18.0.0/16")]);
        await rig.Pass.RunAsync();
        var (app, client) = await Serve(rig, IPAddress.Parse("172.18.0.5"));
        await using var _ = app;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/retention/report")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/retention/report?format=text")).StatusCode);
    }

    [Fact]
    public async Task EP05_a_forwarding_header_means_a_proxy_and_gets_404()
    {
        await _rig.Pass.RunAsync();
        var (app, client) = await Serve(_rig, IPAddress.Parse("10.0.0.9"));
        await using var _ = app;
        var request = new HttpRequestMessage(HttpMethod.Get, "/retention/report");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task EP06_a_request_the_requests_gate_marked_as_proxied_gets_404()
    {
        await _rig.Pass.RunAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_rig.Pass);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.2");
            context.Items[Encore.Requests.Api.RequestsApi.ProxiedItem] = true;
            return next(context);
        });
        RetentionReportEndpoint.Map(app);
        await app.StartAsync();
        await using var _ = app;

        Assert.Equal(HttpStatusCode.NotFound, (await app.GetTestClient().GetAsync("/retention/report")).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.86.50", true)]
    [InlineData("100.64.1.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("169.254.1.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void LAN01_the_private_ranges(string address, bool expected)
    {
        Assert.Equal(expected, RetentionReportEndpoint.IsPrivate(IPAddress.Parse(address)));
    }
}
