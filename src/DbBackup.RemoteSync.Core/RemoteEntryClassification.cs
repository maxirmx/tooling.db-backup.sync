// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync;

internal enum RemoteEntryKind
{
    Unknown,
    RegularFile,
    Directory,
    SymbolicLink,
    Special,
}

internal readonly record struct RemoteEntryTypeFlags(
    bool IsRegularFile = false,
    bool IsDirectory = false,
    bool IsSymbolicLink = false,
    bool IsSocket = false,
    bool IsBlockDevice = false,
    bool IsCharacterDevice = false,
    bool IsNamedPipe = false);

internal readonly record struct ResolvedRemoteEntry<T>(RemoteEntryKind Kind, T Metadata);

internal static class RemoteEntryClassifier
{
    public static RemoteEntryKind Classify(RemoteEntryTypeFlags flags)
    {
        if (flags.IsSymbolicLink)
        {
            return RemoteEntryKind.SymbolicLink;
        }

        if (flags.IsRegularFile)
        {
            return RemoteEntryKind.RegularFile;
        }

        if (flags.IsDirectory)
        {
            return RemoteEntryKind.Directory;
        }

        return flags.IsSocket ||
            flags.IsBlockDevice ||
            flags.IsCharacterDevice ||
            flags.IsNamedPipe
                ? RemoteEntryKind.Special
                : RemoteEntryKind.Unknown;
    }

    public static async Task<ResolvedRemoteEntry<T>> ResolveAsync<T>(
        T listedMetadata,
        Func<T, RemoteEntryTypeFlags> getFlags,
        Func<CancellationToken, Task<T>> refreshMetadata,
        string remotePath,
        CancellationToken cancellationToken)
    {
        var kind = Classify(getFlags(listedMetadata));
        if (kind != RemoteEntryKind.Unknown)
        {
            return new(kind, listedMetadata);
        }

        var refreshedMetadata = await refreshMetadata(cancellationToken).ConfigureAwait(false);
        kind = Classify(getFlags(refreshedMetadata));
        if (kind == RemoteEntryKind.Unknown)
        {
            throw new InvalidDataException(
                $"The SFTP server did not report a file type for remote entry '{remotePath}'.");
        }

        return new(kind, refreshedMetadata);
    }
}
