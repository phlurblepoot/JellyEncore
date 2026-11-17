using Encore.Discovery;
using Encore.Store;

namespace Encore.Retention;

/// <summary>
/// Finds the main library's own copy of a radio song (T141): an owned item outside the music-radio folder with the same
/// <see cref="TrackKey"/> (artist plus folded title, so another album or edition of the same recording matches, while a live, remix,
/// acoustic or demo version does not). When one exists the radio copy is never promoted; its favorites, playlist places and requests
/// move to the library copy and the radio file goes to the trash.
/// </summary>
public static class LibraryCopy
{
    /// <summary>The library copy of the row's song, or null when the main library has none (or the row has no usable key).</summary>
    public static OwnedEntry? Find(IOwnedIndex owned, TrackedFile row)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(row);
        if (!TrackKey.TryParse(row.TrackKey, out var key))
        {
            return null;
        }

        _ = Guid.TryParse(row.JellyfinItemId, out var radioItem);

        // The oldest copy first: the one the library had before (Lidarr's), not one a later promotion added.
        return owned.EntriesOf(key)
            .Where(e => !e.InRadioDir && e.ItemId != Guid.Empty && e.ItemId != radioItem && !string.IsNullOrEmpty(e.Path))
            .OrderBy(e => e.Created ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.ItemId)
            .FirstOrDefault();
    }

    /// <summary>The library copy's Jellyfin path, for the planner and the report (null when there is none).</summary>
    public static Func<TrackedFile, string?> PathFinder(IOwnedIndex owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        return row => Find(owned, row)?.Path;
    }
}
