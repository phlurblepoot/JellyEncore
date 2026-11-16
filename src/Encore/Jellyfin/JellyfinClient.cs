using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Encore.Jellyfin;

/// <summary>Timeouts and retry policy for <see cref="JellyfinClient"/>. Defaults are the v0.1 design values.</summary>
public sealed record JellyfinClientOptions
{
    /// <summary>Per attempt. Applied as <c>HttpClient.Timeout</c>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits before each GET retry. Two entries give two retries on 5xx.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];

    /// <summary>Each wait is scaled by a random factor in [1-Jitter, 1+Jitter].</summary>
    public double Jitter { get; init; } = 0.2;
}

/// <summary>
/// Jellyfin REST client. Auth comes from the <see cref="JellyfinAuthHandler"/> in front of the HttpClient, so every call is authorized.
/// Retry rules (design section 1.2):
/// <list type="bullet">
/// <item>GET: retried on any 5xx, up to <see cref="JellyfinClientOptions.RetryDelays"/> times.</item>
/// <item>PlayLast: retried once, only on 502, 503 or 504, because a retry after a dropped reply could queue songs twice.</item>
/// <item>Timeouts and transport errors: never retried. 401 and 403 are never retried either.
/// A timeout, or a transport error after the connection was open, sets <see cref="JellyfinException.OutcomeUnknown"/>.</item>
/// </list>
/// </summary>
public sealed class JellyfinClient : IJellyfinClient, IRetentionJellyfin
{
    private readonly HttpClient _http;
    private readonly JellyfinClientOptions _options;

    public JellyfinClient(HttpClient http, JellyfinClientOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_http.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be the Jellyfin base URL (with a trailing slash)", nameof(http));
        }
    }

    public async Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) =>
        await GetAsync<List<SessionInfoDto>>("Sessions", "GetSessions", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
        await GetAsync<List<UserDto>>("Users", "GetUsers", ct).ConfigureAwait(false);

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default)
    {
        RequirePositive(limit);
        var query = Query(("userId", Id(userId)), ("limit", limit.ToString()));
        return GetAsync<QueryResult<BaseItemDto>>($"Artists/{Id(artistId)}/Similar?{query}", "GetSimilarArtists", ct);
    }

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default)
    {
        RequirePositive(limit);
        ArgumentNullException.ThrowIfNull(artistIds);
        if (artistIds.Count == 0)
        {
            throw new ArgumentException("at least one artist id is required", nameof(artistIds));
        }

        var query = Query(
            ("artistIds", string.Join(",", artistIds.Select(Id))),
            ("includeItemTypes", "Audio"),
            ("recursive", "true"),
            ("sortBy", "Random"),
            ("userId", Id(userId)),
            ("limit", limit.ToString()));
        return GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetSongsByArtists", ct);
    }

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default)
    {
        RequirePositive(limit);
        var query = Query(
            ("userId", Id(userId)),
            ("limit", limit.ToString()),
            ("excludeArtistIds", excludeArtistIds is { Count: > 0 } ? string.Join(",", excludeArtistIds.Select(Id)) : null));
        return GetAsync<QueryResult<BaseItemDto>>($"Items/{Id(songId)}/Similar?{query}", "GetSimilarSongs", ct);
    }

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default)
    {
        RequirePositive(limit);
        var query = Query(("userId", Id(userId)), ("limit", limit.ToString()));
        return GetAsync<QueryResult<BaseItemDto>>($"Items/{Id(songId)}/InstantMix?{query}", "GetInstantMix", ct);
    }

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default)
    {
        RequirePositive(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        var query = Query(
            ("recursive", "true"),
            ("includeItemTypes", "Audio"),
            ("fields", "Path,DateCreated"),
            ("startIndex", startIndex.ToString(CultureInfo.InvariantCulture)),
            ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("enableTotalRecordCount", "true"));
        return GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetAudioPage", ct);
    }

    public async Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(fields);
        if (ids.Count == 0)
        {
            return [];
        }

        var query = Query(("ids", string.Join(",", ids.Select(Id))), ("fields", Uri.EscapeDataString(fields).Replace("%2C", ",", StringComparison.Ordinal)));
        var result = await GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetItemsByIds", ct).ConfigureAwait(false);
        return result.Items;
    }

    public async Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jellyfinPaths);
        if (jellyfinPaths.Count == 0)
        {
            throw new ArgumentException("at least one path is required", nameof(jellyfinPaths));
        }

        var body = JsonSerializer.Serialize(new
        {
            Updates = jellyfinPaths.Select(p => new { Path = p, UpdateType = "Created" }).ToArray(),
        }, MediaUpdatedJson);

        // No retries: the call is idempotent and the next ingest repeats it.
        using var response = await SendAsync(
            HttpMethod.Post, "Library/Media/Updated", "ReportMediaUpdated", maxRetries: 0, _ => false, ct,
            () => new StringContent(body, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
    }

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default) =>
        GetAsync<PublicSystemInfo>("System/Info/Public", "GetPublicSystemInfo", ct);

    public async Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default) =>
        await GetAsync<List<VirtualFolderInfo>>("Library/VirtualFolders", "GetVirtualFolders", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default) =>
        await GetAsync<List<string>>("Library/PhysicalPaths", "GetPhysicalPaths", ct).ConfigureAwait(false);

    public Task<QueryResult<BaseItemDto>> SearchItemsAsync(string searchTerm, Guid userId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);
        RequirePositive(limit);
        var query = Query(
            ("searchTerm", Uri.EscapeDataString(searchTerm.Trim())),
            ("includeItemTypes", "Audio,MusicAlbum"),
            ("recursive", "true"),
            ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("userId", Id(userId)));
        return GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "SearchItems", ct);
    }

    public async Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default)
    {
        RequirePositive(limit);
        var query = Query(
            ("parentId", Id(parentId)),
            ("recursive", "true"),
            ("includeItemTypes", "Audio"),
            ("fields", "Path,DateCreated"),
            ("sortBy", "DateCreated"),
            ("sortOrder", "Descending"),
            ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("enableTotalRecordCount", "false"));
        var result = await GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetRecentAudio", ct).ConfigureAwait(false);
        return result.Items;
    }

    private static readonly JsonSerializerOptions MediaUpdatedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            throw new ArgumentException("at least one item id is required", nameof(itemIds));
        }

        var query = Query(("playCommand", "PlayLast"), ("itemIds", string.Join(",", itemIds.Select(Id))));
        var path = $"Sessions/{Uri.EscapeDataString(sessionId)}/Playing?{query}";

        // Once, and only on gateway errors: a retry after a dropped reply could queue the songs twice.
        using var response = await SendAsync(HttpMethod.Post, path, "PlayLast", maxRetries: 1, IsGatewayError, ct).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string sessionId, string header, string text, int timeoutMs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var body = JsonSerializer.Serialize(new { Header = header, Text = text, TimeoutMs = timeoutMs }, MediaUpdatedJson);

        // No retries here: the notifier owns the one retry (a repeated message is only a second toast).
        using var response = await SendAsync(
            HttpMethod.Post, $"Sessions/{Uri.EscapeDataString(sessionId)}/Message", "SendMessage", maxRetries: 0, _ => false, ct,
            () => new StringContent(body, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
    }

    // ---- Retention calls (IRetentionJellyfin, v04 §13) ----------------------------------------------

    private const int FavoritePageSize = 500;

    public async Task<IReadOnlyList<BaseItemDto>> GetFavoriteAudioAsync(Guid userId, Guid? parentId = null, CancellationToken ct = default)
    {
        var all = new List<BaseItemDto>();
        for (var start = 0; ; start += FavoritePageSize)
        {
            var query = Query(
                ("userId", Id(userId)),
                ("isFavorite", "true"),
                ("includeItemTypes", "Audio"),
                ("recursive", "true"),
                ("parentId", parentId is { } p ? Id(p) : null),
                ("fields", "Path"),
                ("enableUserData", "true"),
                ("startIndex", start.ToString(CultureInfo.InvariantCulture)),
                ("limit", FavoritePageSize.ToString(CultureInfo.InvariantCulture)),
                ("enableTotalRecordCount", "true"));
            var page = await GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetFavoriteAudio", ct).ConfigureAwait(false);
            all.AddRange(page.Items);
            if (page.Items.Count < FavoritePageSize || (page.TotalRecordCount > 0 && all.Count >= page.TotalRecordCount))
            {
                return all;
            }
        }
    }

    public async Task<IReadOnlyList<PlaylistRef>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default)
    {
        var query = Query(("userId", Id(userId)), ("includeItemTypes", "Playlist"), ("recursive", "true"));
        var result = await GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetPlaylists", ct).ConfigureAwait(false);
        return result.Items.Select(i => new PlaylistRef(i.Id, i.Name)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        var query = Query(("userId", Id(userId)));
        var result = await GetAsync<QueryResult<BaseItemDto>>($"Playlists/{Id(playlistId)}/Items?{query}", "GetPlaylistEntries", ct).ConfigureAwait(false);
        return result.Items.Select(i => i.Id).ToList();
    }

    public Task<PlaylistWriteResult> AddToPlaylistAsync(Guid playlistId, Guid itemId, Guid userId, int position, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        var query = Query(("ids", Id(itemId)), ("userId", Id(userId)), ("position", position.ToString(CultureInfo.InvariantCulture)));
        return WriteAsync(HttpMethod.Post, $"Playlists/{Id(playlistId)}/Items?{query}", "AddToPlaylist", ct);
    }

    public Task<PlaylistWriteResult> RemovePlaylistEntriesAsync(Guid playlistId, IReadOnlyCollection<Guid> entryIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            throw new ArgumentException("at least one entry id is required", nameof(entryIds));
        }

        var query = Query(("entryIds", string.Join(",", entryIds.Select(Id))));
        return WriteAsync(HttpMethod.Delete, $"Playlists/{Id(playlistId)}/Items?{query}", "RemovePlaylistEntries", ct);
    }

    public async Task MarkFavoriteAsync(Guid itemId, Guid userId, CancellationToken ct = default)
    {
        var query = Query(("userId", Id(userId)));
        using var response = await SendAsync(HttpMethod.Post, $"UserFavoriteItems/{Id(itemId)}?{query}", "MarkFavorite", maxRetries: 0, _ => false, ct).ConfigureAwait(false);
    }

    public async Task<UserItemData?> GetUserItemDataAsync(Guid itemId, Guid userId, CancellationToken ct = default)
    {
        var query = Query(("ids", Id(itemId)), ("userId", Id(userId)), ("enableUserData", "true"));
        var result = await GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "GetUserItemData", ct).ConfigureAwait(false);
        var item = result.Items.FirstOrDefault();
        return item is null ? null : new UserItemData(item.UserData?.IsFavorite ?? false, item.UserData?.PlayCount ?? 0);
    }

    public async Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default)
    {
        var query = Query(("ids", Id(itemId)));
        var result = await GetAsync<QueryResult<BaseItemDto>>($"Items?{query}", "ItemExists", ct).ConfigureAwait(false);
        return result.Items.Count > 0;
    }

    public async Task<SmartListsInfo> GetSmartListsAsync(CancellationToken ct = default)
    {
        var plugins = await GetAsync<List<PluginInfoDto>>("Plugins", "GetPlugins", ct).ConfigureAwait(false);
        if (!plugins.Any(p => p.Name?.Contains("SmartLists", StringComparison.OrdinalIgnoreCase) == true))
        {
            return SmartListsInfo.NotInstalled;
        }

        var failed = new SmartListsInfo(true, false, new HashSet<Guid>());
        try
        {
            var lists = await GetAsync<List<SmartListDtoLite>>("Plugins/SmartLists?type=Playlist", "GetSmartLists", ct).ConfigureAwait(false);
            var ids = new HashSet<Guid>();
            foreach (var list in lists)
            {
                if (list is null)
                {
                    return failed;
                }

                if (!AddId(list.JellyfinPlaylistId, ids))
                {
                    return failed;
                }

                foreach (var mapping in list.UserPlaylists ?? [])
                {
                    if (mapping is null || !AddId(mapping.JellyfinPlaylistId, ids))
                    {
                        return failed;
                    }
                }
            }

            return new SmartListsInfo(true, true, ids);
        }
        catch (JellyfinException)
        {
            // A refusal, 404, 5xx, timeout, dropped connection or unreadable body: the list is unknown, so the caller must treat it as a failed read.
            return failed;
        }
    }

    /// <summary>Adds a plugin playlist id. True when it is absent or a valid id; false when it is present but not an id.</summary>
    private static bool AddId(string? value, HashSet<Guid> ids)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!Guid.TryParse(value, out var id))
        {
            return false;
        }

        ids.Add(id);
        return true;
    }

    /// <summary>A write that is never retried. 403 and 404 come back as results; 401 and every other failure throw.</summary>
    private async Task<PlaylistWriteResult> WriteAsync(HttpMethod method, string path, string operation, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(method, path, operation, maxRetries: 0, _ => false, ct).ConfigureAwait(false);
            return PlaylistWriteResult.Ok;
        }
        catch (JellyfinAuthException e) when (e.StatusCode == 403)
        {
            return PlaylistWriteResult.Forbidden;
        }
        catch (JellyfinRequestException e) when (e.StatusCode == 404)
        {
            return PlaylistWriteResult.NotFound;
        }
    }

    private async Task<T> GetAsync<T>(string path, string operation, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, operation, _options.RetryDelays.Count, IsServerError, ct).ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadFromJsonAsync<T>(JellyfinJson.Options, ct).ConfigureAwait(false);
            return body ?? throw new JellyfinRequestException(operation, "empty response body");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JellyfinRequestException(operation, "timed out reading the response");
        }
        catch (JsonException)
        {
            throw new JellyfinRequestException(operation, "response was not the expected JSON");
        }
        catch (NotSupportedException)
        {
            throw new JellyfinRequestException(operation, "response was not the expected JSON");
        }
    }

    /// <summary>Sends one call with its retry rule. The returned response is successful; the caller disposes it.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string operation, int maxRetries, Func<int, bool> isRetryable, CancellationToken ct, Func<HttpContent>? content = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, path) { Content = content?.Invoke() };
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // HttpClient.Timeout fired, not the caller. Timeouts are never retried, and the request may have arrived.
                throw new JellyfinRequestException(operation, "timed out", outcomeUnknown: true);
            }
            catch (HttpRequestException e)
            {
                // Transport failure. Keep only the error kind, never the message or the request.
                throw new JellyfinRequestException(operation, $"connection failed ({e.HttpRequestError})", outcomeUnknown: !FailedBeforeSending(e.HttpRequestError));
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            response.Dispose();

            if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
            {
                throw new JellyfinAuthException(operation, status);
            }

            if (attempt < maxRetries && isRetryable(status))
            {
                await Task.Delay(BackoffFor(attempt), ct).ConfigureAwait(false);
                continue;
            }

            throw new JellyfinRequestException(operation, status);
        }
    }

    private TimeSpan BackoffFor(int attempt)
    {
        var delays = _options.RetryDelays;
        if (delays.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var baseDelay = delays[Math.Min(attempt, delays.Count - 1)];
        var factor = 1 + _options.Jitter * ((2 * Random.Shared.NextDouble()) - 1);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseDelay.TotalMilliseconds * factor));
    }

    private static bool IsServerError(int status) => status is >= 500 and <= 599;

    private static bool IsGatewayError(int status) => status is 502 or 503 or 504;

    /// <summary>
    /// Errors raised while opening the connection, before any of the request was written. Every other kind (a reset or
    /// an ended response, a protocol error, an unknown error) may come after Jellyfin got the request.
    /// </summary>
    private static bool FailedBeforeSending(HttpRequestError error) => error is
        HttpRequestError.NameResolutionError
        or HttpRequestError.ConnectionError
        or HttpRequestError.SecureConnectionError
        or HttpRequestError.ProxyTunnelError;

    private static void RequirePositive(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "must be at least 1");
        }
    }

    /// <summary>Canonical 32-digit lowercase hex form, which Jellyfin uses in its own JSON.</summary>
    private static string Id(Guid id) => id.ToString("N");

    /// <summary>Builds a query string. Values must already be URL-safe (ids, numbers, fixed words). Only the caller's free text is escaped, in the path.</summary>
    private static string Query(params (string Name, string? Value)[] pairs) =>
        string.Join('&', pairs.Where(p => p.Value is not null).Select(p => $"{p.Name}={p.Value}"));
}
