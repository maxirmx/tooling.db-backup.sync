namespace DbBackup.RemoteSync.Sftp.Tests;

public sealed class RealSftpTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuthenticatesTrustsAndListsConfiguredServer()
    {
        var host = Environment.GetEnvironmentVariable("SFTP_TEST_HOST");
        var username = Environment.GetEnvironmentVariable("SFTP_TEST_USERNAME");
        var password = Environment.GetEnvironmentVariable("SFTP_TEST_PASSWORD");
        var fingerprint = Environment.GetEnvironmentVariable("SFTP_TEST_FINGERPRINT");
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password) ||
            string.IsNullOrWhiteSpace(fingerprint))
        {
            return;
        }

        var port = int.TryParse(Environment.GetEnvironmentVariable("SFTP_TEST_PORT"), out var parsedPort)
            ? parsedPort
            : 22;
        var remoteFolder = Environment.GetEnvironmentVariable("SFTP_TEST_FOLDER") ?? "/";
        var settings = new RemoteSyncSettings
        {
            Connection = new ConnectionSettings
            {
                Host = host,
                Port = port,
                Username = username,
                RemoteFolder = remoteFolder,
            },
            Destination = new DestinationSettings
            {
                LocalFolder = Path.Combine(Path.GetTempPath(), "remote-sync-sftp-tests", Guid.NewGuid().ToString("N")),
                Recursive = false,
            },
        };
        PresentedHostKey? presented = null;
        await using var client = new SftpRemoteFileClient(
            settings,
            password,
            key =>
            {
                presented = key;
                return key.Sha256Fingerprint == fingerprint;
            });

        await client.TestConnectionAsync(CancellationToken.None);
        var files = await client.ListFilesAsync(CancellationToken.None);

        Assert.NotNull(presented);
        Assert.Equal(fingerprint, presented.Sha256Fingerprint);
        Assert.All(files, file => Assert.DoesNotContain('\\', file.RelativePath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RejectsMismatchedHostKey()
    {
        var host = Environment.GetEnvironmentVariable("SFTP_TEST_HOST");
        var username = Environment.GetEnvironmentVariable("SFTP_TEST_USERNAME");
        var password = Environment.GetEnvironmentVariable("SFTP_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return;
        }

        var settings = new RemoteSyncSettings
        {
            Connection = new ConnectionSettings
            {
                Host = host,
                Port = int.TryParse(Environment.GetEnvironmentVariable("SFTP_TEST_PORT"), out var parsedPort)
                    ? parsedPort
                    : 22,
                Username = username,
                RemoteFolder = Environment.GetEnvironmentVariable("SFTP_TEST_FOLDER") ?? "/",
            },
            Destination = new DestinationSettings
            {
                LocalFolder = Path.Combine(Path.GetTempPath(), "remote-sync-sftp-tests", Guid.NewGuid().ToString("N")),
            },
        };
        await using var client = new SftpRemoteFileClient(settings, password, _ => false);

        await Assert.ThrowsAnyAsync<Exception>(() => client.TestConnectionAsync(CancellationToken.None));
    }
}
