using System.Globalization;
using System.Text;

namespace Encore.Discovery;

/// <summary>
/// The owned-index key of a song (v0.2 design section 5.1): <c>ArtistKey + "|" + TitleKey</c>, compared ordinally.
/// Built from a Deezer title and artist name, or a Jellyfin item's Name and <c>Picker.ArtistOf(item).Name</c>.
/// Two copies of the same song (a remaster, a "feat." credit, other accents or case) share a key; a live, acoustic,
/// demo or remix version does not. <c>fetch_negative.track_key</c> stores <see cref="Value"/>.
/// </summary>
public readonly record struct TrackKey
{
    private TrackKey(string value) => Value = value;

    /// <summary>The stored form, e.g. <c>smashing pumpkins|luna</c>.</summary>
    public string Value { get; }

    public string ArtistKey => Value is null ? "" : Value[..Value.IndexOf('|', StringComparison.Ordinal)];

    public string TitleKey => Value is null ? "" : Value[(Value.IndexOf('|', StringComparison.Ordinal) + 1)..];

    /// <summary>False when the artist or the title folds to nothing; such a song is never planned from the pool.</summary>
    public static bool TryCreate(string? artist, string? title, out TrackKey key)
    {
        var a = KeyText.ArtistKey(artist);
        var t = KeyText.TitleKey(title);
        if (a.Length == 0 || t.Length == 0)
        {
            key = default;
            return false;
        }

        key = new TrackKey(a + "|" + t);
        return true;
    }

    public static TrackKey? Create(string? artist, string? title) => TryCreate(artist, title, out var key) ? key : null;

    /// <summary>Reads a stored <see cref="Value"/> back (both parts non-empty, one separator).</summary>
    public static bool TryParse(string? value, out TrackKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var bar = value.IndexOf('|', StringComparison.Ordinal);
        if (bar <= 0 || bar == value.Length - 1 || value.IndexOf('|', bar + 1) >= 0)
        {
            return false;
        }

        key = new TrackKey(value);
        return true;
    }

    public override string ToString() => Value ?? "";
}

/// <summary>
/// The one implementation of the v0.2 name folding (design section 5.1). <see cref="TrackKey"/>, the Deezer artist
/// match and the Soulseek scorer all use it.
/// </summary>
public static class KeyText
{
    /// <summary>Words that only name a release version: brackets or a " - " tail made only of these are dropped.</summary>
    private static readonly HashSet<string> VersionWords = new(StringComparer.Ordinal)
    {
        "remaster", "remastered", "mono", "stereo", "version", "single", "album", "radio", "edit",
        "deluxe", "edition", "bonus", "track", "explicit", "clean", "original",
    };

    private static readonly string[] FeatMarkers = [" feat. ", " feat ", " ft. ", " ft ", " featuring "];

    private static readonly string[] DashTails = [" - ", " – ", " — "];

    /// <summary>
    /// Unicode NFKD with accents dropped (Japanese voicing marks are kept), invariant lowercase, apostrophes removed,
    /// &amp; and + as "and", every other character that is not a letter or digit splits words.
    /// </summary>
    public static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var stripped = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark && ch is not ('゙' or '゚'))
            {
                continue;
            }

            stripped.Append(ch);
        }

        var sb = new StringBuilder(stripped.Length + 8);
        foreach (var rune in stripped.ToString().Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (rune.Value is '\'' or '’' or '‘' or '`')
            {
                continue;
            }

            if (rune.Value is '&' or '+')
            {
                sb.Append(" and ");
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                sb.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                sb.Append(' ');
            }
        }

        tokens.AddRange(sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return tokens;
    }

    /// <summary>
    /// Title words without feat. credits, version-only brackets and a version-only " - " tail. When that removes
    /// everything (a title that is only a bracket), the plain words are used.
    /// </summary>
    public static List<string> TitleTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var s = CutAtFeat(StripBrackets(title));
        var dash = LastDash(s);
        if (dash.Index > 0 && AllVersionWords(Tokens(s[(dash.Index + dash.Length)..])))
        {
            s = s[..dash.Index];
        }

        var tokens = Tokens(s);
        return tokens.Count > 0 ? tokens : Tokens(title);
    }

    /// <summary>Artist words cut at feat., without a leading "the" (a lone "The" stays).</summary>
    public static List<string> ArtistTokens(string? artist)
    {
        var tokens = Tokens(CutAtFeat(artist ?? ""));
        if (tokens.Count > 1 && tokens[0] == "the")
        {
            tokens.RemoveAt(0);
        }

        return tokens;
    }

    /// <summary>The artist half of a <see cref="TrackKey"/>; empty when nothing is left.</summary>
    public static string ArtistKey(string? artist) => string.Join(' ', ArtistTokens(artist));

    /// <summary>The title half of a <see cref="TrackKey"/>; empty when nothing is left.</summary>
    public static string TitleKey(string? title) => string.Join(' ', TitleTokens(title));

    private static string CutAtFeat(string s)
    {
        var best = -1;
        foreach (var m in FeatMarkers)
        {
            var i = s.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best))
            {
                best = i;
            }
        }

        return best >= 0 ? s[..best] : s;
    }

    private static (int Index, int Length) LastDash(string s)
    {
        var best = (Index: -1, Length: 0);
        foreach (var d in DashTails)
        {
            var i = s.LastIndexOf(d, StringComparison.Ordinal);
            if (i > best.Index)
            {
                best = (i, d.Length);
            }
        }

        return best;
    }

    private static string StripBrackets(string s)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var open = s[i];
            var close = open == '(' ? ')' : open == '[' ? ']' : '\0';
            var end = close == '\0' ? -1 : s.IndexOf(close, i + 1);
            if (end < 0)
            {
                sb.Append(open);
                i++;
                continue;
            }

            var tokens = Tokens(s[(i + 1)..end]);
            var first = tokens.Count > 0 ? tokens[0] : "";
            var feat = first is "feat" or "ft" or "featuring" or "with";
            if (feat || (tokens.Count > 0 && AllVersionWords(tokens)))
            {
                i = end + 1;
                continue;
            }

            sb.Append(s, i, end + 1 - i);
            i = end + 1;
        }

        return sb.ToString();
    }

    private static bool AllVersionWords(List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (VersionWords.Contains(t))
            {
                continue;
            }

            if (t.Length == 4 && t.All(char.IsAsciiDigit))
            {
                continue;
            }

            if (t == "mix" && i > 0 && tokens[i - 1] is "original" or "album")
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
