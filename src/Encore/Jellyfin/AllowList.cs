namespace Encore.Jellyfin;

/// <summary>An allow-list entry that did not resolve to exactly one Jellyfin user.</summary>
public sealed record AllowListProblem(string Entry, string Reason);

/// <summary>Result of resolving <c>ENCORE_ALLOWED_USERS</c> against Jellyfin's user list.</summary>
public sealed record AllowListResolution(IReadOnlySet<Guid> UserIds, IReadOnlyList<AllowListProblem> Problems);

/// <summary>
/// Maps allow-list entries (user names or user GUIDs) to Jellyfin user ids. Names match case-insensitively.
/// A GUID entry matches by id, so renaming a user does not break the list.
/// </summary>
public static class AllowList
{
    public static AllowListResolution Resolve(IEnumerable<string> entries, IEnumerable<UserDto> users)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(users);

        var known = users.ToList();
        var ids = new HashSet<Guid>();
        var problems = new List<AllowListProblem>();

        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (Guid.TryParse(entry, out var id))
            {
                if (known.Any(u => u.Id == id))
                {
                    ids.Add(id);
                }
                else
                {
                    problems.Add(new AllowListProblem(entry, "no Jellyfin user has this id"));
                }

                continue;
            }

            var matches = known.Where(u => string.Equals(u.Name, entry, StringComparison.OrdinalIgnoreCase)).ToList();
            switch (matches.Count)
            {
                case 1:
                    ids.Add(matches[0].Id);
                    break;
                case 0:
                    problems.Add(new AllowListProblem(entry, "no Jellyfin user has this name"));
                    break;
                default:
                    problems.Add(new AllowListProblem(entry, "more than one Jellyfin user has this name; use the user's id instead"));
                    break;
            }
        }

        return new AllowListResolution(ids, problems);
    }

    /// <summary>Calls GET /Users once and resolves the entries against it.</summary>
    public static async Task<AllowListResolution> ResolveAsync(IJellyfinClient client, IEnumerable<string> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var users = await client.GetUsersAsync(ct).ConfigureAwait(false);
        return Resolve(entries, users);
    }
}
