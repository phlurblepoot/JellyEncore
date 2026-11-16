using Encore.Discovery;
using Encore.Fetch.Import;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Fetch;

/// <summary>Importer cases IM01-IM14 (v0.2 design section 15). Every test works in its own temp folder.</summary>
public sealed class ImporterTests : IDisposable
{
    private const string Job = "job-1";
    private const long Plenty = 500_000_000_000;

    private readonly string _root;
    private readonly string _radio;
    private readonly string _downloads;
    private readonly FakeFreeSpace _free = new(Plenty);
    private readonly ListLogger _log = new();
    private readonly FakeTrackDeezer _deezer = new();

    public ImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "encore-import-" + Guid.NewGuid().ToString("N"));
        _radio = Path.Combine(_root, "radio");
        _downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_radio);
        Directory.CreateDirectory(_downloads);
        _deezer.Detail = DaftPunk();
    }

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Importer NewImporter(string radioJellyfinDir = "/music-radio") =>
        new(new ImporterSettings(_radio, radioJellyfinDir, _downloads), _free, _log, _deezer);

    private string JobDir(string job = Job) => Path.Combine(_downloads, "encore", job);

    private string SourceFlac(double seconds = 224, string job = Job, string name = "04 Harder Better Faster Stronger.flac") =>
        AudioFixtures.Flac(Path.Combine(JobDir(job), name), seconds);

    private string SourceMp3(double seconds = 224, int kbps = 320, string job = Job) =>
        AudioFixtures.Mp3(Path.Combine(JobDir(job), "04 Harder Better Faster Stronger.mp3"), seconds, kbps);

    private static ImportRequest Request(string source, string job = Job, int? duration = 224, long? deezerId = 3135556) =>
        new(job, source, "Daft Punk", "Harder, Better, Faster, Stronger", "Discovery", duration, deezerId,
            Guid.Parse("056e4f3e-d505-4dad-8ec1-d04f521cbb56"));

    private static DeezerTrackDetail DaftPunk() =>
        new(3135556, "Harder, Better, Faster, Stronger", "Harder, Better, Faster, Stronger", "GBDUW0000059", 224,
            4, 1, "2001-03-07",
            new DeezerArtist(27, "Daft Punk", 1000, true),
            new DeezerAlbumRef(302127, "Discovery", "2001-03-12"),
            [new DeezerContributor(27, "Daft Punk", "Main"), new DeezerContributor(99, "Romanthony", "Featured")]);

    private string Final(string relative) => Path.Combine(_radio, relative.Replace('/', Path.DirectorySeparatorChar));

    private const string DaftPunkRelative = "soulseek/Daft Punk/Discovery (2001)/04 - Harder, Better, Faster, Stronger";

    private string[] StagedFiles()
    {
        var staging = Path.Combine(_radio, SafeNames.StagingFolder);
        return Directory.Exists(staging) ? Directory.GetFiles(staging, "*", SearchOption.AllDirectories) : [];
    }

    private string[] ImportedFiles()
    {
        var dir = Path.Combine(_radio, SafeNames.SoulseekFolder);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories) : [];
    }

    private static void AssertTags(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        Assert.Equal("Harder, Better, Faster, Stronger", tag.Title);
        Assert.Equal(["Daft Punk", "Romanthony"], tag.Performers);
        Assert.Equal(["Daft Punk"], tag.AlbumArtists);
        Assert.Equal("Discovery", tag.Album);
        Assert.Equal(4u, tag.Track);
        Assert.Equal(1u, tag.Disc);
        Assert.Equal(2001u, tag.Year);
        Assert.Equal("GBDUW0000059", tag.ISRC);
        Assert.Equal("056e4f3e-d505-4dad-8ec1-d04f521cbb56", tag.MusicBrainzArtistId);
    }

    [Fact]
    public async Task IM01_flac_is_tagged_moved_and_read_back()
    {
        var source = SourceFlac();

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(Final(DaftPunkRelative + ".flac"), result.Path);
        Assert.True(File.Exists(result.Path));
        AssertTags(result.Path!);
        using (var file = TagLib.File.Create(result.Path!))
            Assert.NotNull(file.GetTag(TagLib.TagTypes.Xiph, false));
        Assert.Equal("flac", result.Audio!.Format);
        Assert.Equal(224, result.Audio.DurationS);
        Assert.Equal(44100, result.Audio.SampleRate);
        Assert.Equal(16, result.Audio.BitDepth);
        Assert.False(result.ReusedExisting);
        Assert.False(result.SourceLeftBehind);
        Assert.Empty(StagedFiles());
        Assert.Contains(_log.Events, e => e.Id == 7300);
    }

    [Fact]
    public async Task IM02_mp3_is_tagged_as_id3v24()
    {
        var source = SourceMp3();

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(Final(DaftPunkRelative + ".mp3"), result.Path);
        AssertTags(result.Path!);
        using (var file = TagLib.File.Create(result.Path!))
        {
            var id3 = Assert.IsType<TagLib.Id3v2.Tag>(file.GetTag(TagLib.TagTypes.Id3v2, false));
            Assert.Equal(4, id3.Version);
        }
        Assert.Equal(320, result.Audio!.Kbps);
        Assert.Equal(224, result.Audio.DurationS);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public void IM03_final_path_and_its_variants()
    {
        var full = new TrackTags("Harder, Better, Faster, Stronger", ["Daft Punk"], "Daft Punk", "Discovery", 4, 1, 2001, null, null);
        Assert.Equal(DaftPunkRelative + ".flac", SafeNames.RelativePath(full, "flac"));
        Assert.Equal("soulseek/Daft Punk/Discovery/04 - Harder, Better, Faster, Stronger.flac",
            SafeNames.RelativePath(full with { Year = null }, "flac"));
        Assert.Equal("soulseek/Daft Punk/Singles/04 - Harder, Better, Faster, Stronger.mp3",
            SafeNames.RelativePath(full with { Album = null, Year = null }, "mp3"));
        Assert.Equal("soulseek/Daft Punk/Discovery (2001)/Harder, Better, Faster, Stronger.flac",
            SafeNames.RelativePath(full with { Track = null }, "flac"));
        Assert.Equal("soulseek/Daft Punk/Discovery (2001)/04 - Harder, Better, Faster, Stronger (3).flac",
            SafeNames.RelativePath(full, "flac", 3));
        Assert.Equal("soulseek/Daft Punk/Discovery (2001)/112 - Harder, Better, Faster, Stronger.flac",
            SafeNames.RelativePath(full with { Track = 112 }, "flac"));
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("What? <Now> \"Really\" | a:b*c\\d", "What_ _Now_ _Really_ _ a_b_c_d")]
    [InlineData("tab\there", "tab_here")]
    [InlineData(".hidden", "_hidden")]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("", "_")]
    [InlineData("   ", "_")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("  spaced . . ", "spaced")]
    [InlineData("..and more", "_.and more")]
    public void IM04_segments_are_made_safe(string input, string expected) =>
        Assert.Equal(expected, SafeNames.Segment(input));

    [Fact]
    public void IM04_long_names_are_cut_to_120_characters()
    {
        var title = new string('x', 200);
        Assert.Equal(120, SafeNames.Segment(title).Length);
        var name = SafeNames.FileName("01 - " + title, "flac", 2);
        Assert.Equal(120, name.Length);
        Assert.EndsWith(" (2).flac", name);

        // A cut that lands on trailing dots or spaces is trimmed again; a cut never splits a surrogate pair.
        Assert.Equal(new string('a', 118), SafeNames.Segment(new string('a', 118) + ". bbb"));
        var emoji = new string('a', 119) + "\U0001F3B5";
        Assert.Equal(new string('a', 119), SafeNames.Segment(emoji));
    }

    [Fact]
    public async Task IM05_collision_with_a_different_size_adds_a_number()
    {
        var taken = Final(DaftPunkRelative + ".flac");
        Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
        await File.WriteAllBytesAsync(taken, new byte[10]);

        var result = await NewImporter().ImportAsync(Request(SourceFlac()), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(Final(DaftPunkRelative + " (2).flac"), result.Path);
        Assert.Equal(10, new FileInfo(taken).Length);
        Assert.Equal("/music-radio/" + DaftPunkRelative + " (2).flac", result.JellyfinPath);
    }

    [Fact]
    public async Task IM05_collision_with_the_same_file_keeps_the_existing_one()
    {
        var importer = NewImporter();
        var first = await importer.ImportAsync(Request(SourceFlac(job: "job-a"), "job-a"), CancellationToken.None);
        var second = await importer.ImportAsync(Request(SourceFlac(job: "job-b"), "job-b"), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, second.Status);
        Assert.True(second.ReusedExisting);
        Assert.Equal(first.Path, second.Path);
        Assert.Single(ImportedFiles());
        Assert.Empty(StagedFiles());
        Assert.False(Directory.Exists(JobDir("job-b")));
    }

    [Fact]
    public async Task IM06_nothing_is_left_in_staging_after_success_or_failure()
    {
        var importer = NewImporter();
        await importer.ImportAsync(Request(SourceFlac(job: "ok"), "ok"), CancellationToken.None);
        Assert.Empty(StagedFiles());

        await importer.ImportAsync(Request(SourceFlac(seconds: 100, job: "bad"), "bad"), CancellationToken.None);
        Assert.Empty(StagedFiles());

        var corrupt = Path.Combine(JobDir("junk"), "junk.flac");
        Directory.CreateDirectory(JobDir("junk"));
        await File.WriteAllBytesAsync(corrupt, Enumerable.Repeat((byte)7, 5000).ToArray());
        var junk = await importer.ImportAsync(Request(corrupt, "junk"), CancellationToken.None);
        Assert.Equal(ImportStatus.Mismatch, junk.Status);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task IM07_length_five_seconds_off_is_a_mismatch()
    {
        var source = SourceFlac(seconds: 229);

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Mismatch, result.Status);
        Assert.Equal(ImportReason.Mismatch, result.Reason);
        Assert.Null(result.Path);
        Assert.Empty(ImportedFiles());
        Assert.Empty(StagedFiles());
        Assert.False(File.Exists(source));          // the bad copy is gone
        Assert.True(Directory.Exists(JobDir()));    // the job folder stays for the next peer
        Assert.Contains(_log.Events, e => e.Id == 7310);
    }

    [Fact]
    public async Task IM07_three_seconds_off_is_accepted_and_an_unknown_reference_skips_the_check()
    {
        var importer = NewImporter();
        var near = await importer.ImportAsync(Request(SourceFlac(seconds: 227, job: "near"), "near"), CancellationToken.None);
        Assert.Equal(ImportStatus.Imported, near.Status);

        _deezer.Detail = null;
        var unknown = await importer.ImportAsync(Request(SourceFlac(seconds: 400, job: "unk"), "unk", duration: null), CancellationToken.None);
        Assert.Equal(ImportStatus.Imported, unknown.Status);
    }

    [Fact]
    public async Task IM08_mp3_at_256_kbps_is_a_mismatch()
    {
        var result = await NewImporter().ImportAsync(Request(SourceMp3(kbps: 256)), CancellationToken.None);

        Assert.Equal(ImportStatus.Mismatch, result.Status);
        Assert.Equal(256, result.Audio!.Kbps);
        Assert.Empty(ImportedFiles());
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task IM08_a_file_that_is_not_what_its_extension_says_is_a_mismatch()
    {
        var fake = Path.Combine(JobDir(), "song.flac");
        AudioFixtures.Mp3(fake, 224);

        var result = await NewImporter().ImportAsync(Request(fake), CancellationToken.None);

        Assert.Equal(ImportStatus.Mismatch, result.Status);
        Assert.Empty(ImportedFiles());
    }

    [Fact]
    public async Task IM08_an_unsupported_format_is_a_mismatch()
    {
        var source = Path.Combine(JobDir(), "song.m4a");
        Directory.CreateDirectory(JobDir());
        await File.WriteAllBytesAsync(source, new byte[100]);

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Mismatch, result.Status);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task IM09_free_space_under_the_floor_plus_the_file_is_disk_low()
    {
        var source = SourceFlac();
        var size = new FileInfo(source).Length;
        _free.Bytes = ImporterSettings.DefaultFreeSpaceFloor + size - 1;

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Equal(ImportReason.DiskLow, result.Reason);
        Assert.Empty(ImportedFiles());
        Assert.Empty(StagedFiles());
        Assert.Equal([_radio], _free.Asked);

        _free.Bytes = ImporterSettings.DefaultFreeSpaceFloor + size;
        var ok = await NewImporter().ImportAsync(Request(SourceFlac(job: "two"), "two"), CancellationToken.None);
        Assert.Equal(ImportStatus.Imported, ok.Status);
    }

    [Fact]
    public async Task IM10_the_slskd_copy_and_its_job_folder_are_deleted()
    {
        var source = SourceFlac();

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.False(File.Exists(source));
        Assert.False(Directory.Exists(JobDir()));
        Assert.True(Directory.Exists(Path.Combine(_downloads, "encore")));
        Assert.DoesNotContain(_log.Events, e => e.Id == 7320);
    }

    [Fact]
    public async Task IM10_a_delete_failure_still_imports_and_reports_the_copy_left_behind()
    {
        var source = SourceFlac();
        ImportResult result;
        if (OperatingSystem.IsWindows())
        {
            // An open handle without delete sharing blocks the delete on Windows.
            using var hold = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);
        }
        else
        {
            // A read-only folder blocks deleting its files (as slskd's 755 folders do for another user).
            File.SetUnixFileMode(JobDir(), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);
            if (Environment.UserName == "root")
                return;   // root ignores folder modes
        }

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.True(result.SourceLeftBehind);
        Assert.True(File.Exists(result.Path));
        Assert.Contains(_log.Events, e => e.Id == 7320);
    }

    [Fact]
    public void IM11_startup_cleanup_empties_staging()
    {
        var staging = Path.Combine(_radio, SafeNames.StagingFolder);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "a.flac"), "x");
        File.WriteAllText(Path.Combine(staging, "b.mp3"), "x");

        Assert.Equal(2, NewImporter().CleanStaging());
        Assert.Empty(StagedFiles());
        Assert.Equal(0, NewImporter().CleanStaging());
    }

    [Fact]
    public void IM11_cleanup_without_a_staging_folder_does_nothing() =>
        Assert.Equal(0, NewImporter().CleanStaging());

    [Fact]
    public async Task IM12_linux_modes_are_0775_for_folders_and_0664_for_files()
    {
        if (OperatingSystem.IsWindows())
            return;   // modes are not set on Windows

        var result = await NewImporter().ImportAsync(Request(SourceFlac()), CancellationToken.None);

        const UnixFileMode folder = (UnixFileMode)0b111_111_101;   // 0775
        const UnixFileMode file = (UnixFileMode)0b110_110_100;     // 0664
        Assert.Equal(file, File.GetUnixFileMode(result.Path!));
        var dir = Path.GetDirectoryName(result.Path!)!;
        Assert.Equal(folder, File.GetUnixFileMode(dir));
        Assert.Equal(folder, File.GetUnixFileMode(Path.GetDirectoryName(dir)!));
        Assert.Equal(folder, File.GetUnixFileMode(Path.Combine(_radio, SafeNames.SoulseekFolder)));
        Assert.Equal(folder, File.GetUnixFileMode(Path.Combine(_radio, SafeNames.StagingFolder)));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task IM13_an_embedded_picture_survives_tagging(string ext)
    {
        var source = ext == "flac" ? SourceFlac() : SourceMp3();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];
        using (var file = TagLib.File.Create(source))
        {
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(png)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" },
            ];
            file.Tag.Comment = "kept";
            file.Save();
        }

        var result = await NewImporter().ImportAsync(Request(source), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        using var imported = TagLib.File.Create(result.Path!);
        var picture = Assert.Single(imported.Tag.Pictures);
        Assert.Equal(png, picture.Data.Data);
        Assert.Equal("kept", imported.Tag.Comment);
        Assert.Equal("Harder, Better, Faster, Stronger", imported.Tag.Title);
    }

    [Theory]
    [InlineData("/music-radio")]
    [InlineData("/music-radio/")]
    public async Task IM14_the_jellyfin_path_uses_slashes_and_the_jellyfin_dir(string jellyfinDir)
    {
        var result = await NewImporter(jellyfinDir).ImportAsync(Request(SourceFlac()), CancellationToken.None);

        Assert.Equal("/music-radio/" + DaftPunkRelative + ".flac", result.JellyfinPath);
    }

    [Fact]
    public async Task A_deezer_failure_falls_back_to_the_candidate_fields()
    {
        _deezer.Throw = true;

        var result = await NewImporter().ImportAsync(Request(SourceFlac()), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(Final("soulseek/Daft Punk/Discovery/Harder, Better, Faster, Stronger.flac"), result.Path);
        using var file = TagLib.File.Create(result.Path!);
        Assert.Equal(["Daft Punk"], file.Tag.Performers);
        Assert.Equal("Discovery", file.Tag.Album);
        Assert.Equal(0u, file.Tag.Year);
    }

    [Fact]
    public async Task Without_a_deezer_id_no_lookup_is_made()
    {
        var result = await NewImporter().ImportAsync(Request(SourceFlac(), deezerId: null), CancellationToken.None);

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(0, _deezer.Calls);
    }

    [Fact]
    public void Main_contributors_come_first_then_featured_without_duplicates()
    {
        var detail = DaftPunk() with
        {
            Contributors =
            [
                new DeezerContributor(1, "A", "Main"), new DeezerContributor(2, "B", "Featured"),
                new DeezerContributor(3, "C", "Main"), new DeezerContributor(4, "a", "Featured"),
            ],
        };
        var tags = Importer.BuildTags(Request("x.flac"), detail);
        Assert.Equal(["A", "C", "B"], tags.Performers);
        Assert.Equal("Daft Punk", tags.AlbumArtist);

        var noMain = Importer.BuildTags(Request("x.flac"), detail with { Contributors = [new DeezerContributor(2, "B", "Featured")] });
        Assert.Equal(["Daft Punk", "B"], noMain.Performers);

        var trackYear = Importer.BuildTags(Request("x.flac"), detail with { Album = new DeezerAlbumRef(1, "Discovery", "0000-00-00") });
        Assert.Equal(2001, trackYear.Year);
    }

    [Fact]
    public async Task Cancellation_leaves_nothing_in_staging()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewImporter().ImportAsync(Request(SourceFlac(), deezerId: null), cts.Token));

        Assert.Empty(StagedFiles());
        Assert.Empty(ImportedFiles());
    }

    private sealed class FakeTrackDeezer : IDeezerClient
    {
        public DeezerTrackDetail? Detail { get; set; }

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public Task<DeezerTrackDetail?> GetTrackAsync(long trackId, CancellationToken ct)
        {
            Calls++;
            if (Throw)
                throw new HttpRequestException("deezer down");
            return Task.FromResult(Detail);
        }

        public Task<IReadOnlyList<DeezerSearchTrack>> SearchTracksAsync(string query, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<DeezerArtist?> FindArtistAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeezerTrack>> GetArtistRadioAsync(long artistId, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeezerArtist>> GetRelatedArtistsAsync(long artistId, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeezerTrack>> GetArtistTopAsync(long artistId, int limit, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ListLogger : ILogger<Importer>
    {
        public List<(int Id, LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Events)
                Events.Add((eventId.Id, logLevel, formatter(state, exception)));
        }
    }
}
