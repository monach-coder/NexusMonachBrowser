using System.IO.Compression;
using Nexus.Guardian;

namespace NexusMonach.Security.Tests;

public sealed class SilentUpdateCoordinatorTests
{
    [Fact]
    public void SafeExtractor_ExtractsFilesInsideStagingDirectory()
    {
        using var fixture = new ArchiveFixture();
        fixture.Add("Browser/NexusMonach.Browser.exe", "signed payload");

        SilentUpdateCoordinator.ExtractArchiveSafely(fixture.ArchivePath, fixture.StagingDirectory);

        Assert.Equal("signed payload", File.ReadAllText(Path.Combine(
            fixture.StagingDirectory, "Browser", "NexusMonach.Browser.exe")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("Browser/../../outside.txt")]
    public void SafeExtractor_RejectsDirectoryTraversal(string entryName)
    {
        using var fixture = new ArchiveFixture();
        fixture.Add(entryName, "must not escape");

        Assert.Throws<InvalidDataException>(() =>
            SilentUpdateCoordinator.ExtractArchiveSafely(
                fixture.ArchivePath, fixture.StagingDirectory));
        Assert.False(File.Exists(Path.Combine(fixture.RootDirectory, "outside.txt")));
    }

    private sealed class ArchiveFixture : IDisposable
    {
        public string RootDirectory { get; } = Path.Combine(
            Path.GetTempPath(), "NexusUpdateTests", Guid.NewGuid().ToString("N"));
        public string ArchivePath => Path.Combine(RootDirectory, "update.zip");
        public string StagingDirectory => Path.Combine(RootDirectory, "staging");

        public ArchiveFixture() => Directory.CreateDirectory(RootDirectory);

        public void Add(string entryName, string content)
        {
            using var archive = ZipFile.Open(ArchivePath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        public void Dispose()
        {
            try { Directory.Delete(RootDirectory, recursive: true); }
            catch { }
        }
    }
}
