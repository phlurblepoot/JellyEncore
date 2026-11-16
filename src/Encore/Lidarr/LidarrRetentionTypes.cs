namespace Encore.Lidarr;

/// <summary>One Lidarr command. <see cref="Name"/> is the class name minus "Command" (e.g. <c>RescanFolders</c>); <see cref="Status"/> is camelCase.</summary>
public sealed record LidarrCommand(int Id, string Name, string Status)
{
    /// <summary>Status <c>queued</c> or <c>started</c>.</summary>
    public bool IsRunning =>
        string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "started", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The <c>writeAudioTags</c> values (camelCase strings in Lidarr's config).</summary>
public static class LidarrWriteAudioTags
{
    public const string No = "no";
    public const string NewFiles = "newFiles";
    public const string AllFiles = "allFiles";
    public const string Sync = "sync";

    /// <summary>True when an in-place import of an existing file would retag it (<c>allFiles</c> or <c>sync</c>).</summary>
    public static bool RetagsExistingFiles(string value) =>
        string.Equals(value, AllFiles, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Sync, StringComparison.OrdinalIgnoreCase);
}
