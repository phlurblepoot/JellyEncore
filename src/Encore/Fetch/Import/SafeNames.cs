using System.Globalization;
using System.Text;

namespace Encore.Fetch.Import;

/// <summary>
/// File and folder names for imported songs (v0.2 design section 8.1 step 6). Pure; works the same on every OS.
/// </summary>
public static class SafeNames
{
    /// <summary>Longest segment (folder or file name, extension and collision suffix included).</summary>
    public const int MaxSegment = 120;

    /// <summary>The folder under the radio dir for Soulseek imports (Phase 3b adds <c>youtube</c> beside it).</summary>
    public const string SoulseekFolder = "soulseek";

    /// <summary>The staging folder under the radio dir. Jellyfin ignores dot folders.</summary>
    public const string StagingFolder = ".encore-incoming";

    private const string Unsafe = "/\\:*?\"<>|";

    /// <summary>
    /// One safe path segment: <c>/ \ : * ? " &lt; &gt; |</c> and control characters become <c>_</c>; spaces and trailing
    /// dots are trimmed; a leading dot becomes <c>_</c>; empty, <c>.</c> and <c>..</c> become <c>_</c>; at most
    /// <paramref name="max"/> characters.
    /// </summary>
    public static string Segment(string? text, int max = MaxSegment)
    {
        var sb = new StringBuilder((text ?? "").Length);
        foreach (var c in text ?? "")
            sb.Append(char.IsControl(c) || Unsafe.Contains(c) ? '_' : c);
        var s = Trim(sb.ToString());
        if (s.Length > max)
            s = Trim(Cut(s, max));
        if (s.Length == 0)
            return "_";
        if (s[0] == '.')
            s = "_" + s[1..];
        return s;
    }

    /// <summary>
    /// The final path relative to the radio dir, with <c>/</c>:
    /// <c>soulseek/{Artist}/{Album} ({Year})/{NN} - {Title}.{ext}</c>. No year: <c>{Album}</c>; no album: <c>Singles</c>;
    /// no position: <c>{Title}.{ext}</c>. <paramref name="copy"/> 2 or more adds <c> (n)</c> before the extension.
    /// </summary>
    public static string RelativePath(TrackTags tags, string ext, int copy = 1)
    {
        var album = string.IsNullOrWhiteSpace(tags.Album)
            ? "Singles"
            : tags.Year is int y ? $"{tags.Album} ({y.ToString(CultureInfo.InvariantCulture)})" : tags.Album;
        var stem = tags.Track is int n and > 0
            ? $"{n.ToString("00", CultureInfo.InvariantCulture)} - {tags.Title}"
            : tags.Title;
        return $"{SoulseekFolder}/{Segment(tags.AlbumArtist)}/{Segment(album)}/{FileName(stem, ext, copy)}";
    }

    /// <summary>A safe file name of at most <see cref="MaxSegment"/> characters, suffix and extension included.</summary>
    public static string FileName(string stem, string ext, int copy = 1)
    {
        var suffix = copy >= 2 ? $" ({copy.ToString(CultureInfo.InvariantCulture)})" : "";
        var tail = suffix + "." + ext;
        return Segment(stem, MaxSegment - tail.Length) + tail;
    }

    private static string Trim(string s) => s.Trim().TrimEnd('.', ' ').Trim();

    // Never splits a surrogate pair.
    private static string Cut(string s, int max) =>
        char.IsHighSurrogate(s[max - 1]) ? s[..(max - 1)] : s[..max];
}
