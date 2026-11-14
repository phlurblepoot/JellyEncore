using System.Globalization;
using Encore.Discovery;
using Microsoft.Extensions.Logging;
using TagLib;

namespace Encore.Fetch.Import;

/// <summary>
/// Imports one finished slskd download into music-radio (v0.2 design section 8.1): Deezer track detail, free-space
/// guard, copy into the dot staging folder, tag with TagLibSharp, read the real length and bitrate back, move into
/// <c>{radio}/soulseek/{Artist}/{Album} ({Year})/{NN} - {Title}.{ext}</c>, then delete the slskd copy.
/// Stateless apart from its settings, so concurrent imports are safe (collisions are re-checked at the move).
/// </summary>
public sealed class Importer : IImporter
{
    /// <summary>Largest difference between the real length and the reference, in seconds.</summary>
    public const int DurationToleranceS = 3;

    /// <summary>Lowest MP3 bitrate read back from the file.</summary>
    public const int MinMp3Kbps = 300;

    private const int MaxCopies = 99;

    private const UnixFileMode FolderMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode FileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
        UnixFileMode.OtherRead;

    private readonly ImporterSettings _settings;
    private readonly IFreeSpace _freeSpace;
    private readonly ILogger<Importer> _logger;
    private readonly IDeezerClient? _deezer;

    static Importer()
    {
        // MP3 tags are written as ID3v2.4 (design 8.1 step 4). These are TagLib-wide settings; Encore writes no other ID3.
        TagLib.Id3v2.Tag.DefaultVersion = 4;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public Importer(ImporterSettings settings, IFreeSpace freeSpace, ILogger<Importer> logger, IDeezerClient? deezer = null)
    {
        _settings = settings;
        _freeSpace = freeSpace;
        _logger = logger;
        _deezer = deezer;
    }

    public string StagingDir => System.IO.Path.Combine(_settings.RadioDir, SafeNames.StagingFolder);

    public async Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        var ext = System.IO.Path.GetExtension(request.SourcePath).TrimStart('.').ToLowerInvariant();
        if (ext is not ("flac" or "mp3"))
            return Mismatch(request, null, $"unsupported format '{ext}'");

        long sourceSize;
        try
        {
            sourceSize = new FileInfo(request.SourcePath).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(request, ImportReason.Io, $"source unreadable: {e.Message}");
        }

        var detail = await LoadDetailAsync(request, ct).ConfigureAwait(false);
        var tags = BuildTags(request, detail);
        var reference = request.DurationS ?? (detail is { DurationS: > 0 } ? detail.DurationS : null);

        var free = _freeSpace.FreeBytes(_settings.RadioDir);
        if (free is long f && f - sourceSize < _settings.FreeSpaceFloorBytes)
            return Failed(request, ImportReason.DiskLow,
                $"free {f} bytes minus file {sourceSize} bytes is under {_settings.FreeSpaceFloorBytes} bytes");

        var staged = System.IO.Path.Combine(StagingDir, $"{SafeNames.Segment(request.JobId)}.{ext}");
        try
        {
            try
            {
                CreateFolder(StagingDir);
                await CopyAsync(request.SourcePath, staged, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(request, ImportReason.Io, $"copy failed: {e.Message}");
            }

            try
            {
                WriteTags(staged, ext, tags);
            }
            catch (Exception e) when (e is CorruptFileException or UnsupportedFormatException)
            {
                return Mismatch(request, null, $"not a valid {ext} file: {e.Message}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(request, ImportReason.Io, $"tag write failed: {e.Message}");
            }
            catch (Exception e)
            {
                return Failed(request, ImportReason.Tag, $"{e.GetType().Name}: {e.Message}");
            }

            AudioFacts audio;
            try
            {
                var (facts, problem) = ReadBack(staged, ext, reference);
                if (problem is not null)
                    return Mismatch(request, facts, problem);
                audio = facts!;
            }
            catch (Exception e) when (e is CorruptFileException or UnsupportedFormatException)
            {
                return Mismatch(request, null, $"unreadable after tagging: {e.Message}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(request, ImportReason.Io, $"read back failed: {e.Message}");
            }

            string final;
            string relative;
            bool reused;
            try
            {
                (final, relative, reused) = MoveIntoPlace(staged, tags, ext, audio.SizeBytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(request, ImportReason.Io, $"move failed: {e.Message}");
            }

            var left = RemoveSource(request);
            var jellyfinPath = _settings.RadioJellyfinDir.TrimEnd('/') + "/" + relative;
            _logger.ImportDone(request.JobId, final, audio.Format, audio.Kbps, audio.DurationS);
            return new ImportResult(ImportStatus.Imported, null, null, final, jellyfinPath, tags, audio, reused, left);
        }
        finally
        {
            TryDeleteFile(staged);
        }
    }

    public int CleanStaging()
    {
        if (!Directory.Exists(StagingDir))
            return 0;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(StagingDir, "*", SearchOption.AllDirectories))
        {
            if (TryDeleteFile(file))
                deleted++;
        }
        return deleted;
    }

    /// <summary>Tags from the Deezer track detail (design 8.1 step 4), else from the candidate.</summary>
    public static TrackTags BuildTags(ImportRequest request, DeezerTrackDetail? detail)
    {
        if (detail is null)
        {
            return new TrackTags(request.Title, [request.Artist], request.Artist,
                string.IsNullOrWhiteSpace(request.Album) ? null : request.Album,
                null, null, null, null, request.ArtistMbid?.ToString("D"));
        }

        var main = detail.Contributors.Where(c => c.Role == "Main").Select(c => c.Name);
        var featured = detail.Contributors.Where(c => c.Role == "Featured").Select(c => c.Name);
        var performers = new List<string>();
        foreach (var name in (main.Any() ? main : [detail.Artist.Name]).Concat(featured))
        {
            if (!string.IsNullOrWhiteSpace(name) && !performers.Contains(name, StringComparer.OrdinalIgnoreCase))
                performers.Add(name);
        }
        if (performers.Count == 0)
            performers.Add(request.Artist);

        var albumArtist = string.IsNullOrWhiteSpace(detail.Artist.Name) ? request.Artist : detail.Artist.Name;
        var title = string.IsNullOrWhiteSpace(detail.Title) ? request.Title : detail.Title;
        var album = string.IsNullOrWhiteSpace(detail.Album.Title) ? request.Album : detail.Album.Title;
        return new TrackTags(
            title,
            performers,
            albumArtist,
            string.IsNullOrWhiteSpace(album) ? null : album,
            detail.TrackPosition is > 0 ? detail.TrackPosition : null,
            detail.DiskNumber is > 0 ? detail.DiskNumber : null,
            YearOf(detail.Album.ReleaseDate) ?? YearOf(detail.ReleaseDate),
            string.IsNullOrWhiteSpace(detail.Isrc) ? null : detail.Isrc,
            request.ArtistMbid?.ToString("D"));
    }

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 0
            ? y
            : null;

    private async Task<DeezerTrackDetail?> LoadDetailAsync(ImportRequest request, CancellationToken ct)
    {
        if (_deezer is null || request.DeezerTrackId is not long id)
            return null;
        try
        {
            return await _deezer.GetTrackAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Design 8.1 step 1: a failure falls back to the candidate's fields.
            return null;
        }
    }

    private static async Task CopyAsync(string source, string target, CancellationToken ct)
    {
        await using var input = new FileStream(source, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var output = new FileStream(target, System.IO.FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
    }

    /// <summary>Overwrites only the fields below; everything else (embedded cover art included) stays.</summary>
    private static void WriteTags(string path, string ext, TrackTags t)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.GetTag(ext == "mp3" ? TagTypes.Id3v2 : TagTypes.Xiph, true)
            ?? throw new UnsupportedFormatException($"no {(ext == "mp3" ? "ID3v2" : "Xiph")} tag possible");
        tag.Title = t.Title;
        tag.Performers = [.. t.Performers];
        tag.AlbumArtists = [t.AlbumArtist];
        if (t.Album is not null)
            tag.Album = t.Album;
        if (t.Track is int track)
            tag.Track = (uint)track;
        if (t.Disc is int disc)
            tag.Disc = (uint)disc;
        if (t.Year is int year)
            tag.Year = (uint)year;
        if (t.Isrc is not null)
            tag.ISRC = t.Isrc;
        if (t.MbArtistId is not null)
            tag.MusicBrainzArtistId = t.MbArtistId;
        file.Save();
    }

    /// <summary>The real format, length and bitrate; a non-null problem means the copy does not match (next peer).</summary>
    private static (AudioFacts? Facts, string? Problem) ReadBack(string path, string ext, int? referenceS)
    {
        using var file = TagLib.File.Create(path);
        var p = file.Properties;
        var seconds = p.Duration.TotalSeconds;
        var size = new FileInfo(path).Length;
        var isFlac = file is TagLib.Flac.File && p.Codecs.Any(c => c is TagLib.Flac.StreamHeader);
        var isMp3 = file is TagLib.Mpeg.AudioFile && p.Codecs.Any(c => c is TagLib.Mpeg.AudioHeader);
        var kbps = p.AudioBitrate > 0 ? p.AudioBitrate : seconds > 0 ? (int)Math.Round(size * 8 / seconds / 1000) : 0;
        var facts = new AudioFacts(ext, kbps, (int)Math.Round(seconds),
            p.AudioSampleRate > 0 ? p.AudioSampleRate : null, p.BitsPerSample > 0 ? p.BitsPerSample : null, size);

        if (ext == "flac" && !isFlac)
            return (facts, "codec is not FLAC");
        if (ext == "mp3" && !isMp3)
            return (facts, "codec is not MP3");
        if (seconds < 1)
            return (facts, "no audio length");
        if (referenceS is int r && Math.Abs(seconds - r) > DurationToleranceS)
            return (facts, $"length {seconds:0.#} s, expected {r} s");
        if (ext == "mp3" && p.AudioBitrate < MinMp3Kbps)
            return (facts, $"bitrate {p.AudioBitrate} kbps, need {MinMp3Kbps}");
        return (facts, null);
    }

    /// <summary>
    /// Moves the staged file to its final name (design 8.1 steps 6-8). Same name and size already there: the existing
    /// file is kept. Different size: <c> (2)</c>, <c> (3)</c>... Returns the full path and the path relative to the radio dir.
    /// </summary>
    private (string Final, string Relative, bool Reused) MoveIntoPlace(string staged, TrackTags tags, string ext, long size)
    {
        var copy = 1;
        while (copy <= MaxCopies)
        {
            var relative = SafeNames.RelativePath(tags, ext, copy);
            var final = System.IO.Path.Combine(_settings.RadioDir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            CreateFolders(relative);
            if (System.IO.File.Exists(final))
            {
                if (new FileInfo(final).Length == size)
                    return (final, relative, true);
                copy++;
                continue;
            }

            try
            {
                System.IO.File.Move(staged, final, overwrite: false);
            }
            catch (IOException) when (System.IO.File.Exists(final))
            {
                continue;   // another import took the name in between: look at it again
            }

            if (!OperatingSystem.IsWindows())
                TrySetMode(final, FileMode);
            return (final, relative, false);
        }
        throw new IOException($"more than {MaxCopies} different files named like {SafeNames.RelativePath(tags, ext)}");
    }

    private void CreateFolders(string relative)
    {
        var parts = relative.Split('/');
        var dir = _settings.RadioDir;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            dir = System.IO.Path.Combine(dir, parts[i]);
            CreateFolder(dir);
        }
    }

    private static void CreateFolder(string dir)
    {
        if (Directory.Exists(dir))
            return;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            TrySetMode(dir, FolderMode);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void TrySetMode(string path, UnixFileMode mode)
    {
        try
        {
            System.IO.File.SetUnixFileMode(path, mode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not the owner: the folder keeps its mode.
        }
    }

    /// <summary>Deletes the slskd copy and its <c>encore/{jobId}</c> folder. True when something stayed behind.</summary>
    private bool RemoveSource(ImportRequest request)
    {
        var target = request.SourcePath;
        try
        {
            System.IO.File.Delete(request.SourcePath);
            if (IsPlainName(request.JobId))
            {
                target = System.IO.Path.Combine(_settings.SlskdDownloadsDir, "encore", request.JobId);
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.ImportSourceLeft(request.JobId, target, e.Message);
            return true;
        }
    }

    private static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0;

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
                return false;
            System.IO.File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A bad copy: the slskd file is deleted (its job folder stays for the next peer).</summary>
    private ImportResult Mismatch(ImportRequest request, AudioFacts? audio, string error)
    {
        TryDeleteFile(request.SourcePath);
        _logger.ImportFailed(request.JobId, ImportReason.Mismatch, error);
        return new ImportResult(ImportStatus.Mismatch, ImportReason.Mismatch, error, null, null, null, audio, false, false);
    }

    /// <summary>The job ends: the slskd copy and its job folder are removed (best effort).</summary>
    private ImportResult Failed(ImportRequest request, string reason, string error)
    {
        _logger.ImportFailed(request.JobId, reason, error);
        var left = RemoveSource(request);
        return new ImportResult(ImportStatus.Failed, reason, error, null, null, null, null, false, left);
    }
}
