// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync;

public static class ProductConstants
{
    public const string ProductName = "DB Backup Remote Sync";
    public const string ServiceName = "DbBackupRemoteSync";
    public const string ServiceAccount = @"NT SERVICE\DbBackupRemoteSync";
    public const string EventLogSource = ServiceName;
    public const string PipeName = "DbBackupRemoteSync.Control.v1";
    public const int ControlProtocolVersion = 4;
    public const int SettingsSchemaVersion = 1;
    public const int TrustSchemaVersion = 1;
    public const int StateSchemaVersion = 1;
    public static readonly TimeSpan DefaultDailyTime = new(2, 0, 0);
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];
}

public sealed class ApplicationDataPaths
{
    public ApplicationDataPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string CredentialFile => Path.Combine(RootDirectory, "credential.dat");
    public string TrustFile => Path.Combine(RootDirectory, "trusted-host-key.json");
    public string StateFile => Path.Combine(RootDirectory, "state.json");
    public string DiagnosticLogFile => Path.Combine(RootDirectory, "service.log");

    public static ApplicationDataPaths Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProductConstants.ProductName));
}
