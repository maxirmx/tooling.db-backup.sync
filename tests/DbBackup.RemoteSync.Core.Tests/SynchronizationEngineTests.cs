// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class SynchronizationEngineTests
{
    [Test]
    public async Task RejectsMismatchedHostKeyBeforeDownload()
    {
        using var temporary = new TemporaryDirectory();
        var remote = new FakeRemoteClient(new Dictionary<string, byte[]> { ["backup.sql"] = [1] });
        var engine = CreateEngine(remote);
        var settings = TestSettings.Create(temporary.Path);
        var trust = CreateTrust() with { Sha256Fingerprint = "SHA256:mismatched" };

        await Assert.ThatAsync(
            () => engine.SynchronizeAsync(settings, "secret", trust, CancellationToken.None),
            Throws.TypeOf<AuthenticationException>());

        Assert.That(remote.Downloaded, Is.Empty);
    }

    [Test]
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
        var progress = new List<SynchronizationProgress>();

        var result = await engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None,
            progress.Add);

        var downloadedPath = Path.Combine(temporary.Path, "nested", "new.sql");
        Assert.That(await File.ReadAllTextAsync(Path.Combine(temporary.Path, "existing.sql")), Is.EqualTo("local"));
        Assert.That(await File.ReadAllTextAsync(downloadedPath), Is.EqualTo("new data"));
        Assert.That(result.AlreadyPresent, Is.EqualTo(1));
        Assert.That(result.Downloaded, Is.EqualTo(1));
        Assert.That(result.RemoteFiles, Is.EqualTo(2));
        Assert.That(File.GetLastWriteTimeUtc(downloadedPath),
            Is.EqualTo(timestamp.UtcDateTime).Within(TimeSpan.FromSeconds(2)));
        Assert.That(progress[0].RemoteFile, Is.EqualTo("nested/new.sql"));
        Assert.That(progress[0].DownloadedBytes, Is.EqualTo(0));
        Assert.That(progress[0].TotalBytes, Is.EqualTo(8));
        Assert.That(progress[0].FileNumber, Is.EqualTo(1));
        Assert.That(progress[0].FileCount, Is.EqualTo(1));
        Assert.That(progress[0].CompletedFiles, Is.EqualTo(0));
        Assert.That(progress[0].OverallDownloadedBytes, Is.EqualTo(0));
        Assert.That(progress[0].OverallTotalBytes, Is.EqualTo(8));
        Assert.That(progress[^1].DownloadedBytes, Is.EqualTo(8));
        Assert.That(progress[^1].CompletedFiles, Is.EqualTo(1));
        Assert.That(progress[^1].OverallDownloadedBytes, Is.EqualTo(8));
    }

    [Test]
    public async Task ProgressIncludesAllPendingFilesAndAggregateBytes()
    {
        using var temporary = new TemporaryDirectory();
        var remote = new FakeRemoteClient(new Dictionary<string, byte[]>
        {
            ["first.sql"] = [1, 2],
            ["second.sql"] = [3, 4, 5],
        });
        var engine = CreateEngine(remote);
        var progress = new List<SynchronizationProgress>();

        await engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None,
            progress.Add);

        var secondFileStart = progress.First(item =>
            item.FileNumber == 2 && item.DownloadedBytes == 0);
        Assert.That(secondFileStart.FileCount, Is.EqualTo(2));
        Assert.That(secondFileStart.CompletedFiles, Is.EqualTo(1));
        Assert.That(secondFileStart.OverallDownloadedBytes, Is.EqualTo(2));
        Assert.That(secondFileStart.OverallTotalBytes, Is.EqualTo(5));
        Assert.That(progress[^1].CompletedFiles, Is.EqualTo(2));
        Assert.That(progress[^1].OverallDownloadedBytes, Is.EqualTo(5));
    }

    [Test]
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

        await Assert.ThatAsync(
            () => engine.SynchronizeAsync(TestSettings.Create(temporary.Path), "secret", CreateTrust(), CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());

        Assert.That(remote.Downloaded, Is.Empty);
    }

    [Test]
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

        Assert.That(await File.ReadAllTextAsync(destination), Is.EqualTo("winner"));
        Assert.That(result.RaceSkipped, Is.EqualTo(1));
        Assert.That(Directory.EnumerateFiles(temporary.Path, ".db-backup-download-*.partial"), Is.Empty);
    }

    [Test]
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

        await Assert.ThatAsync(
            () => engine.SynchronizeAsync(TestSettings.Create(temporary.Path), "secret", CreateTrust(), CancellationToken.None),
            Throws.TypeOf<IOException>());

        Assert.That(File.Exists(Path.Combine(temporary.Path, "second.sql")), Is.False);
        Assert.That(Directory.EnumerateFiles(temporary.Path, ".db-backup-download-*.partial"), Is.Empty);
    }

    [Test]
    public async Task ReturnsRemoteInventorySkipCounts()
    {
        using var temporary = new TemporaryDirectory();
        var remote = new FakeRemoteClient(
            [],
            skippedDirectories: 2,
            skippedSymbolicLinks: 3,
            skippedSpecialEntries: 4);
        var engine = CreateEngine(remote);

        var result = await engine.SynchronizeAsync(
            TestSettings.Create(temporary.Path),
            "secret",
            CreateTrust(),
            CancellationToken.None);

        Assert.That(result.RemoteFiles, Is.EqualTo(0));
        Assert.That(result.SkippedDirectories, Is.EqualTo(2));
        Assert.That(result.SkippedSymbolicLinks, Is.EqualTo(3));
        Assert.That(result.SkippedSpecialEntries, Is.EqualTo(4));
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
            var hostKey = new PresentedHostKey(
                settings.Connection.Host,
                settings.Connection.Port,
                "ssh-ed25519",
                "SHA256:test");
            if (!hostKeyValidator(hostKey))
            {
                throw new AuthenticationException("The fake SFTP server's host key was rejected.");
            }

            return client;
        }
    }

    private sealed class FakeRemoteClient : IRemoteFileClient
    {
        private readonly Dictionary<string, byte[]> _files;
        private readonly DateTimeOffset _timestamp;
        private readonly Action? _beforeDownloadCompletes;
        private readonly string? _failFile;
        private readonly int _skippedDirectories;
        private readonly int _skippedSymbolicLinks;
        private readonly int _skippedSpecialEntries;

        public FakeRemoteClient(
            Dictionary<string, byte[]> files,
            DateTimeOffset? timestamp = null,
            Action? beforeDownloadCompletes = null,
            string? failFile = null,
            int skippedDirectories = 0,
            int skippedSymbolicLinks = 0,
            int skippedSpecialEntries = 0)
        {
            _files = files;
            _timestamp = timestamp ?? DateTimeOffset.UtcNow;
            _beforeDownloadCompletes = beforeDownloadCompletes;
            _failFile = failFile;
            _skippedDirectories = skippedDirectories;
            _skippedSymbolicLinks = skippedSymbolicLinks;
            _skippedSpecialEntries = skippedSpecialEntries;
        }

        public List<string> Downloaded { get; } = [];

        public Task<RemoteFileInventory> ListFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteFileInventory(
                _files
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new RemoteFileEntry(pair.Key, pair.Value.Length, _timestamp))
                    .ToArray(),
                _skippedDirectories,
                _skippedSymbolicLinks,
                _skippedSpecialEntries));

        public Task TestFileReadAsync(RemoteFileEntry file, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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
