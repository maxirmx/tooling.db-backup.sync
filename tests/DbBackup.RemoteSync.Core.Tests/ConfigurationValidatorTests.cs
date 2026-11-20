// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class ConfigurationValidatorTests
{
    [Test]
    public void ValidSettingsHaveNoIssues()
    {
        var issues = ConfigurationValidator.Validate(TestSettings.Create(@"C:\Backups\Remote"));

        Assert.That(issues, Is.Empty);
    }

    [TestCase(@"\\server\share")]
    [TestCase(@"C:\")]
    [TestCase("relative")]
    public void InvalidDestinationIsRejected(string path)
    {
        var issues = ConfigurationValidator.Validate(TestSettings.Create(path));

        Assert.That(issues.Select(issue => issue.Field), Has.Member("destination.localFolder"));
    }

    [TestCase("bad user")]
    [TestCase("-option")]
    [TestCase("")]
    public void InvalidUsernameIsRejected(string username)
    {
        var settings = TestSettings.Create(@"C:\Backups\Remote");
        settings = settings with { Connection = settings.Connection with { Username = username } };

        var issues = ConfigurationValidator.Validate(settings);
        Assert.That(issues.Select(issue => issue.Field), Has.Member("connection.username"));
    }

    [Test]
    public void UnsupportedSchemaIsRejected()
    {
        var settings = TestSettings.Create(@"C:\Backups\Remote") with { SchemaVersion = 2 };

        var issues = ConfigurationValidator.Validate(settings);
        Assert.That(issues.Select(issue => issue.MessageKey), Has.Member("UnsupportedSchema"));
    }
}

internal static class TestSettings
{
    public static RemoteSyncSettings Create(string localFolder) => new()
    {
        Connection = new ConnectionSettings
        {
            Host = "backup.example.test",
            Port = 22,
            Username = "backup-operator",
            RemoteFolder = "/var/backups",
        },
        Destination = new DestinationSettings
        {
            LocalFolder = localFolder,
            Recursive = true,
        },
        Schedule = new ScheduleSettings { DailyLocalTime = "02:00" },
        UiCulture = "en",
    };
}
