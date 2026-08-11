// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Text.RegularExpressions;

namespace DbBackup.RemoteSync;

public static partial class ConfigurationValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(RemoteSyncSettings settings)
    {
        var issues = new List<ValidationIssue>();
        var connection = settings.Connection;

        if (settings.SchemaVersion != ProductConstants.SettingsSchemaVersion)
        {
            issues.Add(new("schemaVersion", "UnsupportedSchema"));
        }

        if (string.IsNullOrWhiteSpace(connection.Host) ||
            connection.Host.StartsWith('-') ||
            connection.Host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            issues.Add(new("connection.host", "InvalidHost"));
        }

        if (connection.Port is < 1 or > 65535)
        {
            issues.Add(new("connection.port", "InvalidPort"));
        }

        if (string.IsNullOrWhiteSpace(connection.Username) ||
            connection.Username.StartsWith('-') ||
            !UsernamePattern().IsMatch(connection.Username))
        {
            issues.Add(new("connection.username", "InvalidUsername"));
        }

        if (string.IsNullOrWhiteSpace(connection.RemoteFolder) ||
            !connection.RemoteFolder.StartsWith('/') ||
            connection.RemoteFolder.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            issues.Add(new("connection.remoteFolder", "InvalidRemoteFolder"));
        }

        ValidateLocalFolder(settings.Destination.LocalFolder, issues);

        if (!TimeOnly.TryParseExact(
                settings.Schedule.DailyLocalTime,
                "HH:mm",
                null,
                System.Globalization.DateTimeStyles.None,
                out _))
        {
            issues.Add(new("schedule.dailyLocalTime", "InvalidDailyTime"));
        }

        if (settings.UiCulture is not ("en" or "ru"))
        {
            issues.Add(new("uiCulture", "InvalidCulture"));
        }

        return issues;
    }

    private static void ValidateLocalFolder(string localFolder, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(localFolder) ||
            localFolder.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(localFolder))
        {
            issues.Add(new("destination.localFolder", "InvalidLocalFolder"));
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(localFolder);
            var root = Path.GetPathRoot(fullPath);
            if (string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar),
                    root?.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("destination.localFolder", "DriveRootNotAllowed"));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new("destination.localFolder", "InvalidLocalFolder"));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
