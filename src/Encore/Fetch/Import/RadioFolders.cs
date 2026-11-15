namespace Encore.Fetch.Import;

/// <summary>Startup setup of the radio dir.</summary>
public static class RadioFolders
{
    private const UnixFileMode FolderMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Creates <c>{radioDir}/soulseek</c> (0775 on Linux) when it is missing, so the radio dir is never empty when Jellyfin
    /// scans (Jellyfin skips a library location that holds nothing). Returns true when the folder was created.
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it cannot be made.
    /// </summary>
    public static bool EnsureSoulseekFolder(string radioDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioDir);
        var dir = Path.Combine(radioDir, SafeNames.SoulseekFolder);
        if (Directory.Exists(dir))
        {
            return false;
        }

        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(dir, FolderMode);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The folder exists; only its mode is left as the umask made it.
            }
        }

        return true;
    }
}
