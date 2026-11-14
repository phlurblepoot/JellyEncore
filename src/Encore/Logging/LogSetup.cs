using Microsoft.Extensions.Logging.Console;

namespace Encore.Logging;

/// <summary>Shared logging setup: single-line JSON on the console with UTC timestamps and scopes.</summary>
public static class LogSetup
{
    public static void ConfigureJson(JsonConsoleFormatterOptions options)
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
    }


    /// <summary>LOG_LEVEL sets the Encore category; framework and HTTP client categories stay at Warning.</summary>
    public static void ApplyFilters(ILoggingBuilder logging, LogLevel encoreLevel)
    {
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);
        logging.AddFilter("Encore", encoreLevel);
    }
}
