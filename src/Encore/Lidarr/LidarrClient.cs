using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Encore.Lidarr;

/// <summary>Timeouts and retry waits for <see cref="LidarrClient"/>.</summary>
public sealed record LidarrClientOptions
{
    /// <summary>Per call. Applied as <c>HttpClient.Timeout</c> (v03 design: Lidarr 10 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Wait before the one retry of a GET that got a 5xx.</summary>
    public TimeSpan GetRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often <see cref="LidarrStatusMonitor"/> asks system/status.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Lidarr client over a typed HttpClient. The <see cref="LidarrAuthHandler"/> in front of it adds the API key, so this
/// class never sees it. Retries: GETs once on 5xx; writes never (a retried POST could add twice).
/// </summary>
public sealed partial class LidarrClient : ILidarrClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LidarrClientOptions _options;

    public LidarrClient(HttpClient http, LidarrClientOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_http.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be the Lidarr base URL (with a trailing slash)", nameof(http));
        }
    }

    public async Task<LidarrStatus> GetStatusAsync(CancellationToken ct)
    {
        var dto = await GetJsonAsync<StatusDto>("api/v1/system/status", "GetStatus", ct).ConfigureAwait(false);
        return new LidarrStatus(dto.AppName ?? "", dto.Version ?? "");
    }

    public async Task<IReadOnlyList<LidarrRootFolder>> GetRootFoldersAsync(CancellationToken ct)
    {
        var dtos = await GetJsonAsync<List<RootFolderDto>>("api/v1/rootfolder", "GetRootFolders", ct).ConfigureAwait(false);
        return dtos
            .Where(d => !string.IsNullOrEmpty(d.Path))
            .Select(d => new LidarrRootFolder(d.Id, d.Path!, d.DefaultQualityProfileId, d.DefaultMetadataProfileId, d.Accessible))
            .ToList();
    }

    public async Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        return await GetAlbumListAsync($"api/v1/album/lookup?term={Uri.EscapeDataString(term)}", "LookupAlbums", ct).ConfigureAwait(false);
    }

    public async Task<LidarrArtist?> FindArtistByForeignIdAsync(string foreignArtistId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignArtistId);
        var nodes = await GetJsonAsync<JsonArray>($"api/v1/artist?mbId={Uri.EscapeDataString(foreignArtistId)}", "FindArtist", ct).ConfigureAwait(false);
        return nodes.OfType<JsonObject>().Select(ToArtist).FirstOrDefault();
    }

    public async Task<LidarrArtist?> GetArtistAsync(int id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/artist/{id}", "GetArtist", null, retryServerError: true, ct, allowNotFound: true).ConfigureAwait(false);
        return response is null ? null : ToArtist(await ReadAsync<JsonObject>(response, "GetArtist", ct).ConfigureAwait(false));
    }

    public async Task<LidarrArtist?> SetArtistMonitoredAsync(int id, bool monitored, CancellationToken ct)
    {
        var current = await GetArtistAsync(id, ct).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        // Send the resource back exactly as returned (the path in it must not change: Lidarr queues a move on every PUT).
        var body = (JsonObject)current.Raw.DeepClone();
        body["monitored"] = monitored;
        using var response = await SendAsync(HttpMethod.Put, $"api/v1/artist/{id}", "SetArtistMonitored", body, retryServerError: false, ct, allowNotFound: true).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        var answer = await TryReadObjectAsync(response, ct).ConfigureAwait(false);
        return ToArtist(answer ?? body);
    }

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct) =>
        GetAlbumListAsync($"api/v1/album?artistId={artistId}", "GetAlbumsByArtist", ct);

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, bool includeAllArtistAlbums, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignAlbumId);
        var path = $"api/v1/album?foreignAlbumId={Uri.EscapeDataString(foreignAlbumId)}";
        if (includeAllArtistAlbums)
        {
            path += "&includeAllArtistAlbums=true";
        }

        return GetAlbumListAsync(path, "GetAlbumsByForeignId", ct);
    }

    public async Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        return await GetAlbumListAsync("api/v1/album?" + string.Join('&', ids.Select(i => $"albumIds={i}")), "GetAlbumsByIds", ct).ConfigureAwait(false);
    }

    public async Task<LidarrAlbum?> GetAlbumAsync(int id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/album/{id}", "GetAlbum", null, retryServerError: true, ct, allowNotFound: true).ConfigureAwait(false);
        return response is null ? null : ToAlbum(await ReadAsync<AlbumDto>(response, "GetAlbum", ct).ConfigureAwait(false));
    }

    public async Task<int> CountTrackFilesAsync(int albumId, CancellationToken ct)
    {
        // An unknown album id is a 404 (ModelNotFoundException in Lidarr's error pipeline).
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/trackfile?albumId={albumId}", "CountTrackFiles", null, retryServerError: true, ct, allowNotFound: true).ConfigureAwait(false);
        if (response is null)
        {
            return 0;
        }

        // Only the number of rows matters; each row is large (media info, tags), so nothing is bound to a type.
        var rows = await ReadAsync<JsonArray>(response, "CountTrackFiles", ct).ConfigureAwait(false);
        return rows.Count(r => r is JsonObject);
    }

    public async Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ForeignAlbumId);
        ArgumentNullException.ThrowIfNull(request.Artist);
        var body = new
        {
            foreignAlbumId = request.ForeignAlbumId,
            title = request.Title,
            monitored = true,
            anyReleaseOk = true,
            addOptions = new { addType = "manual", searchForNewAlbum = false },
            artist = request.Artist,
        };

        using var response = await SendAsync(HttpMethod.Post, "api/v1/album", "AddAlbum", body, retryServerError: false, ct).ConfigureAwait(false);
        return ToAlbum(await ReadAsync<AlbumDto>(response!, "AddAlbum", ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<LidarrAlbum>> SetAlbumsMonitoredAsync(IReadOnlyCollection<int> albumIds, bool monitored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(albumIds);
        if (albumIds.Count == 0)
        {
            throw new ArgumentException("at least one album id is required", nameof(albumIds));
        }

        var body = new { albumIds, monitored };
        using var response = await SendAsync(HttpMethod.Put, "api/v1/album/monitor", "SetAlbumsMonitored", body, retryServerError: false, ct).ConfigureAwait(false);
        var dtos = await ReadAsync<List<AlbumDto>>(response!, "SetAlbumsMonitored", ct).ConfigureAwait(false);
        return dtos.Select(ToAlbum).ToList();
    }

    private async Task<IReadOnlyList<LidarrAlbum>> GetAlbumListAsync(string path, string operation, CancellationToken ct)
    {
        var dtos = await GetJsonAsync<List<AlbumDto>>(path, operation, ct).ConfigureAwait(false);
        return dtos.Where(d => !string.IsNullOrEmpty(d.ForeignAlbumId)).Select(ToAlbum).ToList();
    }

    private async Task<T> GetJsonAsync<T>(string path, string operation, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, operation, null, retryServerError: true, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response!, operation, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct) where T : class
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
            return body ?? throw new LidarrRequestException(operation, "empty response body");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LidarrRequestException(operation, "timed out reading the response");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new LidarrRequestException(operation, "response was not the expected JSON");
        }
    }

    private static async Task<JsonObject?> TryReadObjectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends one call. Returns a successful response (the caller disposes it), or null when <paramref name="allowNotFound"/> is set and
    /// Lidarr said 404. 401/403 and 400 become their own exception types.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, string operation, object? body, bool retryServerError, CancellationToken ct,
        bool allowNotFound = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: Json);
                }

                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new LidarrRequestException(operation, "timed out");
            }
            catch (HttpRequestException e)
            {
                // Keep only the error kind, never the message (it can hold the URL).
                throw new LidarrRequestException(operation, $"connection failed ({e.HttpRequestError})");
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            try
            {
                switch (status)
                {
                    case 401 or 403:
                        throw new LidarrAuthException(operation, status);
                    case 400:
                        throw new LidarrValidationException(operation, await ReadFailuresAsync(response, ct).ConfigureAwait(false));
                    case 404 when allowNotFound:
                        return null;
                }
            }
            finally
            {
                response.Dispose();
            }

            if (retryServerError && attempt == 0 && status is >= 500 and <= 599)
            {
                await Task.Delay(_options.GetRetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            throw new LidarrRequestException(operation, status);
        }
    }

    /// <summary>Reads Lidarr's 400 array (PascalCase propertyName and errorMessage); another 400 shape gives an empty list.</summary>
    private static async Task<IReadOnlyList<LidarrValidationFailure>> ReadFailuresAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var node = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct).ConfigureAwait(false);
            if (node is not JsonArray array)
            {
                return [];
            }

            return array.OfType<JsonObject>()
                .Select(o => new LidarrValidationFailure(
                    ReadString(o, "propertyName") ?? "",
                    ReadString(o, "errorMessage") ?? ""))
                .ToList();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    // Lidarr writes PascalCase names in this array; accept any casing.
    private static string? ReadString(JsonObject o, string name)
    {
        foreach (var (key, value) in o)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                return s;
            }
        }

        return null;
    }

    private static LidarrArtist ToArtist(JsonObject raw)
    {
        static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var id = raw["id"] is JsonValue idv && idv.TryGetValue<int>(out var i) ? i : 0;
        var monitored = raw["monitored"] is JsonValue mv && mv.TryGetValue<bool>(out var m) && m;
        return new LidarrArtist(id, Str(raw, "artistName"), Str(raw, "foreignArtistId"), monitored, Str(raw, "monitorNewItems"), raw);
    }

    private static LidarrAlbum ToAlbum(AlbumDto d) =>
        new(d.Id, d.Title, d.Disambiguation, d.ArtistId, d.ForeignAlbumId ?? "", d.Monitored, d.AlbumType,
            d.SecondaryTypes ?? [], d.ReleaseDate,
            (d.Images ?? []).Select(i => new LidarrImage(i.CoverType, i.Url, i.RemoteUrl)).ToList(),
            d.RemoteCover,
            (d.Releases ?? []).Select(r => new LidarrRelease(r.ForeignReleaseId, r.Title, r.TrackCount, r.Format, r.Monitored)).ToList(),
            d.Artist is null ? null : new LidarrArtistRef(d.Artist.Id, d.Artist.ArtistName, d.Artist.ForeignArtistId, d.Artist.Monitored),
            d.Statistics is null ? null : new LidarrAlbumStatistics(
                d.Statistics.TrackFileCount, d.Statistics.TrackCount, d.Statistics.TotalTrackCount, d.Statistics.SizeOnDisk, d.Statistics.PercentOfTracks));

    private sealed record StatusDto(string? AppName, string? Version);

    private sealed record RootFolderDto(int Id, string? Path, int DefaultQualityProfileId, int DefaultMetadataProfileId, bool Accessible);

    private sealed record ImageDto(string? CoverType, string? Url, string? RemoteUrl);

    private sealed record ReleaseDto(string? ForeignReleaseId, string? Title, int TrackCount, string? Format, bool Monitored);

    private sealed record ArtistRefDto(int Id, string? ArtistName, string? ForeignArtistId, bool Monitored);

    private sealed record StatisticsDto(int TrackFileCount, int TrackCount, int TotalTrackCount, long SizeOnDisk, double PercentOfTracks);

    private sealed record AlbumDto(
        int Id, string? Title, string? Disambiguation, int ArtistId, string? ForeignAlbumId, bool Monitored, string? AlbumType,
        List<string>? SecondaryTypes, DateTimeOffset? ReleaseDate, List<ImageDto>? Images, string? RemoteCover,
        List<ReleaseDto>? Releases, ArtistRefDto? Artist, StatisticsDto? Statistics);
}
