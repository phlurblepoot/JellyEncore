using System.Text;
using Microsoft.Extensions.Logging;

namespace Encore.Config;

/// <summary>Validated Encore settings. The API key is held here but never printed: <see cref="ToString"/> redacts it.</summary>
public sealed record EncoreOptions
{
    public required Uri JellyfinUrl { get; init; }

    public required string JellyfinApiKey { get; init; }

    public required IReadOnlyList<string> AllowedUsers { get; init; }

    public int Lookahead { get; init; } = 5;

    public int Batch { get; init; } = 2;

    public int PollSeconds { get; init; } = 10;

    public RecentWindow RecentWindow { get; init; } = RecentWindow.Session;

    public bool DryRun { get; init; } = true;

    public required string DataDir { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary>Stable per-install device id (<c>encore-</c> plus 12 hex digits), set once the data dir is validated.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>Jellyfin socket URL: same host, ws/wss scheme, <c>/socket</c> path. Carries no key.</summary>
    public Uri WebSocketUrl
    {
        get
        {
            var builder = new UriBuilder(JellyfinUrl)
            {
                Scheme = JellyfinUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Path = JellyfinUrl.AbsolutePath.TrimEnd('/') + "/socket",
            };
            return builder.Uri;
        }
    }

    // The compiler-generated printing would include the key, so replace it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"JellyfinUrl = {JellyfinUrl}, JellyfinApiKey = <redacted>, AllowedUsers = [{string.Join(", ", AllowedUsers)}], ")
            .Append($"Lookahead = {Lookahead}, Batch = {Batch}, PollSeconds = {PollSeconds}, RecentWindow = {RecentWindow}, ")
            .Append($"DryRun = {DryRun}, DataDir = {DataDir}, LogLevel = {LogLevel}, DeviceId = {DeviceId}");
        return true;
    }
}
