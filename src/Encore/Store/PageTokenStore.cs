using System.Globalization;

namespace Encore.Store;

/// <summary>One <c>page_tokens</c> row (T114). The token itself is never stored, only its SHA-256 as 64 uppercase hex digits.</summary>
public sealed record PageTokenRow(string TokenHash, string UserId, DateTimeOffset CreatedUtc, DateTimeOffset LastUsedUtc);

/// <summary>
/// The request page's own sign-in tokens (T114, schema 4). Each row holds the token's hash, the Jellyfin user id
/// (32 hex digits), when it was made and when it was last used. Implemented by <see cref="RequestStore"/>.
/// </summary>
public interface IPageTokenStore
{
    void InsertPageToken(string tokenHash, string userId, DateTimeOffset at);

    PageTokenRow? GetPageToken(string tokenHash);

    void TouchPageToken(string tokenHash, DateTimeOffset at);

    bool DeletePageToken(string tokenHash);

    /// <summary>Deletes every token of one user, or of everyone when <paramref name="userId"/> is null. Returns the count.</summary>
    int DeletePageTokens(string? userId);

    /// <summary>Deletes every token whose user is not in <paramref name="keepUserIds"/>. Returns the count.</summary>
    int DeletePageTokensExcept(IReadOnlySet<string> keepUserIds);

    /// <summary>Deletes tokens last used before <paramref name="unusedBefore"/> or made before <paramref name="createdBefore"/>.</summary>
    int DeleteExpiredPageTokens(DateTimeOffset unusedBefore, DateTimeOffset createdBefore);

    int CountPageTokens();
}

public sealed partial class RequestStore
{
    public void InsertPageToken(string tokenHash, string userId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO page_tokens (token_hash, user_id, created_utc, last_used_utc) VALUES ($hash, $user, $at, $at)";
            cmd.Parameters.AddWithValue("$hash", tokenHash);
            cmd.Parameters.AddWithValue("$user", userId);
            cmd.Parameters.AddWithValue("$at", EncoreStore.FormatUtc(at));
            cmd.ExecuteNonQuery();
        }
    }

    public PageTokenRow? GetPageToken(string tokenHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT token_hash, user_id, created_utc, last_used_utc FROM page_tokens WHERE token_hash = $hash";
            cmd.Parameters.AddWithValue("$hash", tokenHash);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new PageTokenRow(r.GetString(0), r.GetString(1), ParseUtc(r.GetString(2)), ParseUtc(r.GetString(3)))
                : null;
        }
    }

    public void TouchPageToken(string tokenHash, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE page_tokens SET last_used_utc = $at WHERE token_hash = $hash";
            cmd.Parameters.AddWithValue("$hash", tokenHash);
            cmd.Parameters.AddWithValue("$at", EncoreStore.FormatUtc(at));
            cmd.ExecuteNonQuery();
        }
    }

    public bool DeletePageToken(string tokenHash)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM page_tokens WHERE token_hash = $hash";
            cmd.Parameters.AddWithValue("$hash", tokenHash);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int DeletePageTokens(string? userId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            if (userId is null)
            {
                cmd.CommandText = "DELETE FROM page_tokens";
            }
            else
            {
                cmd.CommandText = "DELETE FROM page_tokens WHERE user_id = $user";
                cmd.Parameters.AddWithValue("$user", userId);
            }

            return cmd.ExecuteNonQuery();
        }
    }

    public int DeletePageTokensExcept(IReadOnlySet<string> keepUserIds)
    {
        ArgumentNullException.ThrowIfNull(keepUserIds);
        lock (_gate)
        {
            var users = new List<string>();
            using (var list = _db.CreateCommand())
            {
                list.CommandText = "SELECT DISTINCT user_id FROM page_tokens";
                using var r = list.ExecuteReader();
                while (r.Read())
                {
                    users.Add(r.GetString(0));
                }
            }

            var deleted = 0;
            foreach (var user in users.Where(u => !keepUserIds.Contains(u)))
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM page_tokens WHERE user_id = $user";
                cmd.Parameters.AddWithValue("$user", user);
                deleted += cmd.ExecuteNonQuery();
            }

            return deleted;
        }
    }

    public int DeleteExpiredPageTokens(DateTimeOffset unusedBefore, DateTimeOffset createdBefore)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM page_tokens WHERE last_used_utc < $unused OR created_utc < $created";
            cmd.Parameters.AddWithValue("$unused", EncoreStore.FormatUtc(unusedBefore));
            cmd.Parameters.AddWithValue("$created", EncoreStore.FormatUtc(createdBefore));
            return cmd.ExecuteNonQuery();
        }
    }

    public int CountPageTokens()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM page_tokens";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
