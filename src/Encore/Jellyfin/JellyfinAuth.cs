using System.Net.Http.Headers;
using System.Reflection;

namespace Encore.Jellyfin;

/// <summary>
/// Builds the single Jellyfin auth header Encore sends: <c>Authorization: MediaBrowser Token="..."</c>.
/// Encore never sends <c>X-Emby-*</c>, <c>X-MediaBrowser-Token</c>, or an <c>api_key</c>/<c>ApiKey</c> query parameter.
/// </summary>
public static class JellyfinAuth
{
    public const string Scheme = "MediaBrowser";
    public const string ClientName = "Encore";
    public const string DeviceName = "Encore";

    /// <summary>
    /// The parameter part of the header, e.g. <c>Token="KEY", Client="Encore", Device="Encore", DeviceId="...", Version="..."</c>.
    /// Throws with the parameter NAME only, never its value, so the API key cannot leak through an error.
    /// </summary>
    public static string BuildParameter(string apiKey, string deviceId, string version)
    {
        RequireSafe(nameof(apiKey), apiKey);
        RequireSafe(nameof(deviceId), deviceId);
        RequireSafe(nameof(version), version);
        return $"Token=\"{apiKey}\", Client=\"{ClientName}\", Device=\"{DeviceName}\", DeviceId=\"{deviceId}\", Version=\"{version}\"";
    }

    /// <summary>Version of the running Encore assembly (the informational version, or 0.0.0).</summary>
    public static string VersionFromAssembly() =>
        typeof(JellyfinAuth).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static void RequireSafe(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("must not be blank", name);
        }

        // Quotes, backslashes and control characters would break the quoted-string form of the header.
        if (value.Any(c => c is '"' or '\\' || char.IsControl(c)))
        {
            throw new ArgumentException("contains a character that is not allowed in the auth header", name);
        }
    }
}

/// <summary>Adds the Jellyfin Authorization header to every request that passes through it.</summary>
public sealed class JellyfinAuthHandler : DelegatingHandler
{
    private readonly string _authorization;

    /// <param name="parameter">The output of <see cref="JellyfinAuth.BuildParameter"/>.</param>
    public JellyfinAuthHandler(string parameter)
    {
        _authorization = $"{JellyfinAuth.Scheme} {parameter}";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        return base.SendAsync(request, cancellationToken);
    }
}
