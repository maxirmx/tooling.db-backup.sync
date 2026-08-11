// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace DbBackup.RemoteSync;

public interface IRemoteFileClient : IAsyncDisposable
{
    Task<RemoteFileInventory> ListFilesAsync(CancellationToken cancellationToken);
    Task TestFileReadAsync(RemoteFileEntry file, CancellationToken cancellationToken);
    Task DownloadFileAsync(RemoteFileEntry file, Stream destination, CancellationToken cancellationToken);
}

public interface IRemoteFileClientFactory
{
    IRemoteFileClient Create(
        RemoteSyncSettings settings,
        string password,
        Func<PresentedHostKey, bool> hostKeyValidator);
}

public sealed class SftpRemoteFileClientFactory
    (ILoggerFactory? loggerFactory = null) : IRemoteFileClientFactory
{
    public IRemoteFileClient Create(
        RemoteSyncSettings settings,
        string password,
        Func<PresentedHostKey, bool> hostKeyValidator) =>
        new SftpRemoteFileClient(settings, password, hostKeyValidator, loggerFactory);
}

public sealed class SftpRemoteFileClient : IRemoteFileClient
{
    private static readonly TimeSpan DownloadInactivityTimeout = TimeSpan.FromSeconds(60);
    private const long ProgressFlushIntervalBytes = 64L * 1024 * 1024;
    private readonly RemoteSyncSettings _settings;
    private readonly SftpClient _client;
    private readonly ILogger<SftpRemoteFileClient> _logger;

    public SftpRemoteFileClient(
        RemoteSyncSettings settings,
        string password,
        Func<PresentedHostKey, bool> hostKeyValidator,
        ILoggerFactory? loggerFactory = null)
    {
        _settings = settings;
        _logger = loggerFactory?.CreateLogger<SftpRemoteFileClient>()
            ?? NullLogger<SftpRemoteFileClient>.Instance;
        var connection = settings.Connection;
        var passwordMethod = new PasswordAuthenticationMethod(connection.Username, password);
        var keyboardMethod = new KeyboardInteractiveAuthenticationMethod(connection.Username);
        keyboardMethod.AuthenticationPrompt += (_, eventArgs) =>
        {
            foreach (var prompt in eventArgs.Prompts.Where(prompt => !prompt.IsEchoed))
            {
                prompt.Response = password;
            }
        };

        var connectionInfo = new ConnectionInfo(
            connection.Host,
            connection.Port,
            connection.Username,
            passwordMethod,
            keyboardMethod)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        _client = new SftpClient(connectionInfo)
        {
            OperationTimeout = TimeSpan.FromSeconds(60),
        };
        _client.HostKeyReceived += (_, eventArgs) =>
        {
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(eventArgs.HostKey)).TrimEnd('=');
            var presented = new PresentedHostKey(
                connection.Host,
                connection.Port,
                eventArgs.HostKeyName,
                fingerprint);
            eventArgs.CanTrust = hostKeyValidator(presented);
        };
    }

    public async Task<RemoteFileInventory> ListFilesAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var inventory = new InventoryBuilder();
        var root = NormalizeRemoteRoot(_settings.Connection.RemoteFolder);
        await WalkAsync(root, string.Empty, inventory, cancellationToken).ConfigureAwait(false);
        inventory.Files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return inventory.Build();
    }

    public async Task DownloadFileAsync(
        RemoteFileEntry file,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var remotePath = CombineRemote(
            NormalizeRemoteRoot(_settings.Connection.RemoteFolder),
            file.RelativePath);

        // SSH.NET's optimized DownloadFileAsync path can queue up to 100 reads before
        // returning the first bytes. Some SFTP servers do not tolerate that burst and
        // leave the transfer waiting with an empty local partial file. Opening a normal
        // stream starts conservatively and increases read-ahead only after data arrives.
        _logger.LogInformation(
            "Opening remote file {RemoteFile} ({ExpectedBytes} bytes).",
            file.RelativePath,
            file.Length);
        await using var source = await OpenRemoteStreamAsync(remotePath, file, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Remote file {RemoteFile} opened; waiting for data.", file.RelativePath);

        var buffer = new byte[1024 * 128];
        long downloadedBytes = 0;
        long nextProgressFlush = ProgressFlushIntervalBytes;
        using var inactivitySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivitySource.CancelAfter(DownloadInactivityTimeout);
        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = await source.ReadAsync(buffer, inactivitySource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The SFTP server returned no data for '{file.RelativePath}' within " +
                    $"{DownloadInactivityTimeout.TotalSeconds:0} seconds.");
            }

            if (bytesRead == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            downloadedBytes = checked(downloadedBytes + bytesRead);
            inactivitySource.CancelAfter(DownloadInactivityTimeout);
            if (downloadedBytes == bytesRead || downloadedBytes >= nextProgressFlush)
            {
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Receiving remote file {RemoteFile}: {DownloadedBytes} of {ExpectedBytes} bytes.",
                    file.RelativePath,
                    downloadedBytes,
                    file.Length);
                while (nextProgressFlush <= downloadedBytes)
                {
                    nextProgressFlush += ProgressFlushIntervalBytes;
                }
            }
        }

        if (downloadedBytes != file.Length)
        {
            throw new IOException(
                $"Remote file size changed or the transfer was incomplete: " +
                $"expected {file.Length} bytes, received {downloadedBytes} bytes.");
        }

        _logger.LogInformation(
            "Received remote file {RemoteFile}: {DownloadedBytes} bytes.",
            file.RelativePath,
            downloadedBytes);
    }

    public async Task TestFileReadAsync(
        RemoteFileEntry file,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var remotePath = CombineRemote(
            NormalizeRemoteRoot(_settings.Connection.RemoteFolder),
            file.RelativePath);

        try
        {
            await using var source = await OpenRemoteStreamAsync(remotePath, file, cancellationToken)
                .ConfigureAwait(false);
            if (file.Length == 0)
            {
                return;
            }

            var buffer = new byte[1];
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(DownloadInactivityTimeout);
            int bytesRead;
            try
            {
                bytesRead = await source.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The SFTP server returned no data for '{file.RelativePath}' within " +
                    $"{DownloadInactivityTimeout.TotalSeconds:0} seconds.");
            }

            if (bytesRead == 0)
            {
                throw new EndOfStreamException(
                    $"The SFTP server returned no data for non-empty file '{file.RelativePath}'.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new IOException(
                $"Remote read-permission check failed for '{file.RelativePath}': {exception.Message}",
                exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WalkAsync(
        string remoteDirectory,
        string relativeDirectory,
        InventoryBuilder inventory,
        CancellationToken cancellationToken)
    {
        await foreach (var item in _client.ListDirectoryAsync(remoteDirectory, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Name is "." or "..")
            {
                continue;
            }

            var relativePath = relativeDirectory.Length == 0
                ? item.Name
                : relativeDirectory + "/" + item.Name;
            var resolved = await RemoteEntryClassifier.ResolveAsync(
                item.Attributes,
                GetTypeFlags,
                async token => (await _client.GetAsync(item.FullName, token).ConfigureAwait(false)).Attributes,
                item.FullName,
                cancellationToken).ConfigureAwait(false);
            switch (resolved.Kind)
            {
                case RemoteEntryKind.RegularFile:
                    inventory.Files.Add(new RemoteFileEntry(
                        relativePath,
                        resolved.Metadata.Size,
                        new DateTimeOffset(resolved.Metadata.LastWriteTimeUtc, TimeSpan.Zero)));
                    break;
                case RemoteEntryKind.Directory when _settings.Destination.Recursive:
                    await WalkAsync(item.FullName, relativePath, inventory, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case RemoteEntryKind.Directory:
                    inventory.SkippedDirectories++;
                    break;
                case RemoteEntryKind.SymbolicLink:
                    inventory.SkippedSymbolicLinks++;
                    break;
                case RemoteEntryKind.Special:
                    inventory.SkippedSpecialEntries++;
                    break;
                default:
                    throw new InvalidOperationException("The remote entry type was not resolved.");
            }
        }
    }

    private static RemoteEntryTypeFlags GetTypeFlags(SftpFileAttributes attributes) => new(
        IsRegularFile: attributes.IsRegularFile,
        IsDirectory: attributes.IsDirectory,
        IsSymbolicLink: attributes.IsSymbolicLink,
        IsSocket: attributes.IsSocket,
        IsBlockDevice: attributes.IsBlockDevice,
        IsCharacterDevice: attributes.IsCharacterDevice,
        IsNamedPipe: attributes.IsNamedPipe);

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SftpFileStream> OpenRemoteStreamAsync(
        string remotePath,
        RemoteFileEntry file,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DownloadInactivityTimeout);
        try
        {
            return await _client.OpenAsync(
                remotePath,
                FileMode.Open,
                FileAccess.Read,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The SFTP server did not open '{file.RelativePath}' within " +
                $"{DownloadInactivityTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static string NormalizeRemoteRoot(string root) =>
        root == "/" ? root : root.TrimEnd('/');

    private static string CombineRemote(string root, string relativePath) =>
        root == "/" ? "/" + relativePath : root + "/" + relativePath;

    private sealed class InventoryBuilder
    {
        public List<RemoteFileEntry> Files { get; } = [];
        public int SkippedDirectories { get; set; }
        public int SkippedSymbolicLinks { get; set; }
        public int SkippedSpecialEntries { get; set; }

        public RemoteFileInventory Build() => new(
            Files.ToArray(),
            SkippedDirectories,
            SkippedSymbolicLinks,
            SkippedSpecialEntries);
    }
}
