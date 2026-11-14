using System.Globalization;
using System.Text;

namespace Encore.Fetch.Scoring;

/// <summary>Name folding for the scorer; follows the TrackKey steps of v0.2 design section 5.1.</summary>
internal static class ScoringText
{
    private static readonly HashSet<string> VersionWords = new(StringComparer.Ordinal)
    {
        "remaster", "remastered", "mono", "stereo", "version", "single", "album", "radio", "edit",
        "deluxe", "edition", "bonus", "track", "explicit", "clean", "original",
    };

    private static readonly string[] FeatMarkers = [" feat. ", " feat ", " ft. ", " ft ", " featuring "];

    /// <summary>Lowercase, accents removed, apostrophes dropped, &amp; and + as "and", everything else that is not a letter or digit splits.</summary>
    public static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (ch is '\'' or '\u2019' or '\u2018' or '`') continue;
            if (ch is '&' or '+') { sb.Append(" and "); continue; }
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        tokens.AddRange(sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return tokens;
    }

    /// <summary>Title tokens with feat parts, version-only brackets and a version-only " - " tail removed.</summary>
    public static List<string> TitleKeyTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        var s = StripBrackets(title);
        var cut = CutAtFeat(s);
        s = cut;
        var dash = s.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && AllVersionWords(Tokens(s[(dash + 3)..]))) s = s[..dash];
        var tokens = Tokens(s);
        return tokens.Count > 0 ? tokens : Tokens(title);
    }

    /// <summary>Artist tokens cut at feat., without a leading "the".</summary>
    public static List<string> ArtistKeyTokens(string? artist)
    {
        var tokens = Tokens(CutAtFeat(artist ?? ""));
        if (tokens.Count > 1 && tokens[0] == "the") tokens.RemoveAt(0);
        return tokens;
    }

    private static string CutAtFeat(string s)
    {
        var best = -1;
        foreach (var m in FeatMarkers)
        {
            var i = s.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best >= 0 ? s[..best] : s;
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
            if (end < 0) { sb.Append(open); i++; continue; }
            var inner = s[(i + 1)..end];
            var tokens = Tokens(inner);
            var first = tokens.Count > 0 ? tokens[0] : "";
            var feat = first is "feat" or "ft" or "featuring" or "with";
            if (feat || (tokens.Count > 0 && AllVersionWords(tokens))) { i = end + 1; continue; }
            sb.Append(s, i, end + 1 - i);
            i = end + 1;
        }
        return sb.ToString();
    }

    private static bool AllVersionWords(List<string> tokens)
    {
        if (tokens.Count == 0) return false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (VersionWords.Contains(t)) continue;
            if (t.Length == 4 && t.All(char.IsDigit)) continue;
            if (t == "mix" && i > 0 && tokens[i - 1] is "original" or "album") continue;
            return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="phrase"/> appears as consecutive whole tokens in <paramref name="tokens"/>.</summary>
    public static bool ContainsPhrase(IReadOnlyList<string> tokens, IReadOnlyList<string> phrase)
    {
        if (phrase.Count == 0 || phrase.Count > tokens.Count) return false;
        for (var i = 0; i + phrase.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < phrase.Count && ok; j++) ok = tokens[i + j] == phrase[j];
            if (ok) return true;
        }
        return false;
    }
}
