using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class SynchronizationEngineTests
{
    [Fact]
    public async Task DownloadsOnlyMissingFilesAndPreservesTimestamp()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "nested"));
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "existing.sql"), "local");
        var timestamp = new DateTimeOffset(2026, 8, 1, 3, 4, 5, TimeSpan.Zero);
        var remote = new FakeRemoteClient(
            new Dictionary<string, byte[]>
            {
                ["existing.sql"] = "remote"u8.ToArray(),
                ["nested/new.sql"] = "new data"u8.ToArray(),
            },
            timestamp);
        var engine = CreateEngine(remote);

        var result = await engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None);

        Assert.Equal("local", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "existing.sql")));
        Assert.Equal("new data", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "nested", "new.sql")));
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(timestamp.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(temporary.Path, "nested", "new.sql")), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CaseInsensitiveCollisionsFailBeforeDownload()
    {
        using var temporary = new TemporaryDirectory();
        var remote = new FakeRemoteClient(
            new Dictionary<string, byte[]>
            {
                ["Backup.sql"] = [1],
                ["backup.sql"] = [2],
            });
        var engine = CreateEngine(remote);

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None));

        Assert.Empty(remote.Downloaded);
    }

    [Fact]
    public async Task DestinationCreatedDuringTransferIsNotOverwritten()
    {
        using var temporary = new TemporaryDirectory();
        var destination = Path.Combine(temporary.Path, "race.sql");
        var remote = new FakeRemoteClient(
            new Dictionary<string, byte[]> { ["race.sql"] = "remote"u8.ToArray() },
            beforeDownloadCompletes: () => File.WriteAllText(destination, "winner"));
        var engine = CreateEngine(remote);

        var result = await engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None);

        Assert.Equal("winner", await File.ReadAllTextAsync(destination));
        Assert.Equal(1, result.RaceSkipped);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".db-backup-download-*.partial"));
    }

    [Fact]
    public async Task FailedDownloadRemovesPartialAndStopsRun()
    {
        using var temporary = new TemporaryDirectory();
        var remote = new FakeRemoteClient(
            new Dictionary<string, byte[]>
            {
                ["first.sql"] = [1],
                ["second.sql"] = [2],
            },
            failFile: "first.sql");
        var engine = CreateEngine(remote);

        await Assert.ThrowsAsync<IOException>(() => engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(temporary.Path, "second.sql")));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, ".db-backup-download-*.partial"));
    }

    private static SynchronizationEngine CreateEngine(FakeRemoteClient remote) =>
        new(new FakeRemoteFactory(remote), NullLogger<SynchronizationEngine>.Instance);

    private static TrustedHostKey CreateTrust() => new()
    {
        Host = "backup.example.test",
        Port = 22,
        Algorithm = "ssh-ed25519",
        Sha256Fingerprint = "SHA256:test",
    };

    private sealed class FakeRemoteFactory(FakeRemoteClient client) : IRemoteFileClientFactory
    {
        public IRemoteFileClient Create(RemoteSyncSettings settings, string password, Func<PresentedHostKey, bool> hostKeyValidator)
        {
            Assert.True(hostKeyValidator(new PresentedHostKey(
                settings.Connection.Host,
                settings.Connection.Port,
                "ssh-ed25519",
                "SHA256:test")));
            return client;
        }
    }

    private sealed class FakeRemoteClient : IRemoteFileClient
    {
        private readonly Dictionary<string, byte[]> _files;
        private readonly DateTimeOffset _timestamp;
        private readonly Action? _beforeDownloadCompletes;
        private readonly string? _failFile;

        public FakeRemoteClient(
            Dictionary<string, byte[]> files,
            DateTimeOffset? timestamp = null,
            Action? beforeDownloadCompletes = null,
            string? failFile = null)
        {
            _files = files;
            _timestamp = timestamp ?? DateTimeOffset.UtcNow;
            _beforeDownloadCompletes = beforeDownloadCompletes;
            _failFile = failFile;
        }

        public List<string> Downloaded { get; } = [];

        public Task TestConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteFileEntry>>(_files
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new RemoteFileEntry(pair.Key, pair.Value.Length, _timestamp))
                .ToArray());

        public async Task DownloadFileAsync(RemoteFileEntry file, Stream destination, CancellationToken cancellationToken)
        {
            Downloaded.Add(file.RelativePath);
            await destination.WriteAsync(_files[file.RelativePath], cancellationToken);
            _beforeDownloadCompletes?.Invoke();
            if (file.RelativePath == _failFile)
            {
                throw new IOException("Injected failure");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remote-sync-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
