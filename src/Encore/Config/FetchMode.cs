namespace Encore.Config;

/// <summary>Whether Encore fetches songs it does not own (v0.2 design, section 11).</summary>
public enum FetchMode
{
    /// <summary>No fetching; v0.1 behaviour.</summary>
    Off,

    /// <summary>Searches and scores but downloads nothing.</summary>
    DryRun,

    /// <summary>Downloads and imports songs.</summary>
    Live,
}

/// <summary>Soulseek search budget: at most <see cref="Searches"/> searches per <see cref="Seconds"/> seconds.</summary>
public readonly record struct SearchLimit(int Searches, int Seconds)
{
    public override string ToString() => $"{Searches}/{Seconds}";
}
