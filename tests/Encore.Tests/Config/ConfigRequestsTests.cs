using Encore.Config;
using Encore.Logging;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Config;

/// <summary>v0.3 request settings (v03 design section 11, cases RQ01-RQ20).</summary>
public sealed class ConfigRequestsTests : IDisposable
{
    private const string LidarrKey = "LIDARRSECRET0123456789ab";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-req-test-" + Guid.NewGuid().ToString("N"));

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

    private Dictionary<string, string?> With(string name, string? value)
    {
        var env = Valid();
        env[name] = value;
        return env;
    }

    private static void AssertOnlyError(ConfigResult result, string variable)
    {
        Assert.False(result.IsValid);
        Assert.Equal(variable, Assert.Single(result.Errors).Variable);
    }

    [Fact]
    public void RQ01_defaults()
    {
        var result = EncoreOptionsLoader.Load(Valid());
        var o = result.Options!;
        Assert.False(o.RequestsEnabled);
        Assert.Empty(o.RequestUsers);
        Assert.False(o.RequestUsersAll);
        Assert.Equal(25, o.RequestSongsPerDay);
        Assert.Equal(2, o.RequestAlbumsPerDay);
        Assert.Empty(o.RequestOrigins);
        Assert.Null(o.PublicUrl);
        Assert.Equal("", o.PathBase);
        Assert.Empty(o.TrustedProxies);
        Assert.Null(o.JellyfinPublicUrl);
        Assert.Null(o.LidarrUrl);
        Assert.Null(o.LidarrApiKey);
        Assert.Null(o.LidarrRootFolder);
        Assert.Equal(new PathMapping("/media/music", "/media/music"), o.LidarrPathMap);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    public void RQ02_requests_switch(string text, bool expected)
    {
        var env = With("ENCORE_REQUESTS", text);
        env["ENCORE_FETCH_MODE"] = "dry_run";
        env["ENCORE_SLSKD_URL"] = "http://slskd:5030";
        env["ENCORE_SLSKD_API_KEY"] = "SLSKDSECRET0123456789xyz";
        Assert.Equal(expected, EncoreOptionsLoader.Load(env).Options!.RequestsEnabled);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("true")]
    [InlineData("1")]
    public void RQ02_bad_requests_value_is_an_error(string text) =>
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_REQUESTS", text)), "ENCORE_REQUESTS");

    [Fact]
    public void RQ03_request_users_list_trims_and_dedups()
    {
        var o = EncoreOptionsLoader.Load(With("ENCORE_REQUEST_USERS", " gotenksnath, Mom ,gotenksnath,abc123 ")).Options!;
        Assert.Equal(["gotenksnath", "Mom", "abc123"], o.RequestUsers);
        Assert.False(o.RequestUsersAll);
    }

    [Theory]
    [InlineData("*")]
    [InlineData(" * ")]
    [InlineData("nathan,*")]
    public void RQ04_star_means_everyone(string text)
    {
        var o = EncoreOptionsLoader.Load(With("ENCORE_REQUEST_USERS", text)).Options!;
        Assert.True(o.RequestUsersAll);
        Assert.DoesNotContain("*", o.RequestUsers);
    }

    [Theory]
    [InlineData("ENCORE_REQUEST_SONGS_PER_DAY", "0", true)]
    [InlineData("ENCORE_REQUEST_SONGS_PER_DAY", "500", true)]
    [InlineData("ENCORE_REQUEST_SONGS_PER_DAY", "501", false)]
    [InlineData("ENCORE_REQUEST_SONGS_PER_DAY", "-1", false)]
    [InlineData("ENCORE_REQUEST_SONGS_PER_DAY", "many", false)]
    [InlineData("ENCORE_REQUEST_ALBUMS_PER_DAY", "0", true)]
    [InlineData("ENCORE_REQUEST_ALBUMS_PER_DAY", "50", true)]
    [InlineData("ENCORE_REQUEST_ALBUMS_PER_DAY", "51", false)]
    public void RQ05_daily_caps_range(string name, string text, bool ok)
    {
        var result = EncoreOptionsLoader.Load(With(name, text));
        if (ok)
        {
            Assert.True(result.IsValid);
        }
        else
        {
            AssertOnlyError(result, name);
        }
    }

    [Fact]
    public void RQ06_origins_are_normalised_and_deduped()
    {
        var o = EncoreOptionsLoader.Load(With("ENCORE_REQUEST_ORIGINS",
            "http://192.168.86.50:8096, HTTPS://Media.Nateplex.com:443 ,https://media.nateplex.com")).Options!;
        Assert.Equal(["http://192.168.86.50:8096", "https://media.nateplex.com"], o.RequestOrigins);
    }

    [Theory]
    [InlineData("media.nateplex.com")]
    [InlineData("ftp://media.nateplex.com")]
    [InlineData("https://media.nateplex.com/")]
    [InlineData("https://media.nateplex.com/jf")]
    [InlineData("https://media.nateplex.com?x=1")]
    [InlineData("https://user@media.nateplex.com")]
    public void RQ06_bad_origin_is_an_error(string text) =>
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_REQUEST_ORIGINS", text)), "ENCORE_REQUEST_ORIGINS");

    [Theory]
    [InlineData("/encore")]
    [InlineData("/apps/encore")]
    [InlineData("/a-b_c.d~e")]
    public void RQ07_path_base_valid(string text) =>
        Assert.Equal(text, EncoreOptionsLoader.Load(With("ENCORE_PATH_BASE", text)).Options!.PathBase);

    [Theory]
    [InlineData("encore")]
    [InlineData("/encore/")]
    [InlineData("/")]
    [InlineData("/a//b")]
    [InlineData("/a/../b")]
    [InlineData("/en core")]
    [InlineData("/enc?ore")]
    public void RQ07_path_base_invalid(string text) =>
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_PATH_BASE", text)), "ENCORE_PATH_BASE");

    [Fact]
    public void RQ08_public_url_without_path_base()
    {
        var o = EncoreOptionsLoader.Load(With("ENCORE_PUBLIC_URL", "http://192.168.86.50:5095/")).Options!;
        Assert.Equal("http://192.168.86.50:5095/", o.PublicUrl!.ToString());
        Assert.Equal("", o.PathBase);
    }

    [Fact]
    public void RQ08_public_url_path_must_equal_path_base()
    {
        var env = With("ENCORE_PUBLIC_URL", "https://media.nateplex.com/encore/");
        env["ENCORE_PATH_BASE"] = "/encore";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal("https://media.nateplex.com/encore", o.PublicUrl!.ToString());

        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_PUBLIC_URL", "https://media.nateplex.com/encore")), "ENCORE_PUBLIC_URL");

        var mismatch = With("ENCORE_PUBLIC_URL", "https://media.nateplex.com/other");
        mismatch["ENCORE_PATH_BASE"] = "/encore";
        AssertOnlyError(EncoreOptionsLoader.Load(mismatch), "ENCORE_PUBLIC_URL");

        var noPath = With("ENCORE_PUBLIC_URL", "https://media.nateplex.com");
        noPath["ENCORE_PATH_BASE"] = "/encore";
        AssertOnlyError(EncoreOptionsLoader.Load(noPath), "ENCORE_PUBLIC_URL");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("ftp://x/")]
    [InlineData("http://u:p@host:5095")]
    [InlineData("http://host:5095?a=b")]
    public void RQ08_bad_public_url(string text) =>
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_PUBLIC_URL", text)), "ENCORE_PUBLIC_URL");

    [Fact]
    public void RQ09_trusted_proxies_accept_ips_and_cidrs()
    {
        var o = EncoreOptionsLoader.Load(With("ENCORE_TRUSTED_PROXIES",
            "172.18.0.5, 10.0.0.0/8, 2400:cb00::/32, ::1")).Options!;
        Assert.Equal(4, o.TrustedProxies.Count);
        Assert.Contains(o.TrustedProxies, n => n.Contains(System.Net.IPAddress.Parse("10.4.5.6")));
        Assert.Contains(o.TrustedProxies, n => n.Contains(System.Net.IPAddress.Parse("172.18.0.5")));
        Assert.DoesNotContain(o.TrustedProxies, n => n.Contains(System.Net.IPAddress.Parse("172.18.0.6")));
        Assert.Contains(o.TrustedProxies, n => n.Contains(System.Net.IPAddress.Parse("2400:cb00::1")));
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/x")]
    [InlineData("2400:cb00::/129")]
    [InlineData("10.1.2.3/8")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.1,,bad")]
    public void RQ09_bad_proxy_entry_is_an_error(string text) =>
        Assert.Contains(EncoreOptionsLoader.Load(With("ENCORE_TRUSTED_PROXIES", text)).Errors, e => e.Variable == "ENCORE_TRUSTED_PROXIES");

    [Fact]
    public void RQ09_at_most_64_proxies()
    {
        string List(int n) => string.Join(",", Enumerable.Range(1, n).Select(i => $"10.0.{i}.1"));
        Assert.Equal(64, EncoreOptionsLoader.Load(With("ENCORE_TRUSTED_PROXIES", List(64))).Options!.TrustedProxies.Count);
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_TRUSTED_PROXIES", List(65))), "ENCORE_TRUSTED_PROXIES");
    }

    [Fact]
    public void RQ10_jellyfin_public_url()
    {
        var o = EncoreOptionsLoader.Load(With("ENCORE_JELLYFIN_PUBLIC_URL", "https://media.nateplex.com/")).Options!;
        Assert.Equal("https://media.nateplex.com/", o.JellyfinPublicUrl!.ToString());
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_JELLYFIN_PUBLIC_URL", "media.nateplex.com")), "ENCORE_JELLYFIN_PUBLIC_URL");
    }

    [Fact]
    public void RQ11_lidarr_url_and_key()
    {
        var env = With("ENCORE_LIDARR_URL", "http://lidarr:8686/");
        env["ENCORE_LIDARR_API_KEY"] = LidarrKey;
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal("http://lidarr:8686/", o.LidarrUrl!.ToString());
        Assert.Equal(LidarrKey, o.LidarrApiKey);
    }

    [Fact]
    public void RQ11_lidarr_url_needs_a_key()
    {
        var result = EncoreOptionsLoader.Load(With("ENCORE_LIDARR_URL", "http://lidarr:8686"));
        AssertOnlyError(result, "ENCORE_LIDARR_API_KEY");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789012345678")] // 19
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123X")] // 65
    [InlineData("0123456789-123456789012")]
    [InlineData("0123456789 123456789012")]
    public void RQ12_bad_lidarr_key_is_an_error_without_the_key(string key)
    {
        var env = With("ENCORE_LIDARR_URL", "http://lidarr:8686");
        env["ENCORE_LIDARR_API_KEY"] = key;
        var result = EncoreOptionsLoader.Load(env);
        AssertOnlyError(result, "ENCORE_LIDARR_API_KEY");
        Assert.DoesNotContain(key, result.Errors.Single().Message);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("http://u:p@lidarr:8686")]
    [InlineData("http://lidarr:8686?apikey=x")]
    public void RQ11_bad_lidarr_url(string text)
    {
        var env = With("ENCORE_LIDARR_URL", text);
        env["ENCORE_LIDARR_API_KEY"] = LidarrKey;
        AssertOnlyError(EncoreOptionsLoader.Load(env), "ENCORE_LIDARR_URL");
    }

    [Fact]
    public void RQ13_key_without_url_is_a_warning_and_is_dropped()
    {
        var result = EncoreOptionsLoader.Load(With("ENCORE_LIDARR_API_KEY", LidarrKey));
        Assert.True(result.IsValid);
        Assert.Null(result.Options!.LidarrApiKey);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("ENCORE_LIDARR_API_KEY", warning.Variable);
        Assert.DoesNotContain(LidarrKey, warning.Message);
    }

    [Fact]
    public void RQ14_root_folder_and_path_map()
    {
        var env = With("ENCORE_LIDARR_ROOT_FOLDER", "/media/music/");
        env["ENCORE_LIDARR_PATH_MAP"] = " /music/ = /media/music/ ";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal("/media/music", o.LidarrRootFolder);
        Assert.Equal(new PathMapping("/music", "/media/music"), o.LidarrPathMap);
    }

    [Theory]
    [InlineData("/music")]
    [InlineData("music=/media")]
    [InlineData("/music=media")]
    [InlineData("/a=/b=/c")]
    [InlineData("/a/../b=/c")]
    [InlineData("/=/c")]
    public void RQ14_bad_path_map(string text) =>
        AssertOnlyError(EncoreOptionsLoader.Load(With("ENCORE_LIDARR_PATH_MAP", text)), "ENCORE_LIDARR_PATH_MAP");

    [Fact]
    public void RQ15_requests_on_with_fetch_off_warns()
    {
        var result = EncoreOptionsLoader.Load(With("ENCORE_REQUESTS", "on"));
        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("ENCORE_REQUESTS", warning.Variable);
        Assert.Contains("songs_unavailable", warning.Message);
    }

    [Fact]
    public void RQ15_no_warning_when_off_or_fetch_on()
    {
        Assert.Empty(EncoreOptionsLoader.Load(With("ENCORE_REQUESTS", "off")).Warnings);

        var env = With("ENCORE_REQUESTS", "on");
        env["ENCORE_FETCH_MODE"] = "dry_run";
        env["ENCORE_SLSKD_URL"] = "http://slskd:5030";
        env["ENCORE_SLSKD_API_KEY"] = "SLSKDSECRET0123456789xyz";
        Assert.Empty(EncoreOptionsLoader.Load(env).Warnings);
    }

    [Fact]
    public void RQ17_https_public_url_without_trusted_proxies_warns()
    {
        var env = Full();
        env.Remove("ENCORE_TRUSTED_PROXIES");
        var result = EncoreOptionsLoader.Load(env);
        Assert.True(result.IsValid);
        Assert.Equal("ENCORE_TRUSTED_PROXIES", Assert.Single(result.Warnings).Variable);

        Assert.Empty(EncoreOptionsLoader.Load(Full()).Warnings);

        var lan = Full();
        lan.Remove("ENCORE_TRUSTED_PROXIES");
        lan.Remove("ENCORE_PATH_BASE");
        lan["ENCORE_PUBLIC_URL"] = "http://192.168.86.50:5095";
        Assert.Empty(EncoreOptionsLoader.Load(lan).Warnings);
    }

    private Dictionary<string, string?> Full()
    {
        var env = With("ENCORE_REQUESTS", "on");
        env["ENCORE_FETCH_MODE"] = "dry_run";
        env["ENCORE_SLSKD_URL"] = "http://slskd:5030";
        env["ENCORE_SLSKD_API_KEY"] = "SLSKDSECRET0123456789xyz";
        env["ENCORE_REQUEST_USERS"] = "*";
        env["ENCORE_REQUEST_ORIGINS"] = "https://media.nateplex.com";
        env["ENCORE_PUBLIC_URL"] = "https://media.nateplex.com/encore";
        env["ENCORE_PATH_BASE"] = "/encore";
        env["ENCORE_TRUSTED_PROXIES"] = "172.18.0.0/16";
        env["ENCORE_JELLYFIN_PUBLIC_URL"] = "https://media.nateplex.com";
        env["ENCORE_LIDARR_URL"] = "http://lidarr:8686";
        env["ENCORE_LIDARR_API_KEY"] = LidarrKey;
        return env;
    }

    [Fact]
    public void RQ16_to_string_redacts_the_lidarr_key()
    {
        var text = EncoreOptionsLoader.Load(Full()).Options!.ToString();
        Assert.DoesNotContain(LidarrKey, text);
        Assert.DoesNotContain("SLSKDSECRET", text);
        Assert.Contains("LidarrApiKey = <redacted>", text);
        Assert.Contains("RequestsEnabled = True", text);
        Assert.Contains("RequestUsers = [*]", text);
        Assert.Contains("PathBase = /encore", text);
        Assert.Contains("TrustedProxies = [172.18.0.0/16]", text);
    }

    [Fact]
    public void RQ17_startup_log_reports_the_new_settings_and_never_the_key()
    {
        var sink = new ListLogger();
        sink.StartupConfig(EncoreOptionsLoader.Load(Full()).Options!, "0.3.0");
        var line = Assert.Single(sink.Lines);
        Assert.DoesNotContain(LidarrKey, line);
        Assert.Contains("requests on", line);
        Assert.Contains("requestUsers [*]", line);
        Assert.Contains("songsPerDay 25, albumsPerDay 2", line);
        Assert.Contains("origins [https://media.nateplex.com]", line);
        Assert.Contains("pathBase /encore", line);
        Assert.Contains("trustedProxies [172.18.0.0/16]", line);
        Assert.Contains("lidarrUrl http://lidarr:8686", line);
        Assert.Contains("lidarrApiKey set", line);
        Assert.Contains("lidarrPathMap /media/music=/media/music", line);

        var off = new ListLogger();
        off.StartupConfig(EncoreOptionsLoader.Load(Valid()).Options!, "0.3.0");
        var offLine = Assert.Single(off.Lines);
        Assert.Contains("requests off", offLine);
        Assert.Contains("lidarrApiKey unset", offLine);
        Assert.Contains("pathBase none", offLine);
    }

    [Fact]
    public void RQ18_errors_are_reported_together_without_the_lidarr_key()
    {
        var env = Valid();
        env["ENCORE_REQUESTS"] = "maybe";
        env["ENCORE_REQUEST_SONGS_PER_DAY"] = "9999";
        env["ENCORE_LIDARR_URL"] = "http://lidarr:8686";
        env["ENCORE_LIDARR_API_KEY"] = "bad-" + LidarrKey;
        env["ENCORE_TRUSTED_PROXIES"] = "nope";
        var result = EncoreOptionsLoader.Load(env);
        Assert.False(result.IsValid);
        var variables = result.Errors.Select(e => e.Variable).ToList();
        Assert.Contains("ENCORE_REQUESTS", variables);
        Assert.Contains("ENCORE_REQUEST_SONGS_PER_DAY", variables);
        Assert.Contains("ENCORE_LIDARR_API_KEY", variables);
        Assert.Contains("ENCORE_TRUSTED_PROXIES", variables);
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains(LidarrKey));
    }

    [Fact]
    public void RQ19_every_new_variable_is_known_to_the_environment_loader()
    {
        string[] expected =
        [
            "ENCORE_REQUESTS", "ENCORE_REQUEST_USERS", "ENCORE_REQUEST_SONGS_PER_DAY", "ENCORE_REQUEST_ALBUMS_PER_DAY",
            "ENCORE_REQUEST_ORIGINS", "ENCORE_PUBLIC_URL", "ENCORE_PATH_BASE", "ENCORE_TRUSTED_PROXIES",
            "ENCORE_JELLYFIN_PUBLIC_URL", "ENCORE_LIDARR_URL", "ENCORE_LIDARR_API_KEY", "ENCORE_LIDARR_ROOT_FOLDER",
            "ENCORE_LIDARR_PATH_MAP",
        ];
        foreach (var name in expected)
        {
            Assert.Contains(name, EncoreOptionsLoader.Variables);
        }
    }

    [Fact]
    public void RQ20_unknown_fetch_mode_gives_no_extra_requests_warning()
    {
        var env = With("ENCORE_REQUESTS", "on");
        env["ENCORE_FETCH_MODE"] = "bogus";
        var result = EncoreOptionsLoader.Load(env);
        Assert.False(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w => w.Variable == "ENCORE_REQUESTS");
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
