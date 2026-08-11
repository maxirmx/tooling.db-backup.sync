// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class RemoteEntryClassifierTests
{
    [Fact]
    public void ClassifiesSupportedEntryTypes()
    {
        Assert.Equal(
            RemoteEntryKind.RegularFile,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsRegularFile: true)));
        Assert.Equal(
            RemoteEntryKind.Directory,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsDirectory: true)));
        Assert.Equal(
            RemoteEntryKind.SymbolicLink,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsSymbolicLink: true)));
        Assert.Equal(
            RemoteEntryKind.Special,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsNamedPipe: true)));
        Assert.Equal(
            RemoteEntryKind.Special,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsSocket: true)));
        Assert.Equal(
            RemoteEntryKind.Special,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsBlockDevice: true)));
        Assert.Equal(
            RemoteEntryKind.Special,
            RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsCharacterDevice: true)));
    }

    [Fact]
    public async Task UnknownListedTypeIsResolvedFromRefreshedMetadata()
    {
        var refreshCalls = 0;

        var resolved = await RemoteEntryClassifier.ResolveAsync(
            new RemoteEntryTypeFlags(),
            flags => flags,
            _ =>
            {
                refreshCalls++;
                return Task.FromResult(new RemoteEntryTypeFlags(IsRegularFile: true));
            },
            "/backups/database.sql",
            CancellationToken.None);

        Assert.Equal(RemoteEntryKind.RegularFile, resolved.Kind);
        Assert.True(resolved.Metadata.IsRegularFile);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task KnownListedTypeDoesNotRefreshMetadata()
    {
        var resolved = await RemoteEntryClassifier.ResolveAsync(
            new RemoteEntryTypeFlags(IsDirectory: true),
            flags => flags,
            _ => throw new InvalidOperationException("Metadata should not be refreshed."),
            "/backups/archive",
            CancellationToken.None);

        Assert.Equal(RemoteEntryKind.Directory, resolved.Kind);
    }

    [Fact]
    public async Task UnknownRefreshedTypeFailsWithRemotePath()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RemoteEntryClassifier.ResolveAsync(
                new RemoteEntryTypeFlags(),
                flags => flags,
                _ => Task.FromResult(new RemoteEntryTypeFlags()),
                "/backups/unknown-entry",
                CancellationToken.None));

        Assert.Contains("/backups/unknown-entry", exception.Message, StringComparison.Ordinal);
    }
}
