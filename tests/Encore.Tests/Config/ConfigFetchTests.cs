using Encore.Config;
using Encore.Logging;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Config;

/// <summary>v0.2 fetch settings (design section 11, cases CF01-CF16).</summary>
/// <remarks>CF15 sets process environment variables, so the class runs outside the parallel collections.</remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConfigFetchTests : IDisposable
{
    private const string SlskdKey = "SLSKDSECRET0123456789xyz";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-fetch-test-" + Guid.NewGuid().ToString("N"));

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

    private Dictionary<string, string?> Fetch(string mode)
    {
        var env = Valid();
        env["ENCORE_FETCH_MODE"] = mode;
        env["ENCORE_SLSKD_URL"] = "http://gluetun-slskd:5030";
        env["ENCORE_SLSKD_API_KEY"] = SlskdKey;
        return env;
    }

    private (string Downloads, string Radio) MakeDirs()
    {
        var downloads = Path.Combine(_root, "downloads");
        var radio = Path.Combine(_root, "radio");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(radio);
        return (downloads, radio);
    }

    [Fact]
    public void CF01_defaults_need_no_slskd_variables()
    {
        var result = EncoreOptionsLoader.Load(Valid());
        var o = result.Options!;
        Assert.Equal(FetchMode.Off, o.FetchMode);
        Assert.Null(o.SlskdUrl);
        Assert.Null(o.SlskdApiKey);
        Assert.Equal(new SearchLimit(34, 220), o.SearchLimit);
        Assert.Equal(2, o.FetchConcurrency);
        Assert.Equal("/slskd-downloads", o.SlskdDownloadsDir);
        Assert.Equal("/music-radio", o.RadioDir);
        Assert.Equal("/music-radio", o.RadioJellyfinDir);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("off", FetchMode.Off)]
    [InlineData("OFF", FetchMode.Off)]
    [InlineData("dry_run", FetchMode.DryRun)]
    [InlineData("dry-run", FetchMode.DryRun)]
    [InlineData("DryRun", FetchMode.DryRun)]
    [InlineData("live", FetchMode.Live)]
    public void CF02_mode_spellings(string text, FetchMode expected)
    {
        var (downloads, radio) = MakeDirs();
        var env = Fetch(text);
        env["ENCORE_SLSKD_DOWNLOADS_DIR"] = downloads;
        env["ENCORE_RADIO_DIR"] = radio;
        env["ENCORE_DRY_RUN"] = "false";
        Assert.Equal(expected, EncoreOptionsLoader.Load(env).Options!.FetchMode);
    }

    [Fact]
    public void CF02_unknown_mode_is_an_error_naming_the_values()
    {
        var env = Fetch("on");
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_FETCH_MODE", error.Variable);
        Assert.Contains("off, dry_run or live", error.Message);
    }

    [Fact]
    public void CF03_dry_run_without_url_and_key_gives_exactly_those_errors()
    {
        var env = Valid();
        env["ENCORE_FETCH_MODE"] = "dry_run";
        var errors = EncoreOptionsLoader.Load(env).Errors;
        Assert.Equal(["ENCORE_SLSKD_URL", "ENCORE_SLSKD_API_KEY"], errors.Select(e => e.Variable));
    }

    [Theory]
    [InlineData("gluetun-slskd:5030")]
    [InlineData("/relative")]
    [InlineData("ftp://slskd:5030")]
    [InlineData("http://user:pw@slskd:5030")]
    [InlineData("http://slskd:5030/?a=1")]
    [InlineData("http://slskd:5030/#frag")]
    public void CF04_bad_urls_give_one_error(string url)
    {
        var env = Fetch("dry_run");
        env["ENCORE_SLSKD_URL"] = url;
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_SLSKD_URL", error.Variable);
    }

    [Fact]
    public void CF04_trailing_slash_is_trimmed()
    {
        var env = Fetch("dry_run");
        env["ENCORE_SLSKD_URL"] = "http://gluetun-slskd:5030/";
        Assert.Equal("http://gluetun-slskd:5030", EncoreOptionsLoader.Load(env).Options!.SlskdUrl!.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("SHORTSECRET12345")]
    [InlineData("SLSKDLONG")]
    [InlineData("SLSKDSPACE0123456789 tail")]
    [InlineData("SLSKDCONTROL")]
    public void CF05_bad_keys_are_errors_and_never_echoed(string kind)
    {
        var key = kind switch
        {
            "SHORTSECRET12345" => "SHORTSECRET1234", // 15 characters
            "SLSKDLONG" => "SLSKDLONG" + new string('x', 247), // 256 characters
            "SLSKDCONTROL" => "SLSKDCONTROL0123\u0001tail", // a control character that is not whitespace
            _ => kind,
        };
        var env = Fetch("dry_run");
        env["ENCORE_SLSKD_API_KEY"] = key;
        var errors = EncoreOptionsLoader.Load(env).Errors;
        var error = Assert.Single(errors);
        Assert.Equal("ENCORE_SLSKD_API_KEY", error.Variable);
        Assert.DoesNotContain("SHORTSECRET", error.Message);
        Assert.DoesNotContain("SLSKDLONG", error.Message);
        Assert.DoesNotContain("SLSKDSPACE", error.Message);
        Assert.DoesNotContain("SLSKDCONTROL", error.Message);
    }

    [Fact]
    public void CF05_boundary_lengths_are_accepted()
    {
        foreach (var length in new[] { 16, 255 })
        {
            var env = Fetch("dry_run");
            env["ENCORE_SLSKD_API_KEY"] = new string('k', length);
            Assert.True(EncoreOptionsLoader.Load(env).IsValid, $"length {length}");
        }
    }

    [Fact]
    public void CF06_live_with_dry_run_true_is_an_error_but_other_combinations_pass()
    {
        var (downloads, radio) = MakeDirs();
        var env = Fetch("live");
        env["ENCORE_SLSKD_DOWNLOADS_DIR"] = downloads;
        env["ENCORE_RADIO_DIR"] = radio;

        env["ENCORE_DRY_RUN"] = "true";
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_FETCH_MODE", error.Variable);

        env["ENCORE_DRY_RUN"] = "false";
        Assert.True(EncoreOptionsLoader.Load(env).IsValid);

        env["ENCORE_FETCH_MODE"] = "dry_run";
        env["ENCORE_DRY_RUN"] = "true";
        Assert.True(EncoreOptionsLoader.Load(env).IsValid);
    }

    [Fact]
    public void CF07_live_checks_dirs_and_dry_run_does_not()
    {
        var env = Fetch("live");
        env["ENCORE_DRY_RUN"] = "false";
        env["ENCORE_SLSKD_DOWNLOADS_DIR"] = Path.Combine(_root, "missing-downloads");
        env["ENCORE_RADIO_DIR"] = Path.Combine(_root, "missing-radio");
        var errors = EncoreOptionsLoader.Load(env).Errors;
        Assert.Equal(["ENCORE_SLSKD_DOWNLOADS_DIR", "ENCORE_RADIO_DIR"], errors.Select(e => e.Variable));

        env["ENCORE_FETCH_MODE"] = "dry_run";
        Assert.True(EncoreOptionsLoader.Load(env).IsValid);
        env.Remove("ENCORE_SLSKD_URL");
        env.Remove("ENCORE_SLSKD_API_KEY");
        env["ENCORE_FETCH_MODE"] = "off";
        Assert.True(EncoreOptionsLoader.Load(env).IsValid);
    }

    [Fact]
    public void CF07_radio_dir_that_is_a_file_is_an_error()
    {
        var (downloads, _) = MakeDirs();
        var file = Path.Combine(_root, "afile");
        File.WriteAllText(file, "x");
        var env = Fetch("live");
        env["ENCORE_DRY_RUN"] = "false";
        env["ENCORE_SLSKD_DOWNLOADS_DIR"] = downloads;
        env["ENCORE_RADIO_DIR"] = file;
        Assert.Equal("ENCORE_RADIO_DIR", Assert.Single(EncoreOptionsLoader.Load(env).Errors).Variable);
    }

    [Fact]
    public void CF08_read_only_downloads_dir_is_a_warning()
    {
        if (OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Environment.UserName == "root"))
        {
            return; // cannot make a folder read-only for this user here
        }

        var (downloads, radio) = MakeDirs();
        File.SetUnixFileMode(downloads, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var env = Fetch("live");
            env["ENCORE_DRY_RUN"] = "false";
            env["ENCORE_SLSKD_DOWNLOADS_DIR"] = downloads;
            env["ENCORE_RADIO_DIR"] = radio;
            var result = EncoreOptionsLoader.Load(env);
            Assert.True(result.IsValid);
            Assert.Equal("ENCORE_SLSKD_DOWNLOADS_DIR", Assert.Single(result.Warnings).Variable);
        }
        finally
        {
            File.SetUnixFileMode(downloads, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("/music-radio/", "/music-radio")]
    [InlineData("/data/radio", "/data/radio")]
    public void CF09_jellyfin_dir_is_trimmed(string text, string expected)
    {
        var env = Valid();
        env["ENCORE_RADIO_JELLYFIN_DIR"] = text;
        Assert.Equal(expected, EncoreOptionsLoader.Load(env).Options!.RadioJellyfinDir);
    }

    [Theory]
    [InlineData("music-radio")]
    [InlineData("/a/../b")]
    [InlineData("C:\\x")]
    [InlineData("/")]
    public void CF09_bad_jellyfin_dir_is_an_error(string text)
    {
        var env = Valid();
        env["ENCORE_RADIO_JELLYFIN_DIR"] = text;
        Assert.Equal("ENCORE_RADIO_JELLYFIN_DIR", Assert.Single(EncoreOptionsLoader.Load(env).Errors).Variable);
    }

    [Fact]
    public void CF10_search_limit_valid_value()
    {
        var env = Valid();
        env["ENCORE_SLSK_SEARCH_LIMIT"] = "10/60";
        Assert.Equal(new SearchLimit(10, 60), EncoreOptionsLoader.Load(env).Options!.SearchLimit);
        env["ENCORE_SLSK_SEARCH_LIMIT"] = "34/220";
        Assert.Equal(new SearchLimit(34, 220), EncoreOptionsLoader.Load(env).Options!.SearchLimit);
    }

    [Theory]
    [InlineData("0/220")]
    [InlineData("101/220")]
    [InlineData("34/5")]
    [InlineData("34/3601")]
    [InlineData("34")]
    [InlineData("a/b")]
    [InlineData("34/220/1")]
    public void CF10_bad_search_limit_is_an_error(string text)
    {
        var env = Valid();
        env["ENCORE_SLSK_SEARCH_LIMIT"] = text;
        Assert.Equal("ENCORE_SLSK_SEARCH_LIMIT", Assert.Single(EncoreOptionsLoader.Load(env).Errors).Variable);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("5", true)]
    [InlineData("0", false)]
    [InlineData("6", false)]
    [InlineData("x", false)]
    public void CF11_concurrency_range(string text, bool ok)
    {
        var env = Valid();
        env["ENCORE_FETCH_CONCURRENCY"] = text;
        var result = EncoreOptionsLoader.Load(env);
        Assert.Equal(ok, result.IsValid);
        if (!ok)
        {
            Assert.Equal("ENCORE_FETCH_CONCURRENCY", Assert.Single(result.Errors).Variable);
        }
    }

    [Fact]
    public void CF12_to_string_redacts_both_keys()
    {
        var text = EncoreOptionsLoader.Load(Fetch("dry_run")).Options!.ToString();
        Assert.DoesNotContain("SLSKDSECRET", text);
        Assert.DoesNotContain("JFKEY", text);
        Assert.Contains("SlskdApiKey = <redacted>", text);
        Assert.Contains("JellyfinApiKey = <redacted>", text);
        Assert.Contains("FetchMode = DryRun", text);
    }

    [Fact]
    public void CF13_startup_log_has_new_fields_and_never_the_key()
    {
        var options = EncoreOptionsLoader.Load(Fetch("dry_run")).Options!;
        var sink = new ListLogger();
        sink.StartupConfig(options, "0.2.0");
        var line = Assert.Single(sink.Lines);
        Assert.DoesNotContain("SLSKDSECRET", line);
        Assert.Contains("fetchMode DryRun", line);
        Assert.Contains("slskdUrl http://gluetun-slskd:5030", line);
        Assert.Contains("slskdApiKey set", line);
        Assert.Contains("searchLimit 34/220", line);
        Assert.Contains("fetchConcurrency 2", line);

        var off = new ListLogger();
        off.StartupConfig(EncoreOptionsLoader.Load(Valid()).Options!, "0.2.0");
        Assert.Contains("slskdApiKey unset", Assert.Single(off.Lines));
    }

    [Fact]
    public void CF13_config_warning_log_event()
    {
        var sink = new ListLogger();
        sink.StartupConfigWarning("ENCORE_SLSKD_DOWNLOADS_DIR", "not writable");
        var line = Assert.Single(sink.Lines);
        Assert.StartsWith("startup.config_warning|", line);
    }

    [Fact]
    public void CF14_errors_are_reported_together_without_the_key()
    {
        var env = Fetch("bogus");
        env["ENCORE_SLSKD_URL"] = "nope";
        env["ENCORE_FETCH_CONCURRENCY"] = "9";
        var errors = EncoreOptionsLoader.Load(env).Errors;
        Assert.Equal(["ENCORE_FETCH_MODE", "ENCORE_SLSKD_URL", "ENCORE_FETCH_CONCURRENCY"], errors.Select(e => e.Variable));
        Assert.All(errors, e => Assert.DoesNotContain("SLSKDSECRET", e.Message));
    }

    [Fact]
    public void CF15_environment_loader_reads_every_new_variable()
    {
        var (downloads, radio) = MakeDirs();
        var values = new Dictionary<string, string>
        {
            ["JELLYFIN_URL"] = "http://jellyfin:8096",
            ["JELLYFIN_API_KEY"] = "JFKEY0123456789abcdef",
            ["ENCORE_ALLOWED_USERS"] = "nathan",
            ["ENCORE_DATA_DIR"] = Path.Combine(_root, "data"),
            ["ENCORE_DRY_RUN"] = "false",
            ["ENCORE_FETCH_MODE"] = "live",
            ["ENCORE_SLSKD_URL"] = "http://slskd:5030/",
            ["ENCORE_SLSKD_API_KEY"] = SlskdKey,
            ["ENCORE_SLSKD_DOWNLOADS_DIR"] = downloads,
            ["ENCORE_RADIO_DIR"] = radio,
            ["ENCORE_RADIO_JELLYFIN_DIR"] = "/jf-radio/",
            ["ENCORE_SLSK_SEARCH_LIMIT"] = "20/100",
            ["ENCORE_FETCH_CONCURRENCY"] = "3",
        };
        var previous = values.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (k, v) in values)
            {
                Environment.SetEnvironmentVariable(k, v);
            }

            var result = EncoreOptionsLoader.LoadFromEnvironment();
            var o = result.Options!;
            Assert.Empty(result.Errors);
            Assert.Equal(FetchMode.Live, o.FetchMode);
            Assert.Equal("http://slskd:5030", o.SlskdUrl!.ToString().TrimEnd('/'));
            Assert.Equal(SlskdKey, o.SlskdApiKey);
            Assert.Equal(downloads, o.SlskdDownloadsDir);
            Assert.Equal(radio, o.RadioDir);
            Assert.Equal("/jf-radio", o.RadioJellyfinDir);
            Assert.Equal(new SearchLimit(20, 100), o.SearchLimit);
            Assert.Equal(3, o.FetchConcurrency);
        }
        finally
        {
            foreach (var (k, v) in previous)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Fact]
    public void CF16_no_ratio_setting_exists()
    {
        var banned = new[] { "ratio", "percent", "owned", "mix" };
        foreach (var property in typeof(EncoreOptions).GetProperties())
        {
            Assert.DoesNotContain(banned, b => property.Name.Contains(b, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var variable in EncoreOptionsLoader.Variables)
        {
            Assert.DoesNotContain(banned, b => variable.Contains(b, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{eventId.Name}|{formatter(state, exception)}");
    }
}
