using Encore.Discovery;

namespace Encore.Fetch.Scoring;

/// <summary>Name folding for the scorer: the TrackKey folding of v0.2 design section 5.1 (<see cref="KeyText"/>), plus phrase search.</summary>
internal static class ScoringText
{
    /// <summary>Lowercase, accents removed, apostrophes dropped, &amp; and + as "and", everything else that is not a letter or digit splits.</summary>
    public static List<string> Tokens(string? text) => KeyText.Tokens(text);

    /// <summary>Title tokens with feat parts, version-only brackets and a version-only " - " tail removed.</summary>
    public static List<string> TitleKeyTokens(string? title) => KeyText.TitleTokens(title);

    /// <summary>Artist tokens cut at feat., without a leading "the".</summary>
    public static List<string> ArtistKeyTokens(string? artist) => KeyText.ArtistTokens(artist);

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
