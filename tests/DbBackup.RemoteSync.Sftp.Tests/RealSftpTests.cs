// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.Cryptography;
using Renci.SshNet.Common;

namespace DbBackup.RemoteSync.Sftp.Tests;

[Category("Integration")]
public sealed class RealSftpTests
{
    [Test]
    public async Task AuthenticatesTrustsAndListsConfiguredServer()
    {
        var settings = CreateSettings();
        var password = GetRequiredEnvironmentVariable("SFTP_TEST_PASSWORD");
        var fingerprint = GetRequiredEnvironmentVariable("SFTP_TEST_FINGERPRINT");
        var expectedFile = GetRequiredEnvironmentVariable("SFTP_TEST_EXPECTED_FILE").Replace('\\', '/');
        var expectedSha256 = GetRequiredEnvironmentVariable("SFTP_TEST_EXPECTED_SHA256");

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
        var matches = inventory.Files.Where(candidate => candidate.RelativePath == expectedFile).ToArray();
        Assert.That(matches, Has.Length.EqualTo(1), $"Expected exactly one remote file named '{expectedFile}'.");
        var file = matches[0];
        await client.TestFileReadAsync(file, CancellationToken.None);
        await using var content = new MemoryStream();
        await client.DownloadFileAsync(file, content, CancellationToken.None);
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));

        Assert.That(presented?.Sha256Fingerprint, Is.EqualTo(fingerprint));
        Assert.That(actualSha256, Is.EqualTo(expectedSha256).IgnoreCase);
        Assert.That(inventory.Files.Select(candidate => candidate.RelativePath), Has.None.Contains("\\"));
    }

    [Test]
    public async Task RejectsMismatchedHostKey()
    {
        var settings = CreateSettings();
        var password = GetRequiredEnvironmentVariable("SFTP_TEST_PASSWORD");
        PresentedHostKey? presented = null;
        await using var client = new SftpRemoteFileClient(
            settings,
            password,
            key =>
            {
                presented = key;
                return false;
            });

        await Assert.ThatAsync(
            () => client.ListFilesAsync(CancellationToken.None),
            Throws.TypeOf<SshConnectionException>());
        Assert.That(presented, Is.Not.Null, "The server must reach host key validation.");
    }

    private static RemoteSyncSettings CreateSettings() => new()
    {
        Connection = new ConnectionSettings
        {
            Host = GetRequiredEnvironmentVariable("SFTP_TEST_HOST"),
            Port = int.TryParse(Environment.GetEnvironmentVariable("SFTP_TEST_PORT"), out var parsedPort)
                ? parsedPort
                : 22,
            Username = GetRequiredEnvironmentVariable("SFTP_TEST_USERNAME"),
            RemoteFolder = Environment.GetEnvironmentVariable("SFTP_TEST_FOLDER") ?? "/",
        },
        Destination = new DestinationSettings
        {
            LocalFolder = Path.Combine(Path.GetTempPath(), "remote-sync-sftp-tests", Guid.NewGuid().ToString("N")),
        },
    };

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Ignore($"Set {name} to run this integration test.");
        }

        return value;
    }
}
