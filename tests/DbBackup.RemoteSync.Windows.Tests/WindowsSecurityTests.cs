// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.AccessControl;
using System.Security.Principal;

namespace DbBackup.RemoteSync.Windows.Tests;

public sealed class WindowsSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-sync-windows-tests", Guid.NewGuid().ToString("N"));

    public WindowsSecurityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DpapiCredentialRoundTripsAndFileIsAclProtected()
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test identity has no SID.");
        var paths = new ApplicationDataPaths(_root);
        var store = new DpapiCredentialStore(paths, currentSid);

        await store.SaveAsync("correct horse battery staple");

        Assert.Equal("correct horse battery staple", await store.LoadAsync());
        var protectedBytes = await File.ReadAllBytesAsync(paths.CredentialFile);
        Assert.True(protectedBytes.AsSpan().IndexOf("correct horse battery staple"u8) < 0);
        var security = new FileInfo(paths.CredentialFile).GetAccessControl();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.Contains(rules, rule => rule.IdentityReference.Equals(currentSid));
        Assert.DoesNotContain(rules, rule =>
            rule.IdentityReference is SecurityIdentifier sid &&
            sid.IsWellKnown(WellKnownSidType.WorldSid));
    }

    [Fact]
    public void DestinationRuleCanBeGrantedAndRemovedPrecisely()
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test identity has no SID.");
        var destination = Path.Combine(_root, "destination");
        var manager = new ServiceDirectoryAccessManager(currentSid);

        manager.GrantModify(destination);
        Assert.True(ContainsManagedRule(destination, currentSid));

        manager.RemoveManagedRule(destination);
        Assert.False(ContainsManagedRule(destination, currentSid));
    }

    [Fact]
    public async Task StoresWriteVersionedJsonAtomically()
    {
        var paths = new ApplicationDataPaths(_root);
        var store = new SettingsStore(paths);
        var settings = new RemoteSyncSettings
        {
            Connection = new ConnectionSettings
            {
                Host = "backup.example.test",
                Username = "backup",
                RemoteFolder = "/backup",
            },
            Destination = new DestinationSettings { LocalFolder = Path.Combine(_root, "destination") },
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(settings, loaded);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool ContainsManagedRule(string path, SecurityIdentifier sid)
    {
        var rules = new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        return rules.Any(rule =>
            rule.IdentityReference.Equals(sid) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }
}
