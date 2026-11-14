using System.Globalization;

namespace Encore.Config;

public sealed record ConfigResult(EncoreOptions? Options, IReadOnlyList<ConfigError> Errors)
{
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
    ];

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

        if (errors.Count > 0)
        {
            return new ConfigResult(null, errors);
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
        };
        return new ConfigResult(options, errors);
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

    private static string Truncate(string s) => s.Length <= 100 ? s : s[..100] + "...";
}
