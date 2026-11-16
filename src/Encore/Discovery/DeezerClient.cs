using System.Globalization;
using System.Text.Json;

namespace Encore.Discovery;

public sealed record DeezerArtist(long Id, string Name, int NbFan, bool Radio);

public sealed record DeezerAlbumRef(long Id, string Title, string? ReleaseDate);

public sealed record DeezerTrack(long Id, string Title, string TitleShort, string? TitleVersion, int DurationS,
    long Rank, DeezerArtist Artist, DeezerAlbumRef Album);

/// <summary>Main or Featured.</summary>
public sealed record DeezerContributor(long Id, string Name, string Role);

public sealed record DeezerTrackDetail(long Id, string Title, string TitleShort, string? Isrc, int DurationS,
    int? TrackPosition, int? DiskNumber, string? ReleaseDate, DeezerArtist Artist, DeezerAlbumRef Album,
    IReadOnlyList<DeezerContributor> Contributors);

/// <summary>
/// One row of a Deezer text search (v0.3 design 5): only the fields the request page shows. Search rows have no release
/// date or genre. The signed <c>preview</c> link is never read.
/// </summary>
public sealed record DeezerSearchTrack(long Id, string Title, string? TitleVersion, string ArtistName, string AlbumTitle,
    int DurationS, string? CoverUrl, bool Explicit);

public interface IDeezerClient
{
    /// <summary>GET /search?q=&amp;limit=: songs by text, same limiter and 24 h cache as the other calls (cache key: the folded query). Rows are in Deezer's order.</summary>
    Task<IReadOnlyList<DeezerSearchTrack>> SearchTracksAsync(string query, int limit, CancellationToken ct);

    /// <summary>GET /search/artist?q=&amp;limit=10: among exact (folded) name matches the highest nb_fan; none gives null.</summary>
    Task<DeezerArtist?> FindArtistAsync(string name, CancellationToken ct);

    /// <summary>GET /artist/{id}/radio?limit=. Rows have no isrc.</summary>
    Task<IReadOnlyList<DeezerTrack>> GetArtistRadioAsync(long artistId, int limit, CancellationToken ct);

    /// <summary>GET /artist/{id}/related?limit=.</summary>
    Task<IReadOnlyList<DeezerArtist>> GetRelatedArtistsAsync(long artistId, int limit, CancellationToken ct);

    /// <summary>GET /artist/{id}/top?limit=.</summary>
    Task<IReadOnlyList<DeezerTrack>> GetArtistTopAsync(long artistId, int limit, CancellationToken ct);

    /// <summary>GET /track/{id}: the only call with isrc, track_position, disk_number and the track's release_date. Null when Deezer has no such track.</summary>
    Task<DeezerTrackDetail?> GetTrackAsync(long trackId, CancellationToken ct);
}

/// <summary>
/// Keyless Deezer client (https://api.deezer.com). Deezer reports errors as HTTP 200 with an <c>error</c> object:
/// code 800 means not found (null or empty, no retry); code 4 is the quota, which waits and retries once and then
/// fails with <see cref="DiscoveryFailure.Quota"/>. HTTP 5xx retries once after a short delay; a timeout is not retried.
/// Results are cached; failures never are.
/// </summary>
public sealed class DeezerClient(HttpClient http, DiscoveryRuntime runtime, ILogger<DeezerClient> logger) : IDeezerClient
{
    private const int NotFoundCode = 800;
    private const int QuotaCode = 4;

    private static readonly IReadOnlyList<DeezerTrack> NoTracks = [];
    private static readonly IReadOnlyList<DeezerArtist> NoArtists = [];
    private static readonly IReadOnlyList<DeezerSearchTrack> NoSearchTracks = [];

    public async Task<DeezerArtist?> FindArtistAsync(string name, CancellationToken ct)
    {
        var folded = NameFold.Fold(name);
        if (folded.Length == 0)
        {
            return null;
        }

        var key = "dz:artist:" + folded;
        if (runtime.Cache.TryGet<DeezerArtist>(key, out var cached))
        {
            return cached;
        }

        using var doc = await GetAsync("/search/artist", "search/artist?q=" + Uri.EscapeDataString(name.Trim()) + "&limit=10", ct).ConfigureAwait(false);
        DeezerArtist? best = null;
        foreach (var row in Rows(doc))
        {
            var artist = ParseArtist(row);
            if (artist is not null && NameFold.Fold(artist.Name) == folded && (best is null || artist.NbFan > best.NbFan))
            {
                best = artist;
            }
        }

        if (best is null)
        {
            logger.ArtistNotFound("deezer", name);
        }

        runtime.Cache.Set(key, best, best is null ? runtime.Options.MissTtl : runtime.Options.ArtistTtl);
        return best;
    }

    public async Task<IReadOnlyList<DeezerSearchTrack>> SearchTracksAsync(string query, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var folded = string.Join(' ', KeyText.Tokens(query));
        if (folded.Length == 0)
        {
            return NoSearchTracks;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"dz:search:{limit}:{folded}");
        if (runtime.Cache.TryGet<IReadOnlyList<DeezerSearchTrack>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        using var doc = await GetAsync(
            "/search",
            string.Create(CultureInfo.InvariantCulture, $"search?q={Uri.EscapeDataString(query.Trim())}&limit={limit}"),
            ct).ConfigureAwait(false);
        IReadOnlyList<DeezerSearchTrack> result = doc is null
            ? NoSearchTracks
            : Rows(doc).Select(ParseSearchTrack).OfType<DeezerSearchTrack>().ToList();
        runtime.Cache.Set(key, result, runtime.Options.ListTtl);
        return result;
    }

    public Task<IReadOnlyList<DeezerTrack>> GetArtistRadioAsync(long artistId, int limit, CancellationToken ct) =>
        GetTracksAsync("radio", artistId, limit, ct);

    public Task<IReadOnlyList<DeezerTrack>> GetArtistTopAsync(long artistId, int limit, CancellationToken ct) =>
        GetTracksAsync("top", artistId, limit, ct);

    public async Task<IReadOnlyList<DeezerArtist>> GetRelatedArtistsAsync(long artistId, int limit, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"dz:related:{artistId}:{limit}");
        if (runtime.Cache.TryGet<IReadOnlyList<DeezerArtist>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        using var doc = await GetAsync(
            $"/artist/{artistId}/related",
            string.Create(CultureInfo.InvariantCulture, $"artist/{artistId}/related?limit={limit}"),
            ct).ConfigureAwait(false);
        IReadOnlyList<DeezerArtist> result = doc is null
            ? NoArtists
            : Rows(doc).Select(ParseArtist).OfType<DeezerArtist>().ToList();
        runtime.Cache.Set(key, result, runtime.Options.ListTtl);
        return result;
    }

    public async Task<DeezerTrackDetail?> GetTrackAsync(long trackId, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"dz:track:{trackId}");
        if (runtime.Cache.TryGet<DeezerTrackDetail>(key, out var cached))
        {
            return cached;
        }

        using var doc = await GetAsync($"/track/{trackId}", string.Create(CultureInfo.InvariantCulture, $"track/{trackId}"), ct).ConfigureAwait(false);
        var detail = doc is null ? null : ParseDetail(doc.RootElement);
        runtime.Cache.Set(key, detail, detail is null ? runtime.Options.MissTtl : runtime.Options.TrackTtl);
        return detail;
    }

    private async Task<IReadOnlyList<DeezerTrack>> GetTracksAsync(string kind, long artistId, int limit, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"dz:{kind}:{artistId}:{limit}");
        if (runtime.Cache.TryGet<IReadOnlyList<DeezerTrack>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        using var doc = await GetAsync(
            $"/artist/{artistId}/{kind}",
            string.Create(CultureInfo.InvariantCulture, $"artist/{artistId}/{kind}?limit={limit}"),
            ct).ConfigureAwait(false);
        IReadOnlyList<DeezerTrack> result = doc is null
            ? NoTracks
            : Rows(doc).Select(ParseTrack).OfType<DeezerTrack>().ToList();
        runtime.Cache.Set(key, result, runtime.Options.ListTtl);
        return result;
    }

    /// <summary>One Deezer call with the limiter, the error-in-200 rules and the single retries. Null means not found.</summary>
    private async Task<JsonDocument?> GetAsync(string endpoint, string pathAndQuery, CancellationToken ct)
    {
        try
        {
            return await GetCoreAsync(endpoint, pathAndQuery, ct).ConfigureAwait(false);
        }
        catch (DeezerException)
        {
            runtime.NoteDeezerFailure();
            throw;
        }
    }

    private async Task<JsonDocument?> GetCoreAsync(string endpoint, string pathAndQuery, CancellationToken ct)
    {
        var options = runtime.Options;
        var quotaRetried = false;
        var serverRetried = false;
        while (true)
        {
            var raw = await DiscoveryTransport.GetAsync(
                http, runtime.Deezer, runtime.Time, options.Timeout, pathAndQuery, endpoint,
                (e, kind, message) => new DeezerException(e, kind, message), ct).ConfigureAwait(false);

            if (raw.Status >= 500 && !serverRetried)
            {
                serverRetried = true;
                await Task.Delay(options.ServerErrorDelay, runtime.Time, ct).ConfigureAwait(false);
                continue;
            }

            if (raw.Status is < 200 or >= 300)
            {
                throw new DeezerException(endpoint, DiscoveryFailure.Http, $"{endpoint} failed with HTTP {raw.Status}", raw.Status);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw.Body);
            }
            catch (JsonException)
            {
                throw new DeezerException(endpoint, DiscoveryFailure.BadResponse, $"{endpoint} returned unreadable JSON");
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.TryGetInt32(out var parsed) ? parsed : -1;
                doc.Dispose();
                if (code == NotFoundCode)
                {
                    return null;
                }

                if (code == QuotaCode)
                {
                    logger.Quota("deezer", options.QuotaWait.TotalSeconds);
                    if (!quotaRetried)
                    {
                        quotaRetried = true;
                        await Task.Delay(options.QuotaWait, runtime.Time, ct).ConfigureAwait(false);
                        continue;
                    }

                    throw new DeezerException(endpoint, DiscoveryFailure.Quota, $"{endpoint}: Deezer quota exceeded (code 4)", QuotaCode);
                }

                throw new DeezerException(endpoint, DiscoveryFailure.Http, $"{endpoint}: Deezer error code {code}", code);
            }

            return doc;
        }
    }

    private static IEnumerable<JsonElement> Rows(JsonDocument? doc)
    {
        if (doc is not null
            && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object);
        }

        return [];
    }

    private static DeezerArtist? ParseArtist(JsonElement e)
    {
        var id = Long(e, "id");
        var name = Text(e, "name");
        if (id is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var fans = Long(e, "nb_fan") ?? 0;
        var radio = e.TryGetProperty("radio", out var r) && r.ValueKind == JsonValueKind.True;
        return new DeezerArtist(id.Value, name, (int)Math.Clamp(fans, 0, int.MaxValue), radio);
    }

    private static DeezerAlbumRef ParseAlbum(JsonElement e)
    {
        if (e.TryGetProperty("album", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            return new DeezerAlbumRef(Long(a, "id") ?? 0, Text(a, "title") ?? string.Empty, Text(a, "release_date"));
        }

        return new DeezerAlbumRef(0, string.Empty, null);
    }

    private static DeezerTrack? ParseTrack(JsonElement e)
    {
        var id = Long(e, "id");
        var title = Text(e, "title");
        if (id is null || string.IsNullOrWhiteSpace(title) || !e.TryGetProperty("artist", out var a) || ParseArtist(a) is not { } artist)
        {
            return null;
        }

        var version = Text(e, "title_version");
        return new DeezerTrack(
            id.Value,
            title,
            Text(e, "title_short") is { Length: > 0 } shortTitle ? shortTitle : title,
            string.IsNullOrWhiteSpace(version) ? null : version,
            (int)Math.Clamp(Long(e, "duration") ?? 0, 0, int.MaxValue),
            Long(e, "rank") ?? 0,
            artist,
            ParseAlbum(e));
    }

    /// <summary>Reads one search row. Never touches <c>preview</c> (a signed, expiring link that must not be kept).</summary>
    private static DeezerSearchTrack? ParseSearchTrack(JsonElement e)
    {
        var id = Long(e, "id");
        var title = Text(e, "title");
        if (id is null || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string? artistName = null;
        if (e.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            artistName = Text(a, "name");
        }

        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var albumTitle = string.Empty;
        string? cover = null;
        if (e.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumTitle = Text(album, "title") ?? string.Empty;
            cover = Text(album, "cover_medium");
        }

        var version = Text(e, "title_version");
        var https = Uri.TryCreate(cover, UriKind.Absolute, out var coverUri) && coverUri.Scheme == Uri.UriSchemeHttps;
        return new DeezerSearchTrack(
            id.Value,
            title.Trim(),
            string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            artistName.Trim(),
            albumTitle,
            (int)Math.Clamp(Long(e, "duration") ?? 0, 0, int.MaxValue),
            https ? coverUri!.AbsoluteUri : null,
            e.TryGetProperty("explicit_lyrics", out var x) && x.ValueKind == JsonValueKind.True);
    }

    private static DeezerTrackDetail? ParseDetail(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || ParseTrack(e) is not { } t)
        {
            return null;
        }

        var contributors = new List<DeezerContributor>();
        if (e.TryGetProperty("contributors", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                if (Long(c, "id") is { } cid && Text(c, "name") is { Length: > 0 } cname)
                {
                    contributors.Add(new DeezerContributor(cid, cname, Text(c, "role") ?? "Main"));
                }
            }
        }

        var isrc = Text(e, "isrc");
        return new DeezerTrackDetail(
            t.Id, t.Title, t.TitleShort,
            string.IsNullOrWhiteSpace(isrc) ? null : isrc,
            t.DurationS,
            Long(e, "track_position") is { } pos and > 0 ? (int)pos : null,
            Long(e, "disk_number") is { } disk and > 0 ? (int)disk : null,
            Text(e, "release_date"),
            t.Artist, t.Album, contributors);
    }

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
