// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Principal;
using DbBackup.RemoteSync.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackup.RemoteSync.Windows.Tests;

public sealed class RemoteSyncWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "remote-sync-worker-tests",
        Guid.NewGuid().ToString("N"));

    public RemoteSyncWorkerTests() => Directory.CreateDirectory(_root);

    [Fact]
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
            Assert.NotNull(status);
            Assert.False(status.IsRunning);
            Assert.Null(status.LastRun);
            Assert.Equal(0, remote.DownloadCalls);
            var state = await stateStore.LoadAsync();
            Assert.Equal(ScheduledSlotStatus.Skipped, state.ScheduledStatus);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
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

            Assert.True(response.Accepted);
            Assert.Equal("ReloadAndRunQueued", response.Code);
            await WaitUntilAsync(() =>
            {
                var status = worker.GetStatus().Status;
                return status?.LastRun?.Succeeded == true && status.NextAttemptUtc is not null;
            });

            var finalStatus = worker.GetStatus().Status;
            Assert.NotNull(finalStatus);
            Assert.Equal("manual", finalStatus.LastRun?.Reason);
            Assert.Equal(1, finalStatus.LastRun?.RemoteFiles);
            Assert.Equal(1, finalStatus.LastRun?.Downloaded);
            Assert.Equal(1, remote.DownloadCalls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
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
            Assert.True(runResponse.Accepted);
            await WaitUntilAsync(() => worker.GetStatus().Status?.IsRunning == true);

            var response = worker.RequestReloadAndRunNow();

            Assert.False(response.Accepted);
            Assert.Equal("AlreadyRunning", response.Code);
            await WaitUntilAsync(() => worker.GetStatus().Status?.ActiveFile == "database.sql");
            var activeStatus = worker.GetStatus().Status;
            Assert.NotNull(activeStatus);
            Assert.Equal(0, activeStatus.ActiveBytesDownloaded);
            Assert.Equal(4, activeStatus.ActiveTotalBytes);
            Assert.Equal(1, activeStatus.ActiveFileNumber);
            Assert.Equal(1, activeStatus.ActiveFileCount);
            Assert.Equal(0, activeStatus.ActiveCompletedFiles);
            Assert.Equal(0, activeStatus.ActiveOverallBytesDownloaded);
            Assert.Equal(4, activeStatus.ActiveOverallTotalBytes);
            releaseDownload.SetResult();
            await WaitUntilAsync(() => worker.GetStatus().Status?.LastRun?.Succeeded == true);
            Assert.Null(worker.GetStatus().Status?.ActiveFile);
            Assert.Equal(1, remote.DownloadCalls);
        }
        finally
        {
            releaseDownload.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
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
            Assert.True(worker.RequestRunNow().Accepted);
            await WaitUntilAsync(() => worker.GetStatus().Status?.ActiveFile == "database.sql");

            var response = worker.RequestCancel();

            Assert.True(response.Accepted);
            Assert.Equal("CancellationRequested", response.Code);
            await WaitUntilAsync(() => worker.GetStatus().Status?.IsRunning == false);
            Assert.Equal(1, remote.DownloadCalls);
            Assert.False(worker.RequestCancel().Accepted);
            var destination = CreateSettings().Destination.LocalFolder;
            Assert.Empty(Directory.EnumerateFiles(destination, ".db-backup-download-*.partial"));
            var state = await stateStore.LoadAsync();
            Assert.Equal(ScheduledSlotStatus.Skipped, state.ScheduledStatus);
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
            Assert.Equal("secret", password);
            Assert.True(hostKeyValidator(new PresentedHostKey(
                settings.Connection.Host,
                settings.Connection.Port,
                "ssh-ed25519",
                "SHA256:test")));
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
