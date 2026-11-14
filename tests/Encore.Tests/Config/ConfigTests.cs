using Encore.Config;
using Encore.Logging;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Config;

public sealed class ConfigTests : IDisposable
{
    private const string Key = "SECRETKEY0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private Dictionary<string, string?> Valid() => new()
    {
        ["JELLYFIN_URL"] = "http://jellyfin:8096/",
        ["JELLYFIN_API_KEY"] = Key,
        ["ENCORE_ALLOWED_USERS"] = "nathan",
        ["ENCORE_DATA_DIR"] = _dir,
    };

    [Fact]
    public void Defaults_are_applied()
    {
        var result = EncoreOptionsLoader.Load(Valid());
        var o = result.Options!;
        Assert.True(result.IsValid);
        Assert.Equal(5, o.Lookahead);
        Assert.Equal(2, o.Batch);
        Assert.Equal(10, o.PollSeconds);
        Assert.True(o.RecentWindow.IsSession);
        Assert.True(o.DryRun);
        Assert.Equal(LogLevel.Information, o.LogLevel);
        Assert.Equal("http://jellyfin:8096", o.JellyfinUrl.ToString().TrimEnd('/'));
        Assert.Equal("ws://jellyfin:8096/socket", o.WebSocketUrl.ToString());
    }

    [Fact]
    public void Https_url_gives_wss_socket_and_keeps_base_path()
    {
        var env = Valid();
        env["JELLYFIN_URL"] = "https://media.example.com/jf///";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal("https://media.example.com/jf", o.JellyfinUrl.ToString());
        Assert.Equal("wss://media.example.com/jf/socket", o.WebSocketUrl.ToString());
    }

    [Fact]
    public void Explicit_values_are_parsed()
    {
        var env = Valid();
        env["ENCORE_LOOKAHEAD"] = "8";
        env["ENCORE_BATCH"] = "8";
        env["ENCORE_POLL_SECONDS"] = "60";
        env["ENCORE_RECENT_WINDOW"] = "6h";
        env["ENCORE_DRY_RUN"] = "No";
        env["LOG_LEVEL"] = "debug";
        var o = EncoreOptionsLoader.Load(env).Options!;
        Assert.Equal(8, o.Lookahead);
        Assert.Equal(8, o.Batch);
        Assert.Equal(60, o.PollSeconds);
        Assert.Equal(TimeSpan.FromHours(6), o.RecentWindow.Duration);
        Assert.False(o.DryRun);
        Assert.Equal(LogLevel.Debug, o.LogLevel);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("YES", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    public void Dry_run_accepts_the_documented_spellings(string text, bool expected)
    {
        var env = Valid();
        env["ENCORE_DRY_RUN"] = text;
        Assert.Equal(expected, EncoreOptionsLoader.Load(env).Options!.DryRun);
    }

    [Fact]
    public void Allowed_users_are_trimmed_and_deduplicated_case_insensitively()
    {
        var env = Valid();
        env["ENCORE_ALLOWED_USERS"] = " Nathan, ,nathan,Sam ,SAM";
        Assert.Equal(["Nathan", "Sam"], EncoreOptionsLoader.Load(env).Options!.AllowedUsers);
    }

    [Fact]
    public void All_bad_values_are_reported_together()
    {
        var env = new Dictionary<string, string?>
        {
            ["JELLYFIN_URL"] = "ftp://x",
            ["JELLYFIN_API_KEY"] = "has space",
            ["ENCORE_ALLOWED_USERS"] = " , ",
            ["ENCORE_LOOKAHEAD"] = "21",
            ["ENCORE_POLL_SECONDS"] = "4",
            ["ENCORE_RECENT_WINDOW"] = "abc",
            ["ENCORE_DRY_RUN"] = "maybe",
            ["LOG_LEVEL"] = "loud",
            ["ENCORE_DATA_DIR"] = _dir,
        };
        var result = EncoreOptionsLoader.Load(env);
        Assert.False(result.IsValid);
        var names = result.Errors.Select(e => e.Variable).ToHashSet();
        string[] expected =
            ["JELLYFIN_URL", "JELLYFIN_API_KEY", "ENCORE_ALLOWED_USERS", "ENCORE_LOOKAHEAD", "ENCORE_POLL_SECONDS", "ENCORE_RECENT_WINDOW", "ENCORE_DRY_RUN", "LOG_LEVEL"];
        Assert.Equal(expected.Order(), names.Order());
    }

    [Fact]
    public void Missing_required_values_are_all_reported()
    {
        var result = EncoreOptionsLoader.Load(new Dictionary<string, string?> { ["ENCORE_DATA_DIR"] = _dir });
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Variable == "JELLYFIN_API_KEY" && e.Message == "JELLYFIN_API_KEY is missing");
    }

    [Fact]
    public void Batch_above_lookahead_is_rejected_with_the_documented_message()
    {
        var env = Valid();
        env["ENCORE_BATCH"] = "9";
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_BATCH must be 1..ENCORE_LOOKAHEAD (5), got \"9\"", error.Message);
    }

    [Theory]
    [InlineData("session", true, 0)]
    [InlineData("SESSION", true, 0)]
    [InlineData("30m", true, 30)]
    [InlineData("1m", true, 1)]
    [InlineData("6h", true, 360)]
    [InlineData("2d", true, 2880)]
    [InlineData("30d", true, 43200)]
    [InlineData("0m", false, 0)]
    [InlineData("31d", false, 0)]
    [InlineData("721h", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("10", false, 0)]
    [InlineData("-5m", false, 0)]
    [InlineData("99999999999m", false, 0)]
    public void Recent_window_parsing(string text, bool ok, int minutes)
    {
        Assert.Equal(ok, RecentWindow.TryParse(text, out var window));
        if (ok && minutes > 0)
        {
            Assert.Equal(TimeSpan.FromMinutes(minutes), window!.Duration);
        }
    }

    [Fact]
    public void Key_is_absent_from_every_error_and_from_to_string()
    {
        var env = Valid();
        env["JELLYFIN_API_KEY"] = Key + " tail";
        env["ENCORE_BATCH"] = "99";
        var bad = EncoreOptionsLoader.Load(env);
        Assert.All(bad.Errors, e => Assert.DoesNotContain("SECRETKEY", e.Message));
        Assert.Contains(bad.Errors, e => e.Message == "JELLYFIN_API_KEY contains whitespace");

        var good = EncoreOptionsLoader.Load(Valid()).Options!;
        Assert.DoesNotContain("SECRETKEY", good.ToString());
        Assert.Contains("<redacted>", good.ToString());
    }

    [Fact]
    public void Startup_summary_log_reports_key_as_set_and_never_shows_it()
    {
        var options = EncoreOptionsLoader.Load(Valid()).Options!;
        var sink = new ListLogger();
        sink.StartupConfig(options, "0.1.0");
        var entry = Assert.Single(sink.Lines);
        Assert.DoesNotContain("SECRETKEY", entry);
        Assert.Contains("apiKey set", entry);
        Assert.Contains(options.DeviceId, entry);
    }

    [Fact]
    public void Device_id_is_created_once_and_reused()
    {
        var first = EncoreOptionsLoader.Load(Valid()).Options!.DeviceId;
        var second = EncoreOptionsLoader.Load(Valid()).Options!.DeviceId;
        Assert.Matches("^encore-[0-9a-f]{12}$", first);
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllText(Path.Combine(_dir, "device-id")).Trim());
    }

    [Fact]
    public void Unwritable_data_dir_is_an_error()
    {
        // A path below an existing regular file can never be created or written.
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "afile");
        File.WriteAllText(file, "x");
        var env = Valid();
        env["ENCORE_DATA_DIR"] = Path.Combine(file, "sub");
        var error = Assert.Single(EncoreOptionsLoader.Load(env).Errors);
        Assert.Equal("ENCORE_DATA_DIR", error.Variable);
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
