using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Encore.Config;

/// <summary>Loads or creates the persistent device id, so Jellyfin's device list does not grow on every restart.</summary>
public static partial class DeviceIdentity
{
    public const string FileName = "device-id";

    public static string LoadOrCreate(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (IdPattern().IsMatch(existing))
            {
                return existing;
            }
        }

        var id = "encore-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        File.WriteAllText(path, id + "\n");
        return id;
    }

    [GeneratedRegex("^encore-[0-9a-f]{12}$")]
    private static partial Regex IdPattern();
}
