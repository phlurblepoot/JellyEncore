namespace Encore.Tests.Store;

/// <summary>SQL that turns a current-schema test file back into an older one, so a migration test starts from a real old file.</summary>
internal static class StoreTestSql
{
    /// <summary>Removes everything migration 5 (v0.4) added; the file is then a v4 file once user_version is set.</summary>
    public const string DropV5 =
        "DROP TABLE slskd_uploads; DROP TABLE play_throughs; DROP TABLE promotions; DROP TABLE promotion_entries; " +
        "DROP TABLE promotion_userdata; DROP TABLE retention_decisions; " +
        "ALTER TABLE fetched_files DROP COLUMN kept_utc; ALTER TABLE fetched_files DROP COLUMN promoted_utc; " +
        "ALTER TABLE fetched_files DROP COLUMN radio_path; ALTER TABLE fetched_files DROP COLUMN delete_reason; " +
        "ALTER TABLE fetched_files DROP COLUMN play_throughs; ";
}
