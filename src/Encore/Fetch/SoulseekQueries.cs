using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Encore.Fetch;

/// <summary>Search texts for one song (design section 6.2): q1 as given, q2 simplified, at most two.</summary>
public static partial class SoulseekQueries
{
    private static readonly string[] FeatMarkers = [" feat.", " feat ", " ft.", " ft ", " featuring "];

    /// <summary>q1, then q2 when it differs from q1 and is not empty.</summary>
    public static IReadOnlyList<string> For(string artist, string title)
    {
        var q1 = Clean($"{artist} {title}");
        var q2 = Simplified(artist, title);
        var list = new List<string>(FetchLimits.QueriesPerSong);
        if (q1.Length > 0)
        {
            list.Add(q1);
        }

        if (q2.Length > 0 && !string.Equals(q1, q2, StringComparison.Ordinal))
        {
            list.Add(q2);
        }

        return list;
    }

    /// <summary>ASCII-folded, brackets and feat. parts removed, a " - " tail cut from the title, then <see cref="Clean"/>.</summary>
    public static string Simplified(string artist, string title)
    {
        var t = CutAtFeat(Brackets().Replace(title ?? "", " "));
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            t = t[..dash];
        }

        var a = CutAtFeat(Brackets().Replace(artist ?? "", " "));
        return Clean(AsciiFold($"{a} {t}"));
    }

    /// <summary>Removes double quotes and words starting with '-' (slskd reads them as exclusions), collapses spaces.</summary>
    public static string Clean(string text)
    {
        var words = (text ?? "").Replace("\"", " ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.StartsWith('-'));
        return string.Join(' ', words);
    }

    private static string CutAtFeat(string s)
    {
        var lower = s.ToLowerInvariant();
        var cut = s.Length;
        foreach (var marker in FeatMarkers)
        {
            var at = lower.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && at < cut)
            {
                cut = at;
            }
        }

        return s[..cut];
    }

    private static string AsciiFold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(ch <= '\u007f' ? ch : ' ');
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex Brackets();
}
