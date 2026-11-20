// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class RemoteEntryClassifierTests
{
    [Test]
    public void ClassifiesSupportedEntryTypes()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsRegularFile: true)),
                Is.EqualTo(RemoteEntryKind.RegularFile));
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsDirectory: true)),
                Is.EqualTo(RemoteEntryKind.Directory));
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsSymbolicLink: true)),
                Is.EqualTo(RemoteEntryKind.SymbolicLink));
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsNamedPipe: true)),
                Is.EqualTo(RemoteEntryKind.Special));
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsSocket: true)),
                Is.EqualTo(RemoteEntryKind.Special));
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsBlockDevice: true)),
                Is.EqualTo(RemoteEntryKind.Special));
            Assert.That(RemoteEntryClassifier.Classify(new RemoteEntryTypeFlags(IsCharacterDevice: true)),
                Is.EqualTo(RemoteEntryKind.Special));
        }
    }

    [Test]
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

        Assert.That(resolved.Kind, Is.EqualTo(RemoteEntryKind.RegularFile));
        Assert.That(resolved.Metadata.IsRegularFile, Is.True);
        Assert.That(refreshCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task KnownListedTypeDoesNotRefreshMetadata()
    {
        var resolved = await RemoteEntryClassifier.ResolveAsync(
            new RemoteEntryTypeFlags(IsDirectory: true),
            flags => flags,
            _ => throw new InvalidOperationException("Metadata should not be refreshed."),
            "/backups/archive",
            CancellationToken.None);

        Assert.That(resolved.Kind, Is.EqualTo(RemoteEntryKind.Directory));
    }

    [Test]
    public async Task UnknownRefreshedTypeFailsWithRemotePath()
    {
        await Assert.ThatAsync(
            () => RemoteEntryClassifier.ResolveAsync(
                new RemoteEntryTypeFlags(),
                flags => flags,
                _ => Task.FromResult(new RemoteEntryTypeFlags()),
                "/backups/unknown-entry",
                CancellationToken.None),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("/backups/unknown-entry"));
    }
}
