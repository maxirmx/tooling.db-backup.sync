// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class WindowsPathMapperTests
{
    [Test]
    public void NestedRemotePathUsesWindowsSeparators()
    {
        Assert.That(WindowsPathMapper.ToLocalRelativePath("year/month/backup.sql"),
            Is.EqualTo(Path.Combine("year", "month", "backup.sql")));
    }

    [TestCase("../secret")]
    [TestCase("folder//file")]
    [TestCase("folder/CON.txt")]
    [TestCase("folder/trailing.")]
    [TestCase("folder/bad:name")]
    public void UnsafeRemotePathIsRejected(string path)
    {
        Assert.Throws<InvalidDataException>(() => WindowsPathMapper.ToLocalRelativePath(path));
    }

    [Test]
    public void CombinedPathCannotEscapeRoot()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsPathMapper.CombineUnderRoot(@"C:\Backups\Remote", @"..\secret.txt"));
    }
}
