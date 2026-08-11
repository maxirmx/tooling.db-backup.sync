// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using Microsoft.Extensions.Logging;

namespace DbBackup.RemoteSync;

public sealed class SynchronizationEngine(
    IRemoteFileClientFactory remoteClientFactory,
    ILogger<SynchronizationEngine> logger)
{
    private const string PartialPattern = ".db-backup-download-*.partial";

    public async Task<SynchronizationResult> SynchronizeAsync(
        RemoteSyncSettings settings,
        string password,
        TrustedHostKey trust,
        CancellationToken cancellationToken)
    {
        var issues = ConfigurationValidator.Validate(settings);
        if (issues.Count != 0)
        {
            throw new InvalidDataException($"Invalid configuration: {string.Join(", ", issues.Select(x => x.MessageKey))}");
        }

        if (!trust.MatchesEndpoint(settings.Connection))
        {
            throw new InvalidDataException("No trusted host key exists for the configured endpoint.");
        }

        var localRoot = Path.GetFullPath(settings.Destination.LocalFolder);
        Directory.CreateDirectory(localRoot);
        CleanupStalePartials(localRoot, DateTimeOffset.UtcNow - TimeSpan.FromHours(24));

        await using var remote = remoteClientFactory.Create(
            settings,
            password,
            presented => trust.Matches(presented));
        var remoteFiles = await remote.ListFilesAsync(cancellationToken).ConfigureAwait(false);
        var mappedFiles = ValidateAndMap(remoteFiles, localRoot);
        var missing = new List<(RemoteFileEntry Remote, string Local)>();
        var alreadyPresent = 0;

        foreach (var mapped in mappedFiles)
        {
            if (Directory.Exists(mapped.Local))
            {
                throw new IOException($"A local directory conflicts with remote file '{mapped.Remote.RelativePath}'.");
            }

            if (File.Exists(mapped.Local))
            {
                alreadyPresent++;
            }
            else
            {
                missing.Add(mapped);
            }
        }

        var downloaded = 0;
        var raceSkipped = 0;
        foreach (var mapped in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(mapped.Local)
                ?? throw new InvalidOperationException("The destination file has no parent directory.");
            Directory.CreateDirectory(parent);
            var partialPath = Path.Combine(parent, $".db-backup-download-{Guid.NewGuid():N}.partial");

            try
            {
                await using (var output = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await remote.DownloadFileAsync(mapped.Remote, output, cancellationToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.SetLastWriteTimeUtc(partialPath, mapped.Remote.LastWriteTimeUtc.UtcDateTime);
                try
                {
                    File.Move(partialPath, mapped.Local, overwrite: false);
                    downloaded++;
                    logger.LogInformation("Downloaded remote file {RemoteFile}.", mapped.Remote.RelativePath);
                }
                catch (IOException) when (File.Exists(mapped.Local))
                {
                    TryDelete(partialPath);
                    raceSkipped++;
                    logger.LogWarning(
                        "The destination appeared during transfer and was not overwritten: {LocalFile}.",
                        mapped.Local);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                TryDelete(partialPath);
                throw new IOException(
                    $"Failed to download '{mapped.Remote.RelativePath}': {exception.Message}",
                    exception);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }
        }

        return new(remoteFiles.Count, alreadyPresent, downloaded, raceSkipped);
    }

    public static IReadOnlyList<(RemoteFileEntry Remote, string Local)> ValidateAndMap(
        IReadOnlyList<RemoteFileEntry> remoteFiles,
        string localRoot)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(RemoteFileEntry Remote, string Local)>(remoteFiles.Count);
        foreach (var remote in remoteFiles)
        {
            var relative = WindowsPathMapper.ToLocalRelativePath(remote.RelativePath);
            if (owners.TryGetValue(relative, out var owner))
            {
                throw new InvalidDataException(
                    $"Remote files '{owner}' and '{remote.RelativePath}' map to the same Windows path.");
            }

            owners.Add(relative, remote.RelativePath);
            result.Add((remote, WindowsPathMapper.CombineUnderRoot(localRoot, relative)));
        }

        return result;
    }

    private void CleanupStalePartials(string localRoot, DateTimeOffset olderThan)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            foreach (var path in Directory.EnumerateFiles(localRoot, PartialPattern, options))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < olderThan.UtcDateTime)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Could not remove stale partial file {PartialFile}.", path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not inspect the destination for stale partial files.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
