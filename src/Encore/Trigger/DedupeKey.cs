using System.Security.Cryptography;
using System.Text;

namespace Encore.Trigger;

/// <summary>
/// Dedupe key (design section 3):
/// <c>{sessionId}|{itemId:N}|{PlaylistItemId ?? "-"}|{sig(EntryQueue)}|{sig(PrevEntryQueue)}</c>.
/// For the Full model the prev part is always "-".
/// </summary>
public static class DedupeKey
{
    public static string Build(
        string sessionId,
        Guid itemId,
        string? playlistItemId,
        IReadOnlyList<QueueEntry> entryQueue,
        IReadOnlyList<QueueEntry>? prevEntryQueue,
        QueueModel model)
    {
        var prev = model == QueueModel.Full ? "-" : Signature(prevEntryQueue);
        return $"{sessionId}|{itemId:N}|{playlistItemId ?? "-"}|{Signature(entryQueue)}|{prev}";
    }

    /// <summary>
    /// "null" for null, "empty" for an empty list, otherwise the first 16 lowercase hex chars of
    /// SHA-256 over <c>Id:PlaylistItemId;</c> per entry, in order (ids in N format).
    /// </summary>
    public static string Signature(IReadOnlyList<QueueEntry>? queue)
    {
        if (queue is null)
        {
            return "null";
        }

        if (queue.Count == 0)
        {
            return "empty";
        }

        var text = new StringBuilder();
        foreach (var entry in queue)
        {
            text.Append(entry.Id.ToString("N")).Append(':').Append(entry.PlaylistItemId).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
