// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using DbBackup.RemoteSync;
using DbBackup.RemoteSync.Service;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = ProductConstants.ServiceName);
builder.Logging.AddEventLog(new EventLogSettings
{
    SourceName = ProductConstants.EventLogSource,
    LogName = "Application",
});

var paths = ApplicationDataPaths.Default;
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<HostTrustStore>();
builder.Services.AddSingleton<SchedulerStateStore>();
builder.Services.AddSingleton<DpapiCredentialStore>();
builder.Services.AddSingleton<IRemoteFileClientFactory, SftpRemoteFileClientFactory>();
builder.Services.AddSingleton<SynchronizationEngine>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RemoteSyncWorker>();
builder.Services.AddSingleton<IServiceControl>(provider => provider.GetRequiredService<RemoteSyncWorker>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<RemoteSyncWorker>());
builder.Services.AddHostedService<NamedPipeControlServer>();

await builder.Build().RunAsync().ConfigureAwait(false);
