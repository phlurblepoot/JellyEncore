using Encore.Config;
using Encore.Logging;
using Encore.Retention;

namespace Encore.Tests.Config;

/// <summary>v0.4 retention settings (v04 design section 10, T121 cases).</summary>
public sealed class ConfigRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-ret-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private Dictionary<string, string?> Valid() => new()
    {
        ["JELLYFIN_URL"] = "http://jellyfin:8096",
        ["JELLYFIN_API_KEY"] = "JFKEY0123456789abcdef",
        ["ENCORE_ALLOWED_USERS"] = "nathan",
        ["ENCORE_DATA_DIR"] = Path.Combine(_root, "data"),
    };

    /// <summary>Fetch live with real folders, ready for a retention mode.</summary>
    private Dictionary<string, string?> Live(string retention, out string library)
    {
        var downloads = Path.Combine(_root, "downloads");
        var radio = Path.Combine(_root, "radio");
        library = Path.Combine(_root, "library");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(radio);
        Directory.CreateDirectory(library);
        var env = Valid();
        env["ENCORE_FETCH_MODE"] = "live";
        env["ENCORE_DRY_RUN"] = "false";
        env["ENCORE_SLSKD_URL"] = "http://gluetun-slskd:5030";
        env["ENCORE_SLSKD_API_KEY"] = "SLSKDSECRET0123456789xyz";
        env["ENCORE_SLSKD_DOWNLOADS_DIR"] = downloads;
        env["ENCORE_RADIO_DIR"] = radio;
        env["ENCORE_RETENTION_MODE"] = retention;
        env["ENCORE_LIBRARY_DIR"] = library;
        return env;
    }

    [Fact]
    public void RC01_defaults()
    {
        var result = EncoreOptionsLoader.Load(Valid());
        var o = result.Options!;
        Assert.Equal(RetentionMode.Off, o.RetentionMode);
        Assert.Equal(30, o.RetentionDays);
        Assert.Equal(3, o.RetentionUploads);
        Assert.Equal(3, o.RetentionPlays);
        Assert.Equal(4, o.RetentionHour);
        Assert.Equal(20, o.RetentionMaxPromotions);
        Assert.Equal(100, o.RetentionMaxDeletes);
        Assert.Empty(o.RetentionIgnorePlaylists);
        Assert.Equal("/library", o.LibraryDir);
        Assert.Equal("/media/music", o.LibraryJellyfinDir);
        Assert.Equal("/music-radio", o.SlskdRadioDir);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("off", RetentionMode.Off)]
    [InlineData("OFF", RetentionMode.Off)]
    [InlineData("dry_run", RetentionMode.DryRun)]
    [InlineData("dry-run", RetentionMode.DryRun)]
    [InlineData("DryRun", RetentionMode.DryRun)]
    [InlineData("live", RetentionMode.Live)]
    public void RC02_mode_spellings(string text, RetentionMode expected)
    {
        var env = Live(text, out _);
        Assert.Equal(expected, EncoreOptionsLoader.Load(env).Options!.RetentionMode);
    }

    [Fact]
    public void RC02_unknown_mode_is_an_error_naming_the_values()
    {
        var error = Assert.Single(EncoreOptionsLoader.Load(Live("on", out _)).Errors);
        Assert.Equal("ENCORE_RETENTION_MODE", error.Variable);
        Assert.Contains("off, dry_run or live", error.Message);
    }

    [Theory]
    [InlineData("ENCORE_RETENTION_DAYS", "1", true)]
    [InlineData("ENCORE_RETENTION_DAYS", "365", true)]
    [InlineData("ENCORE_RETENTION_DAYS", "0", false)]
    [InlineData("ENCORE_RETENTION_DAYS", "366", false)]
    [InlineData("ENCORE_RETENTION_UPLOADS", "1", true)]
    [InlineData("ENCORE_RETENTION_UPLOADS", "1000", true)]
    [InlineData("ENCORE_RETENTION_UPLOADS", "0", false)]
    [InlineData("ENCORE_RETENTION_UPLOADS", "1001", false)]
    [InlineData("ENCORE_RETENTION_PLAYS", "1", true)]
    [InlineData("ENCORE_RETENTION_PLAYS", "50", true)]
    [InlineData("ENCORE_RETENTION_PLAYS", "0", false)]
    [InlineData("ENCORE_RETENTION_PLAYS", "51", false)]
    [InlineData("ENCORE_RETENTION_HOUR", "0", true)]
    [InlineData("ENCORE_RETENTION_HOUR", "23", true)]
    [InlineData("ENCORE_RETENTION_HOUR", "24", false)]
    [InlineData("ENCORE_RETENTION_HOUR", "-1", false)]
    [InlineData("ENCORE_RETENTION_MAX_PROMOTIONS", "0", true)]
    [InlineData("ENCORE_RETENTION_MAX_PROMOTIONS", "500", true)]
    [InlineData("ENCORE_RETENTION_MAX_PROMOTIONS", "501", false)]
    [InlineData("ENCORE_RETENTION_MAX_DELETES", "0", true)]
    [InlineData("ENCORE_RETENTION_MAX_DELETES", "5000", true)]
    [InlineData("ENCORE_RETENTION_MAX_DELETES", "5001", false)]
    [InlineData("ENCORE_RETENTION_MAX_DELETES", "abc", false)]
    public void RC03_ranges(string name, string value, bool ok)
    {
        var env = Valid();
        env[name] = value;
        var result = EncoreOptionsLoader.Load(env);
        if (ok)
        {
            Assert.True(result.IsValid);
        }
        else
        {
            var error = Assert.Single(result.Errors);
            Assert.Equal(name, error.Variable);
        }
    }

    [Fact]
    public void RC03_values_are_read()
    {
        var env = Valid();
        env["ENCORE_RETENTION_DAYS"] = "45";
        env["ENCORE_RETENTION_UPLOADS"] = "7";
        env["ENCORE_RETENTION_PLAYS"] = "5";
        env["ENCORE_RETENTION_HOUR"] = "2";
        env["ENCORE_RETENTION_MAX_PROMOTIONS"] = "1";
        env["ENCORE_RETENTION_MAX_DELETES"] = "9";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal([45, 7, 5, 2, 1, 9],
            [o.RetentionDays, o.RetentionUploads, o.RetentionPlays, o.RetentionHour, o.RetentionMaxPromotions, o.RetentionMaxDeletes]);
    }

    [Theory]
    [InlineData("dry_run")]
    [InlineData("live")]
    public void RC04_not_off_without_fetch_live_is_an_error(string retention)
    {
        foreach (var fetch in new[] { null, "dry_run" })
        {
            var env = Valid();
            env["ENCORE_RETENTION_MODE"] = retention;
            if (fetch is not null)
            {
                env["ENCORE_FETCH_MODE"] = fetch;
                env["ENCORE_SLSKD_URL"] = "http://gluetun-slskd:5030";
                env["ENCORE_SLSKD_API_KEY"] = "SLSKDSECRET0123456789xyz";
            }

            var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
            Assert.Equal("ENCORE_RETENTION_MODE", error.Variable);
            Assert.Contains("ENCORE_FETCH_MODE", error.Message);
        }
    }

    [Fact]
    public void RC04_off_needs_nothing()
    {
        var env = Valid();
        env["ENCORE_RETENTION_MODE"] = "off";
        Assert.True(EncoreOptionsLoader.Load(env).IsValid);
    }

    [Fact]
    public void RC05_live_probe_writes_and_deletes_in_the_hidden_incoming_folder()
    {
        var env = Live("live", out var library);
        var result = EncoreOptionsLoader.Load(env);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        var incoming = Path.Combine(library, RetentionLimits.IncomingFolder);
        Assert.True(Directory.Exists(incoming));
        Assert.Empty(Directory.GetFileSystemEntries(incoming));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RC05_live_with_missing_library_is_an_error()
    {
        var env = Live("live", out var library);
        Directory.Delete(library);
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_LIBRARY_DIR", error.Variable);
    }

    [Fact]
    public void RC05_live_probe_failure_is_an_error()
    {
        var env = Live("live", out var library);
        // A file where the hidden folder must be makes the probe fail on every OS.
        File.WriteAllText(Path.Combine(library, RetentionLimits.IncomingFolder), "x");
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_LIBRARY_DIR", error.Variable);
        Assert.Contains("writable", error.Message);
    }

    [Fact]
    public void RC05_dry_run_missing_library_is_a_warning_and_writes_nothing()
    {
        var env = Live("dry_run", out var library);
        var result = EncoreOptionsLoader.Load(env);
        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(Path.Combine(library, RetentionLimits.IncomingFolder)));

        Directory.Delete(library);
        result = EncoreOptionsLoader.Load(env);
        Assert.True(result.IsValid);
        Assert.Equal("ENCORE_LIBRARY_DIR", Assert.Single(result.Warnings).Variable);
    }

    [Fact]
    public void RC06_ignore_list_format()
    {
        var a = "0123456789ABCDEF0123456789abcdef";
        var b = "11111111-2222-3333-4444-555555555555";
        var env = Valid();
        env["ENCORE_RETENTION_IGNORE_PLAYLISTS"] = $" {a} , {b},{a.ToLowerInvariant()},";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal(["0123456789abcdef0123456789abcdef", "11111111222233334444555555555555"], o.RetentionIgnorePlaylists);
    }

    [Theory]
    [InlineData("nothex")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void RC06_bad_ids_are_errors(string id)
    {
        var env = Valid();
        env["ENCORE_RETENTION_IGNORE_PLAYLISTS"] = "0123456789abcdef0123456789abcdef," + id;
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_RETENTION_IGNORE_PLAYLISTS", error.Variable);
    }

    [Theory]
    [InlineData("ENCORE_LIBRARY_JELLYFIN_DIR", "media/music")]
    [InlineData("ENCORE_LIBRARY_JELLYFIN_DIR", "/media/../x")]
    [InlineData("ENCORE_LIBRARY_JELLYFIN_DIR", "/")]
    [InlineData("ENCORE_SLSKD_RADIO_DIR", "music-radio")]
    [InlineData("ENCORE_SLSKD_RADIO_DIR", "/a\\b")]
    public void RC07_bad_remote_dirs_are_errors(string name, string value)
    {
        var env = Valid();
        env[name] = value;
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal(name, error.Variable);
    }

    [Fact]
    public void RC07_trailing_slash_is_trimmed()
    {
        var env = Valid();
        env["ENCORE_LIBRARY_JELLYFIN_DIR"] = "/data/music/";
        env["ENCORE_SLSKD_RADIO_DIR"] = "/srv/radio/";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal("/data/music", o.LibraryJellyfinDir);
        Assert.Equal("/srv/radio", o.SlskdRadioDir);
    }

    [Fact]
    public void RC08_variables_list_has_the_new_names_and_startup_line_logs_them()
    {
        foreach (var name in new[] { "ENCORE_RETENTION_MODE", "ENCORE_RETENTION_PLAYS", "ENCORE_LIBRARY_DIR", "ENCORE_SLSKD_RADIO_DIR" })
        {
            Assert.Contains(name, EncoreOptionsLoader.Variables);
        }

        var o = EncoreOptionsLoader.Load(Valid()).Options!;
        var logger = new CapturingLogger();
        logger.StartupConfig(o, "test");
        var line = Assert.Single(logger.Lines);
        Assert.Contains("retentionMode Off", line);
        Assert.Contains("libraryDir /library", line);
        Assert.DoesNotContain("JFKEY0123456789abcdef", line);
        Assert.DoesNotContain("JFKEY0123456789abcdef", o.ToString());
    }

    [Fact]
    public void RC09_limits_constants()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), RetentionLimits.EventsReadEvery);
        Assert.Equal(50, RetentionLimits.MaxEventPages);
        Assert.Equal(TimeSpan.FromHours(6), RetentionLimits.MinRescanGap);
        Assert.Equal(5, RetentionLimits.MaxPromotionAttempts);
        Assert.Equal(TimeSpan.FromDays(7), RetentionLimits.TrashKept);
        Assert.Equal(20L * 1024 * 1024 * 1024, RetentionLimits.FreeSpaceFloorBytes);
        Assert.Equal(".encore-part", RetentionLimits.StagedSuffix);
        Assert.Equal("[Smart]", RetentionLimits.SmartListSuffix);
        Assert.Equal((UnixFileMode)0b111_101_101, RetentionLimits.NewFolderMode);
        Assert.Equal((UnixFileMode)0b110_100_100, RetentionLimits.NewFileMode);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
