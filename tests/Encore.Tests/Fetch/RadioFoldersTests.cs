using Encore.Fetch.Import;

namespace Encore.Tests.Fetch;

/// <summary>Startup creation of the radio dir's soulseek folder.</summary>
public sealed class RadioFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-radio-" + Guid.NewGuid().ToString("N"));

    public RadioFoldersTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RF01_a_missing_soulseek_folder_is_created_once()
    {
        Assert.True(RadioFolders.EnsureSoulseekFolder(_root));
        Assert.True(Directory.Exists(Path.Combine(_root, "soulseek")));

        Assert.False(RadioFolders.EnsureSoulseekFolder(_root));
    }

    [Fact]
    public void RF02_an_existing_folder_and_its_files_are_left_alone()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "soulseek"));
        var file = Path.Combine(dir.FullName, "keep.txt");
        File.WriteAllText(file, "x");

        Assert.False(RadioFolders.EnsureSoulseekFolder(_root));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void RF03_the_folder_gets_mode_0775_on_linux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        RadioFolders.EnsureSoulseekFolder(_root);

        var mode = File.GetUnixFileMode(Path.Combine(_root, "soulseek"));
        Assert.Equal((UnixFileMode)0b111_111_101, mode);
    }
}
