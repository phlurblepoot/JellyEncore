using Encore.Config;

namespace Encore.Fetch.Import;

/// <summary>
/// One downloaded file to import (v0.2 design section 8.1). The fetch worker builds it from the job's candidate and
/// the slskd transfer: <see cref="SourcePath"/> is the finished slskd copy under <c>{downloads}/encore/{JobId}/</c>.
/// </summary>
/// <param name="DurationS">The reference length (the candidate's Deezer duration); null when unknown.</param>
/// <param name="ArtistMbid">The candidate's MusicBrainz artist id, written to the tags when present.</param>
public sealed record ImportRequest(
    string JobId,
    string SourcePath,
    string Artist,
    string Title,
    string? Album,
    int? DurationS,
    long? DeezerTrackId,
    Guid? ArtistMbid);

/// <summary>Outcome of one import. <see cref="Mismatch"/> means "try the next peer"; <see cref="Failed"/> ends the job (import_failed).</summary>
public enum ImportStatus { Imported, Mismatch, Failed }

/// <summary>Wire names for <c>import.failed</c> and <see cref="ImportResult.Reason"/>.</summary>
public static class ImportReason
{
    public const string Mismatch = "mismatch";
    public const string DiskLow = "disk_low";
    public const string Io = "io";
    public const string Tag = "tag";
}

/// <summary>The tags written to the file (from the Deezer track detail, else the candidate).</summary>
public sealed record TrackTags(
    string Title,
    IReadOnlyList<string> Performers,
    string AlbumArtist,
    string? Album,
    int? Track,
    int? Disc,
    int? Year,
    string? Isrc,
    string? MbArtistId);

/// <summary>What TagLib read back from the imported file.</summary>
public sealed record AudioFacts(string Format, int Kbps, int DurationS, int? SampleRate, int? BitDepth, long SizeBytes);

/// <param name="Path">The final file under the radio dir (Encore's view); null unless imported.</param>
/// <param name="JellyfinPath">The same file as Jellyfin sees it, always with <c>/</c>.</param>
/// <param name="ReusedExisting">An identical file (same name and size) was already there; it is kept and the copy dropped.</param>
/// <param name="SourceLeftBehind">The slskd copy or its job folder could not be deleted (<c>import.source_left</c>).</param>
public sealed record ImportResult(
    ImportStatus Status,
    string? Reason,
    string? Error,
    string? Path,
    string? JellyfinPath,
    TrackTags? Tags,
    AudioFacts? Audio,
    bool ReusedExisting,
    bool SourceLeftBehind);

public interface IImporter
{
    /// <summary>Tags, checks and moves one downloaded file into the radio dir. Never throws except on cancellation.</summary>
    Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct);

    /// <summary>Deletes every file left in the staging folder (restart reconciliation, design 8.3 step 1). Returns the count.</summary>
    int CleanStaging();
}

/// <summary>Free space of the filesystem holding a path; null when it cannot be read.</summary>
public interface IFreeSpace
{
    long? FreeBytes(string path);
}

/// <summary><see cref="IFreeSpace"/> through <see cref="DriveInfo"/> (statvfs on Linux).</summary>
public sealed class DriveFreeSpace : IFreeSpace
{
    public long? FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The folders the importer works in.</summary>
/// <param name="RadioDir">Encore's writable view of music-radio.</param>
/// <param name="RadioJellyfinDir">The same folder as Jellyfin sees it (starts with <c>/</c>).</param>
/// <param name="SlskdDownloadsDir">Encore's view of slskd's downloads folder.</param>
/// <param name="FreeSpaceFloorBytes">Free space that must remain on the radio dir after the copy.</param>
public sealed record ImporterSettings(
    string RadioDir,
    string RadioJellyfinDir,
    string SlskdDownloadsDir,
    long FreeSpaceFloorBytes = ImporterSettings.DefaultFreeSpaceFloor)
{
    /// <summary>20 GB (decimal), FetchLimits.FreeSpaceFloor in the design.</summary>
    public const long DefaultFreeSpaceFloor = 20_000_000_000;

    public static ImporterSettings From(EncoreOptions options) =>
        new(options.RadioDir, options.RadioJellyfinDir, options.SlskdDownloadsDir);
}
