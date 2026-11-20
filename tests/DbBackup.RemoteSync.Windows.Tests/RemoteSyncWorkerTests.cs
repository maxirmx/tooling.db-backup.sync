// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Principal;
using DbBackup.RemoteSync.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackup.RemoteSync.Windows.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class RemoteSyncWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "remote-sync-worker-tests",
        Guid.NewGuid().ToString("N"));

    public RemoteSyncWorkerTests() => Directory.CreateDirectory(_root);

    [Test]
    public async Task StartupAfterDailyTimeDoesNotBeginSynchronization()
    {
        var paths = new ApplicationDataPaths(_root);
        var settingsStore = new SettingsStore(paths);
        var trustStore = new HostTrustStore(paths);
        var stateStore = new SchedulerStateStore(paths);
        var credentialStore = CreateCredentialStore(paths);
        var remote = new FakeRemoteClient();
        await settingsStore.SaveAsync(CreateSettings());
        await trustStore.SaveAsync(CreateTrust());
        await credentialStore.SaveAsync("secret");
        var worker = CreateWorker(
            paths,
            settingsStore,
            trustStore,
            stateStore,
            credentialStore,
            remote);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => worker.GetStatus().Status?.NextAttemptUtc is not null);

            var status = worker.GetStatus().Status;
            Assert.That(status, Is.Not.Null);
            Assert.That(status.IsRunning, Is.False);
            Assert.That(status.LastRun, Is.Null);
            Assert.That(remote.DownloadCalls, Is.EqualTo(0));
            var state = await stateStore.LoadAsync();
            Assert.That(state.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Skipped));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Test]
    public async Task ExplicitReloadAndRunAcceptsFirstConfigurationAndDoesNotDuplicateDueSlot()
    {
        var paths = new ApplicationDataPaths(_root);
        var settingsStore = new SettingsStore(paths);
        var trustStore = new HostTrustStore(paths);
        var stateStore = new SchedulerStateStore(paths);
        var credentialStore = CreateCredentialStore(paths);
        var remote = new FakeRemoteClient();
        var worker = CreateWorker(
            paths,
            settingsStore,
            trustStore,
            stateStore,
            credentialStore,
            remote);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await settingsStore.SaveAsync(CreateSettings());
            await trustStore.SaveAsync(CreateTrust());
            await credentialStore.SaveAsync("secret");

            var response = worker.RequestReloadAndRunNow();

            Assert.That(response.Accepted, Is.True);
            Assert.That(response.Code, Is.EqualTo("ReloadAndRunQueued"));
            await WaitUntilAsync(() =>
            {
                var status = worker.GetStatus().Status;
                return status?.LastRun?.Succeeded == true && status.NextAttemptUtc is not null;
            });

            var finalStatus = worker.GetStatus().Status;
            Assert.That(finalStatus, Is.Not.Null);
            Assert.That(finalStatus.LastRun?.Reason, Is.EqualTo("manual"));
            Assert.That(finalStatus.LastRun?.RemoteFiles, Is.EqualTo(1));
            Assert.That(finalStatus.LastRun?.Downloaded, Is.EqualTo(1));
            Assert.That(remote.DownloadCalls, Is.EqualTo(1));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Test]
    public async Task ReloadAndRunIsRejectedWhileSynchronizationIsActive()
    {
        var paths = new ApplicationDataPaths(_root);
        var settingsStore = new SettingsStore(paths);
        var trustStore = new HostTrustStore(paths);
        var stateStore = new SchedulerStateStore(paths);
        var credentialStore = CreateCredentialStore(paths);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new FakeRemoteClient(releaseDownload.Task);
        await settingsStore.SaveAsync(CreateSettings());
        await trustStore.SaveAsync(CreateTrust());
        await credentialStore.SaveAsync("secret");
        var worker = CreateWorker(
            paths,
            settingsStore,
            trustStore,
            stateStore,
            credentialStore,
            remote);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => worker.GetStatus().Status?.ConfigurationValid == true);
            var runResponse = worker.RequestRunNow();
            Assert.That(runResponse.Accepted, Is.True);
            await WaitUntilAsync(() => worker.GetStatus().Status?.IsRunning == true);

            var response = worker.RequestReloadAndRunNow();

            Assert.That(response.Accepted, Is.False);
            Assert.That(response.Code, Is.EqualTo("AlreadyRunning"));
            await WaitUntilAsync(() => worker.GetStatus().Status?.ActiveFile == "database.sql");
            var activeStatus = worker.GetStatus().Status;
            Assert.That(activeStatus, Is.Not.Null);
            Assert.That(activeStatus.ActiveBytesDownloaded, Is.EqualTo(0));
            Assert.That(activeStatus.ActiveTotalBytes, Is.EqualTo(4));
            Assert.That(activeStatus.ActiveFileNumber, Is.EqualTo(1));
            Assert.That(activeStatus.ActiveFileCount, Is.EqualTo(1));
            Assert.That(activeStatus.ActiveCompletedFiles, Is.EqualTo(0));
            Assert.That(activeStatus.ActiveOverallBytesDownloaded, Is.EqualTo(0));
            Assert.That(activeStatus.ActiveOverallTotalBytes, Is.EqualTo(4));
            releaseDownload.SetResult();
            await WaitUntilAsync(() => worker.GetStatus().Status?.LastRun?.Succeeded == true);
            Assert.That(worker.GetStatus().Status?.ActiveFile, Is.Null);
            Assert.That(remote.DownloadCalls, Is.EqualTo(1));
        }
        finally
        {
            releaseDownload.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Test]
    public async Task CancelStopsActiveSynchronizationAndRemovesPartialFile()
    {
        var paths = new ApplicationDataPaths(_root);
        var settingsStore = new SettingsStore(paths);
        var trustStore = new HostTrustStore(paths);
        var stateStore = new SchedulerStateStore(paths);
        var credentialStore = CreateCredentialStore(paths);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new FakeRemoteClient(releaseDownload.Task);
        await settingsStore.SaveAsync(CreateSettings());
        await trustStore.SaveAsync(CreateTrust());
        await credentialStore.SaveAsync("secret");
        var worker = CreateWorker(
            paths,
            settingsStore,
            trustStore,
            stateStore,
            credentialStore,
            remote);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => worker.GetStatus().Status?.ConfigurationValid == true);
            Assert.That(worker.RequestRunNow().Accepted, Is.True);
            await WaitUntilAsync(() => worker.GetStatus().Status?.ActiveFile == "database.sql");

            var response = worker.RequestCancel();

            Assert.That(response.Accepted, Is.True);
            Assert.That(response.Code, Is.EqualTo("CancellationRequested"));
            await WaitUntilAsync(() => worker.GetStatus().Status?.IsRunning == false);
            Assert.That(remote.DownloadCalls, Is.EqualTo(1));
            Assert.That(worker.RequestCancel().Accepted, Is.False);
            var destination = CreateSettings().Destination.LocalFolder;
            Assert.That(Directory.EnumerateFiles(destination, ".db-backup-download-*.partial"), Is.Empty);
            var state = await stateStore.LoadAsync();
            Assert.That(state.ScheduledStatus, Is.EqualTo(ScheduledSlotStatus.Skipped));
        }
        finally
        {
            releaseDownload.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static RemoteSyncWorker CreateWorker(
        ApplicationDataPaths paths,
        SettingsStore settingsStore,
        HostTrustStore trustStore,
        SchedulerStateStore stateStore,
        DpapiCredentialStore credentialStore,
        FakeRemoteClient remote)
    {
        var engine = new SynchronizationEngine(
            new FakeRemoteFactory(remote),
            NullLogger<SynchronizationEngine>.Instance);
        return new RemoteSyncWorker(
            paths,
            settingsStore,
            trustStore,
            stateStore,
            credentialStore,
            engine,
            new FixedTimeProvider(CreateDueTime()),
            NullLogger<RemoteSyncWorker>.Instance);
    }

    private DpapiCredentialStore CreateCredentialStore(ApplicationDataPaths paths)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The test identity has no SID.");
        return new DpapiCredentialStore(paths, sid);
    }

    private RemoteSyncSettings CreateSettings() => new()
    {
        Connection = new ConnectionSettings
        {
            Host = "backup.example.test",
            Port = 22,
            Username = "backup",
            RemoteFolder = "/backups",
        },
        Destination = new DestinationSettings
        {
            LocalFolder = Path.Combine(_root, "destination"),
        },
        Schedule = new ScheduleSettings { DailyLocalTime = "02:00" },
        UiCulture = "en",
    };

    private static TrustedHostKey CreateTrust() => new()
    {
        Host = "backup.example.test",
        Port = 22,
        Algorithm = "ssh-ed25519",
        Sha256Fingerprint = "SHA256:test",
    };

    private static DateTimeOffset CreateDueTime()
    {
        var localNoon = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localNoon, TimeZoneInfo.Local));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The worker did not reach the expected state.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeRemoteFactory(FakeRemoteClient client) : IRemoteFileClientFactory
    {
        public IRemoteFileClient Create(
            RemoteSyncSettings settings,
            string password,
            Func<PresentedHostKey, bool> hostKeyValidator)
        {
            Assert.That(password, Is.EqualTo("secret"));
            var hostKey = new PresentedHostKey(
                settings.Connection.Host,
                settings.Connection.Port,
                "ssh-ed25519",
                "SHA256:test");
            Assert.That(hostKeyValidator(hostKey), Is.True);
            return client;
        }
    }

    private sealed class FakeRemoteClient(Task? releaseDownload = null) : IRemoteFileClient
    {
        public int DownloadCalls { get; private set; }

        public Task<RemoteFileInventory> ListFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteFileInventory(
                [new RemoteFileEntry("database.sql", 4, new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero))],
                SkippedDirectories: 0,
                SkippedSymbolicLinks: 0,
                SkippedSpecialEntries: 0));

        public Task TestFileReadAsync(RemoteFileEntry file, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task DownloadFileAsync(
            RemoteFileEntry file,
            Stream destination,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (releaseDownload is not null)
            {
                await releaseDownload.WaitAsync(cancellationToken);
            }

            await destination.WriteAsync("data"u8.ToArray(), cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
