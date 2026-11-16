using System.Globalization;
using System.Text.RegularExpressions;
using Encore.Config;
using Encore.Fetch.Import;
using Encore.Store;

namespace Encore.Retention;

/// <summary>
/// Where a promoted song goes in the main library (v04 §7.1). <see cref="Path"/> is Encore's view (ENCORE_LIBRARY_DIR),
/// <see cref="JellyfinPath"/> the same file as Jellyfin sees it. <see cref="Collision"/> is the library file that already holds
/// this song (same name, any audio extension, ignoring case), or null.
/// </summary>
public sealed record PromotionTarget(string ArtistDir, string AlbumDir, string FileName, string Path, string JellyfinPath, string? Collision)
{
    /// <summary>The target folder as Jellyfin sees it (what Media/Updated is sent for).</summary>
    public string JellyfinFolder => JellyfinPath[..JellyfinPath.LastIndexOf('/')];
}

/// <summary>
/// The library path rules of a promotion (v04 §7.1), the beets and Lidarr layout <c>{Artist}/{Album} ({Year})/{NN} - {Title}.{ext}</c>:
/// the radio import already wrote the artist and album folder names that way (<see cref="SafeNames.RelativePath"/>), so the target
/// takes the radio file's last three path segments. An existing artist folder, and an existing album folder inside it, whose name
/// equals ours ignoring case is joined under its own spelling. Disc 2 and up are named <c>{d}-{NN} - {Title}</c>. The importer's
/// collision suffix <c> (n)</c> is dropped. Pure apart from reading folder names.
/// </summary>
public sealed partial class PromotionPaths
{
    /// <summary>Extensions that count as the same song when the stem matches (a collision).</summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "mp3", "m4a", "aac", "ogg", "oga", "opus", "wav", "wma", "alac", "ape", "wv", "aif", "aiff", "dsf", "mka",
    };

    private readonly string _radioDir;
    private readonly string _libraryDir;
    private readonly string _libraryJellyfinDir;

    public PromotionPaths(string radioDir, string libraryDir, string libraryJellyfinDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryJellyfinDir);
        _radioDir = radioDir;
        _libraryDir = libraryDir;
        _libraryJellyfinDir = libraryJellyfinDir.Replace('\\', '/').TrimEnd('/');
    }

    public static PromotionPaths From(EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PromotionPaths(options.RadioDir, options.LibraryDir, options.LibraryJellyfinDir);
    }

    public string LibraryDir => _libraryDir;

    /// <summary><c>{library}/.encore-incoming</c>: the hidden staging folder (Jellyfin, Lidarr's disk scan and slskd skip dot folders).</summary>
    public string IncomingDir => System.IO.Path.Combine(_libraryDir, RetentionLimits.IncomingFolder);

    /// <summary><c>{library}/.encore-incoming/{rowId}.{ext}.encore-part</c>: a non-audio name, so Lidarr's folder watcher ignores it.</summary>
    public string StagedPath(long rowId, string ext) =>
        System.IO.Path.Combine(IncomingDir, rowId.ToString(CultureInfo.InvariantCulture) + "." + ext + RetentionLimits.StagedSuffix);

    /// <summary>
    /// The target for a radio file, or null when the path is not a file at least three folders deep under the radio dir
    /// (<c>{source}/{Artist}/{Album}/{file}</c>). <paramref name="title"/> (the row's title) keeps a <c> (n)</c> that belongs to the title itself.
    /// </summary>
    public PromotionTarget? Plan(string radioPath, int? discNo, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioPath);
        var relative = System.IO.Path.GetRelativePath(_radioDir, radioPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || System.IO.Path.IsPathRooted(relative))
        {
            return null;
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4)
        {
            return null;
        }

        var artist = segments[^3];
        var album = segments[^2];
        var file = segments[^1];
        var dot = file.LastIndexOf('.');
        if (dot <= 0 || dot == file.Length - 1 || artist.StartsWith('.') || album.StartsWith('.'))
        {
            // A dot folder would hide the song from Jellyfin, Lidarr and slskd (the importer never writes one, SafeNames.Segment).
            return null;
        }

        var ext = file[(dot + 1)..];
        var stem = DropCopySuffix(file[..dot], title);
        if (discNo is int disc and >= 2 && TrackStem().Match(stem) is { Success: true } m)
        {
            stem = disc.ToString(CultureInfo.InvariantCulture) + "-" + m.Groups[1].Value + " - " + m.Groups[2].Value;
        }

        var fileName = SafeNames.FileName(stem, ext);
        var artistDir = ExistingChild(_libraryDir, artist) ?? artist;
        var artistPath = System.IO.Path.Combine(_libraryDir, artistDir);
        var albumDir = ExistingChild(artistPath, album) ?? album;
        var albumPath = System.IO.Path.Combine(artistPath, albumDir);
        var target = System.IO.Path.Combine(albumPath, fileName);
        var jellyfin = $"{_libraryJellyfinDir}/{artistDir}/{albumDir}/{fileName}";
        return new PromotionTarget(artistDir, albumDir, fileName, target, jellyfin, FindCollision(albumPath, System.IO.Path.GetFileNameWithoutExtension(fileName)));
    }

    /// <summary>The library path a promotion of the row would use, for the report (<c>RetentionPass</c>'s promotionTarget).</summary>
    public Func<TrackedFile, string?> ReportTarget(FetchStore fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        return row =>
        {
            if (string.IsNullOrEmpty(row.Path))
            {
                return null;
            }

            try
            {
                return Plan(row.Path, fetch.DiscNoOf(row.Id), row.Title)?.Path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or StoreException or Microsoft.Data.Sqlite.SqliteException)
            {
                return null;
            }
        };
    }

    /// <summary>Drops the importer's <c> (n)</c> (n of 2 or more) unless the title itself ends that way. Without a title nothing is dropped.</summary>
    internal static string DropCopySuffix(string stem, string? title)
    {
        var m = CopySuffix().Match(stem);
        if (!m.Success || title is null || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 2)
        {
            return stem;
        }

        return SafeNames.Segment(title).EndsWith(m.Groups[0].Value[m.Groups[1].Value.Length..], StringComparison.Ordinal) ? stem : m.Groups[1].Value;
    }

    /// <summary>A direct sub-folder whose name equals <paramref name="name"/> ignoring case (exact spelling first), or null. Dot folders never match.</summary>
    private static string? ExistingChild(string parent, string name)
    {
        if (!Directory.Exists(parent))
        {
            return null;
        }

        if (Directory.Exists(System.IO.Path.Combine(parent, name)) && HasExactName(parent, name))
        {
            return name;
        }

        return Directory.EnumerateDirectories(parent)
            .Select(System.IO.Path.GetFileName)
            .Where(n => n is not null && !n.StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExactName(string parent, string name) =>
        Directory.EnumerateDirectories(parent).Any(d => string.Equals(System.IO.Path.GetFileName(d), name, StringComparison.Ordinal));

    /// <summary>An audio file in the album folder with the same stem ignoring case, or null.</summary>
    private static string? FindCollision(string albumPath, string stem)
    {
        if (!Directory.Exists(albumPath))
        {
            return null;
        }

        return Directory.EnumerateFiles(albumPath)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(f =>
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                && AudioExtensions.Contains(System.IO.Path.GetExtension(f).TrimStart('.')));
    }

    [GeneratedRegex(@"^(.*) \((\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();

    [GeneratedRegex(@"^(\d{2,}) - (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackStem();
}
