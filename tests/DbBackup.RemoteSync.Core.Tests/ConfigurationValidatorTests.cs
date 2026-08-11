namespace DbBackup.RemoteSync.Core.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void ValidSettingsHaveNoIssues()
    {
        var issues = ConfigurationValidator.Validate(TestSettings.Create(@"C:\Backups\Remote"));

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"C:\")]
    [InlineData("relative")]
    public void InvalidDestinationIsRejected(string path)
    {
        var issues = ConfigurationValidator.Validate(TestSettings.Create(path));

        Assert.Contains(issues, issue => issue.Field == "destination.localFolder");
    }

    [Theory]
    [InlineData("bad user")]
    [InlineData("-option")]
    [InlineData("")]
    public void InvalidUsernameIsRejected(string username)
    {
        var settings = TestSettings.Create(@"C:\Backups\Remote") with
        {
            Connection = TestSettings.Create(@"C:\Backups\Remote").Connection with { Username = username },
        };

        Assert.Contains(ConfigurationValidator.Validate(settings), issue => issue.Field == "connection.username");
    }

    [Fact]
    public void UnsupportedSchemaIsRejected()
    {
        var settings = TestSettings.Create(@"C:\Backups\Remote") with { SchemaVersion = 2 };

        Assert.Contains(ConfigurationValidator.Validate(settings), issue => issue.MessageKey == "UnsupportedSchema");
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
