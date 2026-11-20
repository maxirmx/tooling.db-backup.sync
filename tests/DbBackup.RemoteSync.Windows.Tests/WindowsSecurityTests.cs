// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.AccessControl;
using System.Security.Principal;

namespace DbBackup.RemoteSync.Windows.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class WindowsSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-sync-windows-tests", Guid.NewGuid().ToString("N"));

    public WindowsSecurityTests() => Directory.CreateDirectory(_root);

    [Test]
    public async Task DpapiCredentialRoundTripsAndFileIsAclProtected()
    {
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentSid = currentIdentity.User
            ?? throw new InvalidOperationException("The test identity has no SID.");
        var paths = new ApplicationDataPaths(_root);
        var store = new DpapiCredentialStore(paths, currentSid);

        await store.SaveAsync("correct horse battery staple");

        Assert.That(await store.LoadAsync(), Is.EqualTo("correct horse battery staple"));
        var protectedBytes = await File.ReadAllBytesAsync(paths.CredentialFile);
        Assert.That(protectedBytes.AsSpan().IndexOf("correct horse battery staple"u8), Is.LessThan(0));
        var security = new FileInfo(paths.CredentialFile).GetAccessControl();
        var identities = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference)
            .ToArray();
        Assert.That(identities, Has.Member(currentSid));
        Assert.That(identities, Has.No.Member(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
    }

    [Test]
    public void DestinationRuleCanBeGrantedAndRemovedPrecisely()
    {
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentSid = currentIdentity.User
            ?? throw new InvalidOperationException("The test identity has no SID.");
        var destination = Path.Combine(_root, "destination");
        var manager = new ServiceDirectoryAccessManager(currentSid);

        manager.GrantModify(destination);
        Assert.That(ContainsExplicitManagedRule(destination, currentSid), Is.True);

        manager.RemoveManagedRule(destination);
        Assert.That(ContainsExplicitManagedRule(destination, currentSid), Is.False);
    }

    [Test]
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

        Assert.That(loaded, Is.EqualTo(settings));
        Assert.That(Directory.EnumerateFiles(_root, "*.tmp"), Is.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool ContainsExplicitManagedRule(string path, SecurityIdentifier sid)
    {
        var rules = new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        return rules.Any(rule =>
            rule.IdentityReference.Equals(sid) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }
}
