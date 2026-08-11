namespace DbBackup.RemoteSync;

public static class WindowsPathMapper
{
    private static readonly char[] InvalidFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string ToLocalRelativePath(string remoteRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteRelativePath);

        var components = remoteRelativePath.Split('/');
        foreach (var component in components)
        {
            ValidateComponent(remoteRelativePath, component);
        }

        return Path.Combine(components);
    }

    public static string CombineUnderRoot(string root, string localRelativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, localRelativePath));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The mapped path escapes the destination: {localRelativePath}");
        }

        return combined;
    }

    private static void ValidateComponent(string source, string component)
    {
        if (component.Length == 0 || component is "." or "..")
        {
            throw new InvalidDataException($"The remote path cannot be represented safely: {source}");
        }

        if (component.Any(character =>
                character < 32 ||
                InvalidFileNameCharacters.Contains(character)))
        {
            throw new InvalidDataException($"The remote path contains a Windows-invalid character: {source}");
        }

        if (component.EndsWith(' ') || component.EndsWith('.'))
        {
            throw new InvalidDataException($"A remote path component ends in a dot or space: {source}");
        }

        var baseName = component.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedDevice(baseName, "COM") ||
            IsNumberedDevice(baseName, "LPT"))
        {
            throw new InvalidDataException($"The remote path uses a reserved Windows name: {source}");
        }
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[3] is >= '1' and <= '9';
}
