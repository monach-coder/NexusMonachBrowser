using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class JsonStoreTests
{
    [Fact]
    public async Task ConcurrentWritesLeaveOneCompleteDocumentAndNoTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nexus-json-store-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var writes = Enumerable.Range(0, 32)
                .Select(index => Task.Run(() => JsonStore.WriteAsync(path,
                    new StoredValue(index, new string((char)('a' + index % 26), 64 * 1024)))))
                .ToArray();

            await Task.WhenAll(writes);

            var stored = await JsonStore.ReadAsync<StoredValue>(path);
            Assert.NotNull(stored);
            Assert.InRange(stored.Index, 0, 31);
            Assert.Equal(64 * 1024, stored.Content.Length);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private sealed record StoredValue(int Index, string Content);
}
