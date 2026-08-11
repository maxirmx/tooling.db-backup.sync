// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Core.Tests;

public sealed class WindowsPathMapperTests
{
    [Fact]
    public void NestedRemotePathUsesWindowsSeparators()
    {
        Assert.Equal(Path.Combine("year", "month", "backup.sql"),
            WindowsPathMapper.ToLocalRelativePath("year/month/backup.sql"));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder//file")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/trailing.")]
    [InlineData("folder/bad:name")]
    public void UnsafeRemotePathIsRejected(string path)
    {
        Assert.Throws<InvalidDataException>(() => WindowsPathMapper.ToLocalRelativePath(path));
    }

    [Fact]
    public void CombinedPathCannotEscapeRoot()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsPathMapper.CombineUnderRoot(@"C:\Backups\Remote", @"..\secret.txt"));
    }
}
