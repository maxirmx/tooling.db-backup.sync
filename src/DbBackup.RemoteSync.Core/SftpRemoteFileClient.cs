// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Cryptography;
using Renci.SshNet;

namespace DbBackup.RemoteSync;

public interface IRemoteFileClient : IAsyncDisposable
{
    Task TestConnectionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(CancellationToken cancellationToken);
    Task DownloadFileAsync(RemoteFileEntry file, Stream destination, CancellationToken cancellationToken);
}

public interface IRemoteFileClientFactory
{
    IRemoteFileClient Create(
        RemoteSyncSettings settings,
        string password,
        Func<PresentedHostKey, bool> hostKeyValidator);
}

public sealed class SftpRemoteFileClientFactory : IRemoteFileClientFactory
{
    public IRemoteFileClient Create(
        RemoteSyncSettings settings,
        string password,
        Func<PresentedHostKey, bool> hostKeyValidator) =>
        new SftpRemoteFileClient(settings, password, hostKeyValidator);
}

public sealed class SftpRemoteFileClient : IRemoteFileClient
{
    private readonly RemoteSyncSettings _settings;
    private readonly SftpClient _client;

    public SftpRemoteFileClient(
        RemoteSyncSettings settings,
        string password,
        Func<PresentedHostKey, bool> hostKeyValidator)
    {
        _settings = settings;
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

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var _ in _client.ListDirectoryAsync(
            NormalizeRemoteRoot(_settings.Connection.RemoteFolder),
            cancellationToken).ConfigureAwait(false))
        {
            break;
        }
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<RemoteFileEntry>();
        var root = NormalizeRemoteRoot(_settings.Connection.RemoteFolder);
        await WalkAsync(root, string.Empty, files, cancellationToken).ConfigureAwait(false);
        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return files;
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
        await _client.DownloadFileAsync(remotePath, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WalkAsync(
        string remoteDirectory,
        string relativeDirectory,
        List<RemoteFileEntry> output,
        CancellationToken cancellationToken)
    {
        await foreach (var item in _client.ListDirectoryAsync(remoteDirectory, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Name is "." or ".." || item.IsSymbolicLink)
            {
                continue;
            }

            var relativePath = relativeDirectory.Length == 0
                ? item.Name
                : relativeDirectory + "/" + item.Name;
            if (item.IsRegularFile)
            {
                output.Add(new RemoteFileEntry(
                    relativePath,
                    item.Length,
                    new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            else if (item.IsDirectory && _settings.Destination.Recursive)
            {
                await WalkAsync(item.FullName, relativePath, output, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NormalizeRemoteRoot(string root) =>
        root == "/" ? root : root.TrimEnd('/');

    private static string CombineRemote(string root, string relativePath) =>
        root == "/" ? "/" + relativePath : root + "/" + relativePath;
}
