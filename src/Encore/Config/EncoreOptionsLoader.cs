using System.Globalization;

namespace Encore.Config;

public sealed record ConfigResult(EncoreOptions? Options, IReadOnlyList<ConfigError> Errors, IReadOnlyList<ConfigError>? WarningList = null)
{
    /// <summary>Problems that do not stop startup (logged as startup.config_warning).</summary>
    public IReadOnlyList<ConfigError> Warnings => WarningList ?? [];

    public bool IsValid => Options is not null;
}

/// <summary>One bad variable. <see cref="Message"/> never contains the API key value.</summary>
public sealed record ConfigError(string Variable, string Message);

public static class EncoreOptionsLoader
{
    /// <summary>Process exit code when the configuration is invalid.</summary>
    public const int ExitCode = 2;

    private static readonly string[] KnownVariables =
    [
        "JELLYFIN_URL", "JELLYFIN_API_KEY", "ENCORE_ALLOWED_USERS", "ENCORE_LOOKAHEAD", "ENCORE_BATCH",
        "ENCORE_POLL_SECONDS", "ENCORE_RECENT_WINDOW", "ENCORE_DRY_RUN", "ENCORE_DATA_DIR", "LOG_LEVEL",
        "ENCORE_FETCH_MODE", "ENCORE_SLSKD_URL", "ENCORE_SLSKD_API_KEY", "ENCORE_SLSKD_DOWNLOADS_DIR", "ENCORE_RADIO_DIR",
        "ENCORE_RADIO_JELLYFIN_DIR", "ENCORE_SLSK_SEARCH_LIMIT", "ENCORE_FETCH_CONCURRENCY",
    ];

    /// <summary>The variable names Encore reads (used by tests).</summary>
    public static IReadOnlyList<string> Variables => KnownVariables;

    /// <summary>Reads the Encore variables from the process environment.</summary>
    public static ConfigResult LoadFromEnvironment()
    {
        var env = new Dictionary<string, string?>();
        foreach (var name in KnownVariables)
        {
            env[name] = Environment.GetEnvironmentVariable(name);
        }

        return Load(env);
    }

    /// <summary>Validates every variable and reports all problems together.</summary>
    public static ConfigResult Load(IDictionary<string, string?> env)
    {
        var errors = new List<ConfigError>();

        var url = ParseUrl(Get(env, "JELLYFIN_URL"), errors);
        var key = ParseKey(Get(env, "JELLYFIN_API_KEY"), errors);
        var users = ParseUsers(Get(env, "ENCORE_ALLOWED_USERS"), errors);

        var lookahead = ParseInt(env, "ENCORE_LOOKAHEAD", 5, 1, 20, errors, out var lookaheadOk);
        var batchMax = lookaheadOk ? lookahead : 20;
        var batchRange = lookaheadOk ? $"1..ENCORE_LOOKAHEAD ({lookahead})" : "1..ENCORE_LOOKAHEAD";
        var batch = ParseInt(env, "ENCORE_BATCH", 2, 1, batchMax, errors, out _, batchRange);
        var poll = ParseInt(env, "ENCORE_POLL_SECONDS", 10, 5, 300, errors, out _);

        var window = RecentWindow.Session;
        var windowText = Get(env, "ENCORE_RECENT_WINDOW");
        if (windowText is not null)
        {
            if (RecentWindow.TryParse(windowText, out var parsed))
            {
                window = parsed!;
            }
            else
            {
                errors.Add(new("ENCORE_RECENT_WINDOW",
                    $"ENCORE_RECENT_WINDOW must be \"session\" or a duration from 1m to 30d such as 30m, 6h, 2d, got \"{Truncate(windowText)}\""));
            }
        }

        var dryRun = true;
        var dryText = Get(env, "ENCORE_DRY_RUN");
        if (dryText is not null)
        {
            switch (dryText.ToLowerInvariant())
            {
                case "true" or "1" or "yes":
                    dryRun = true;
                    break;
                case "false" or "0" or "no":
                    dryRun = false;
                    break;
                default:
                    errors.Add(new("ENCORE_DRY_RUN", $"ENCORE_DRY_RUN must be true, false, 1, 0, yes or no, got \"{Truncate(dryText)}\""));
                    break;
            }
        }

        var logLevel = LogLevel.Information;
        var levelText = Get(env, "LOG_LEVEL");
        if (levelText is not null)
        {
            var parsedLevel = levelText.ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "information" => LogLevel.Information,
                "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => (LogLevel?)null,
            };
            if (parsedLevel is { } level)
            {
                logLevel = level;
            }
            else
            {
                errors.Add(new("LOG_LEVEL", $"LOG_LEVEL must be one of Trace, Debug, Information, Warning, Error, got \"{Truncate(levelText)}\""));
            }
        }

        var dataDir = Get(env, "ENCORE_DATA_DIR") ?? "/data";
        var dataError = CheckDataDir(dataDir);
        if (dataError is not null)
        {
            errors.Add(new("ENCORE_DATA_DIR", dataError));
        }

        var warnings = new List<ConfigError>();
        var fetchModeText = Get(env, "ENCORE_FETCH_MODE");
        var fetchMode = ParseFetchMode(fetchModeText, errors, out var modeKnown);
        Uri? slskdUrl = null;
        string? slskdKey = null;
        // An unknown mode still gets its URL and key checked, so every problem shows in one run.
        if (fetchMode != FetchMode.Off || !modeKnown)
        {
            slskdUrl = ParseSlskdUrl(Get(env, "ENCORE_SLSKD_URL"), errors);
            slskdKey = ParseSlskdKey(Get(env, "ENCORE_SLSKD_API_KEY"), errors);
        }

        var downloadsDir = Get(env, "ENCORE_SLSKD_DOWNLOADS_DIR") ?? "/slskd-downloads";
        var radioDir = Get(env, "ENCORE_RADIO_DIR") ?? "/music-radio";
        var radioJellyfinDir = ParseJellyfinDir(Get(env, "ENCORE_RADIO_JELLYFIN_DIR") ?? "/music-radio", errors);
        var searchLimit = ParseSearchLimit(Get(env, "ENCORE_SLSK_SEARCH_LIMIT"), errors);
        var concurrency = ParseInt(env, "ENCORE_FETCH_CONCURRENCY", 2, 1, 5, errors, out _);

        if (fetchMode == FetchMode.Live)
        {
            if (dryText is not null && dryRun)
            {
                errors.Add(new("ENCORE_FETCH_MODE",
                    "ENCORE_FETCH_MODE=live downloads songs but ENCORE_DRY_RUN=true sends none; use dry_run, or set ENCORE_DRY_RUN=false"));
            }

            CheckLiveDirs(downloadsDir, radioDir, errors, warnings);
        }

        if (errors.Count > 0)
        {
            return new ConfigResult(null, errors, warnings);
        }

        var options = new EncoreOptions
        {
            JellyfinUrl = url!,
            JellyfinApiKey = key!,
            AllowedUsers = users!,
            Lookahead = lookahead,
            Batch = batch,
            PollSeconds = poll,
            RecentWindow = window,
            DryRun = dryRun,
            DataDir = dataDir,
            LogLevel = logLevel,
            DeviceId = DeviceIdentity.LoadOrCreate(dataDir),
            FetchMode = fetchMode,
            SlskdUrl = slskdUrl,
            SlskdApiKey = slskdKey,
            SlskdDownloadsDir = downloadsDir,
            RadioDir = radioDir,
            RadioJellyfinDir = radioJellyfinDir!,
            SearchLimit = searchLimit,
            FetchConcurrency = concurrency,
        };
        return new ConfigResult(options, errors, warnings);
    }

    /// <summary>Returns the trimmed value, or null when the variable is unset or blank.</summary>
    private static string? Get(IDictionary<string, string?> env, string name) =>
        env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static Uri? ParseUrl(string? text, List<ConfigError> errors)
    {
        if (text is null)
        {
            errors.Add(new("JELLYFIN_URL", "JELLYFIN_URL is required (for example http://jellyfin:8096)"));
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            errors.Add(new("JELLYFIN_URL", "JELLYFIN_URL must be an absolute http or https URL (for example http://jellyfin:8096)"));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add(new("JELLYFIN_URL", "JELLYFIN_URL must not contain a user name or password"));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add(new("JELLYFIN_URL", "JELLYFIN_URL must not contain a query string or fragment"));
            return null;
        }

        // Trim trailing slashes so later code can append paths that start with a single slash.
        var trimmed = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(trimmed, UriKind.Absolute);
    }

    private static string? ParseKey(string? text, List<ConfigError> errors)
    {
        if (text is null)
        {
            errors.Add(new("JELLYFIN_API_KEY", "JELLYFIN_API_KEY is missing"));
            return null;
        }

        if (text.Any(char.IsWhiteSpace))
        {
            errors.Add(new("JELLYFIN_API_KEY", "JELLYFIN_API_KEY contains whitespace"));
            return null;
        }

        return text;
    }

    private static List<string>? ParseUsers(string? text, List<ConfigError> errors)
    {
        var users = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part))
            {
                users.Add(part);
            }
        }

        if (users.Count == 0)
        {
            errors.Add(new("ENCORE_ALLOWED_USERS", "ENCORE_ALLOWED_USERS must list at least one Jellyfin user name, comma separated"));
            return null;
        }

        return users;
    }

    private static int ParseInt(IDictionary<string, string?> env, string name, int fallback, int min, int max,
        List<ConfigError> errors, out bool ok, string? rangeText = null)
    {
        ok = true;
        var text = Get(env, name);
        if (text is null)
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        ok = false;
        errors.Add(new(name, $"{name} must be {rangeText ?? $"{min}..{max}"}, got \"{Truncate(text)}\""));
        return fallback;
    }

    private static string? CheckDataDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"ENCORE_DATA_DIR \"{Truncate(dir)}\" must exist (or be creatable) and be writable ({ex.GetType().Name})";
        }
    }

    private static FetchMode ParseFetchMode(string? text, List<ConfigError> errors, out bool known)
    {
        known = true;
        if (text is null)
        {
            return FetchMode.Off;
        }

        switch (text.ToLowerInvariant())
        {
            case "off":
                return FetchMode.Off;
            case "dry_run" or "dry-run" or "dryrun":
                return FetchMode.DryRun;
            case "live":
                return FetchMode.Live;
            default:
                known = false;
                errors.Add(new("ENCORE_FETCH_MODE", $"ENCORE_FETCH_MODE must be off, dry_run or live, got \"{Truncate(text)}\""));
                return FetchMode.Off;
        }
    }

    private static Uri? ParseSlskdUrl(string? text, List<ConfigError> errors)
    {
        const string name = "ENCORE_SLSKD_URL";
        if (text is null)
        {
            errors.Add(new(name, "ENCORE_SLSKD_URL is required when ENCORE_FETCH_MODE is not off (for example http://gluetun-slskd:5030)"));
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            errors.Add(new(name, "ENCORE_SLSKD_URL must be an absolute http or https URL (for example http://gluetun-slskd:5030)"));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add(new(name, "ENCORE_SLSKD_URL must not contain a user name or password"));
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || text.Contains('?') || text.Contains('#'))
        {
            errors.Add(new(name, "ENCORE_SLSKD_URL must not contain a query string or fragment"));
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), UriKind.Absolute);
    }

    private static string? ParseSlskdKey(string? text, List<ConfigError> errors)
    {
        const string name = "ENCORE_SLSKD_API_KEY";
        if (text is null)
        {
            errors.Add(new(name, "ENCORE_SLSKD_API_KEY is required when ENCORE_FETCH_MODE is not off"));
            return null;
        }

        if (text.Any(char.IsWhiteSpace))
        {
            errors.Add(new(name, "ENCORE_SLSKD_API_KEY contains whitespace"));
            return null;
        }

        // The X-API-Key header refuses control characters; catching them here gives a config error (exit 2), not a crash at the first slskd call.
        if (text.Any(char.IsControl))
        {
            errors.Add(new(name, "ENCORE_SLSKD_API_KEY contains a control character"));
            return null;
        }

        if (text.Length is < 16 or > 255)
        {
            errors.Add(new(name, "ENCORE_SLSKD_API_KEY must be 16 to 255 characters"));
            return null;
        }

        return text;
    }

    private static string? ParseJellyfinDir(string text, List<ConfigError> errors)
    {
        const string name = "ENCORE_RADIO_JELLYFIN_DIR";
        if (!text.StartsWith('/') || text.Contains('\\') || text.Split('/').Contains(".."))
        {
            errors.Add(new(name, "ENCORE_RADIO_JELLYFIN_DIR must be an absolute path starting with / (no .. segment, no backslash)"));
            return null;
        }

        var trimmed = text.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            errors.Add(new(name, "ENCORE_RADIO_JELLYFIN_DIR must not be the root folder"));
            return null;
        }

        return trimmed;
    }

    private static SearchLimit ParseSearchLimit(string? text, List<ConfigError> errors)
    {
        var fallback = new SearchLimit(34, 220);
        if (text is null)
        {
            return fallback;
        }

        var parts = text.Split('/');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var searches) && searches is >= 1 and <= 100
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 10 and <= 3600)
        {
            return new SearchLimit(searches, seconds);
        }

        errors.Add(new("ENCORE_SLSK_SEARCH_LIMIT",
            $"ENCORE_SLSK_SEARCH_LIMIT must be <searches>/<seconds> with searches 1..100 and seconds 10..3600, got \"{Truncate(text)}\""));
        return fallback;
    }

    private static void CheckLiveDirs(string downloadsDir, string radioDir, List<ConfigError> errors, List<ConfigError> warnings)
    {
        try
        {
            if (!Directory.Exists(downloadsDir))
            {
                errors.Add(new("ENCORE_SLSKD_DOWNLOADS_DIR", $"ENCORE_SLSKD_DOWNLOADS_DIR \"{Truncate(downloadsDir)}\" must exist in live mode"));
            }
            else
            {
                using var entries = Directory.EnumerateFileSystemEntries(downloadsDir).GetEnumerator();
                entries.MoveNext();
                if (!CanWrite(downloadsDir))
                {
                    warnings.Add(new("ENCORE_SLSKD_DOWNLOADS_DIR",
                        $"ENCORE_SLSKD_DOWNLOADS_DIR \"{Truncate(downloadsDir)}\" is not writable; copies in slskd's downloads folder will stay behind"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add(new("ENCORE_SLSKD_DOWNLOADS_DIR", $"ENCORE_SLSKD_DOWNLOADS_DIR \"{Truncate(downloadsDir)}\" must be readable in live mode ({ex.GetType().Name})"));
        }

        if (!Directory.Exists(radioDir) || !CanWrite(radioDir))
        {
            errors.Add(new("ENCORE_RADIO_DIR", $"ENCORE_RADIO_DIR \"{Truncate(radioDir)}\" must exist and be writable in live mode"));
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".encore-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Truncate(string s) => s.Length <= 100 ? s : s[..100] + "...";
}
