// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Text.Json.Serialization;

namespace DbBackup.RemoteSync;

public sealed record RemoteSyncSettings
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = ProductConstants.SettingsSchemaVersion;

    [JsonPropertyName("connection")]
    public ConnectionSettings Connection { get; init; } = new();

    [JsonPropertyName("destination")]
    public DestinationSettings Destination { get; init; } = new();

    [JsonPropertyName("schedule")]
    public ScheduleSettings Schedule { get; init; } = new();

    [JsonPropertyName("uiCulture")]
    public string UiCulture { get; init; } = "en";
}

public sealed record ConnectionSettings
{
    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; } = 22;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("remoteFolder")]
    public string RemoteFolder { get; init; } = string.Empty;
}

public sealed record DestinationSettings
{
    [JsonPropertyName("localFolder")]
    public string LocalFolder { get; init; } = string.Empty;

    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }
}

public sealed record ScheduleSettings
{
    [JsonPropertyName("dailyLocalTime")]
    public string DailyLocalTime { get; init; } = "02:00";

    public TimeOnly GetTime() => TimeOnly.ParseExact(DailyLocalTime, "HH:mm", null);
}

public sealed record TrustedHostKey
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = ProductConstants.TrustSchemaVersion;

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = string.Empty;

    [JsonPropertyName("sha256Fingerprint")]
    public string Sha256Fingerprint { get; init; } = string.Empty;

    public bool MatchesEndpoint(ConnectionSettings connection) =>
        Port == connection.Port &&
        string.Equals(Host, connection.Host, StringComparison.OrdinalIgnoreCase);

    public bool Matches(PresentedHostKey presented) =>
        Port == presented.Port &&
        string.Equals(Host, presented.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Algorithm, presented.Algorithm, StringComparison.Ordinal) &&
        string.Equals(Sha256Fingerprint, presented.Sha256Fingerprint, StringComparison.Ordinal);
}

public enum ScheduledSlotStatus
{
    None,
    Pending,
    Succeeded,
    Exhausted,
    Skipped,
}

public sealed record SchedulerState
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = ProductConstants.StateSchemaVersion;

    [JsonPropertyName("scheduledDate")]
    public DateOnly? ScheduledDate { get; init; }

    [JsonPropertyName("scheduledStatus")]
    public ScheduledSlotStatus ScheduledStatus { get; init; }

    [JsonPropertyName("attemptsCompleted")]
    public int AttemptsCompleted { get; init; }

    [JsonPropertyName("nextAttemptUtc")]
    public DateTimeOffset? NextAttemptUtc { get; init; }

    [JsonPropertyName("lastRun")]
    public LastRunState? LastRun { get; init; }
}

public sealed record LastRunState
{
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("startedUtc")]
    public DateTimeOffset StartedUtc { get; init; }

    [JsonPropertyName("completedUtc")]
    public DateTimeOffset CompletedUtc { get; init; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    [JsonPropertyName("remoteFiles")]
    public int RemoteFiles { get; init; }

    [JsonPropertyName("alreadyPresent")]
    public int AlreadyPresent { get; init; }

    [JsonPropertyName("downloaded")]
    public int Downloaded { get; init; }

    [JsonPropertyName("raceSkipped")]
    public int RaceSkipped { get; init; }

    [JsonPropertyName("skippedDirectories")]
    public int SkippedDirectories { get; init; }

    [JsonPropertyName("skippedSymbolicLinks")]
    public int SkippedSymbolicLinks { get; init; }

    [JsonPropertyName("skippedSpecialEntries")]
    public int SkippedSpecialEntries { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record ValidationIssue(string Field, string MessageKey);

public sealed record PresentedHostKey(
    string Host,
    int Port,
    string Algorithm,
    string Sha256Fingerprint);

public sealed record RemoteFileEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record RemoteFileInventory(
    IReadOnlyList<RemoteFileEntry> Files,
    int SkippedDirectories,
    int SkippedSymbolicLinks,
    int SkippedSpecialEntries);

public sealed record SynchronizationProgress(
    string RemoteFile,
    long DownloadedBytes,
    long TotalBytes);

public sealed record SynchronizationResult(
    int RemoteFiles,
    int AlreadyPresent,
    int Downloaded,
    int RaceSkipped,
    int SkippedDirectories,
    int SkippedSymbolicLinks,
    int SkippedSpecialEntries);
