// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Cryptography;

namespace DbBackup.RemoteSync.Sftp.Tests;

public sealed class RealSftpTests
{
    [SftpFact(
        "SFTP_TEST_HOST",
        "SFTP_TEST_USERNAME",
        "SFTP_TEST_PASSWORD",
        "SFTP_TEST_FINGERPRINT",
        "SFTP_TEST_EXPECTED_FILE",
        "SFTP_TEST_EXPECTED_SHA256")]
    [Trait("Category", "Integration")]
    public async Task AuthenticatesTrustsAndListsConfiguredServer()
    {
        var host = GetRequiredEnvironmentVariable("SFTP_TEST_HOST");
        var username = GetRequiredEnvironmentVariable("SFTP_TEST_USERNAME");
        var password = GetRequiredEnvironmentVariable("SFTP_TEST_PASSWORD");
        var fingerprint = GetRequiredEnvironmentVariable("SFTP_TEST_FINGERPRINT");
        var expectedFile = GetRequiredEnvironmentVariable("SFTP_TEST_EXPECTED_FILE").Replace('\\', '/');
        var expectedSha256 = GetRequiredEnvironmentVariable("SFTP_TEST_EXPECTED_SHA256");

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

        var inventory = await client.ListFilesAsync(CancellationToken.None);
        var file = Assert.Single(inventory.Files, candidate => candidate.RelativePath == expectedFile);
        await client.TestFileReadAsync(file, CancellationToken.None);
        await using var content = new MemoryStream();
        await client.DownloadFileAsync(file, content, CancellationToken.None);
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));

        Assert.NotNull(presented);
        Assert.Equal(fingerprint, presented.Sha256Fingerprint);
        Assert.Equal(expectedSha256, actualSha256, ignoreCase: true);
        Assert.All(inventory.Files, candidate => Assert.DoesNotContain('\\', candidate.RelativePath));
    }

    [SftpFact("SFTP_TEST_HOST", "SFTP_TEST_USERNAME", "SFTP_TEST_PASSWORD")]
    [Trait("Category", "Integration")]
    public async Task RejectsMismatchedHostKey()
    {
        var host = GetRequiredEnvironmentVariable("SFTP_TEST_HOST");
        var username = GetRequiredEnvironmentVariable("SFTP_TEST_USERNAME");
        var password = GetRequiredEnvironmentVariable("SFTP_TEST_PASSWORD");

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

        await Assert.ThrowsAnyAsync<Exception>(() => client.ListFilesAsync(CancellationToken.None));
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"The required environment variable {name} disappeared after test discovery.");
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SftpFactAttribute : FactAttribute
{
    public SftpFactAttribute(params string[] requiredVariables)
    {
        var missing = requiredVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length != 0)
        {
            Skip = $"Set {string.Join(", ", missing)} to run this integration test.";
        }
    }
}
