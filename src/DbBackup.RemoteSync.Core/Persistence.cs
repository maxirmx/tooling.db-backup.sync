// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbBackup.RemoteSync;

public static class AtomicFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The data file must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static async Task WriteBytesAsync(
        string path,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The data file must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, value.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class SettingsStore(ApplicationDataPaths paths)
{
    public async Task<RemoteSyncSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await AtomicFile.ReadJsonAsync<RemoteSyncSettings>(paths.SettingsFile, cancellationToken)
            .ConfigureAwait(false);
        if (settings is not null && settings.SchemaVersion != ProductConstants.SettingsSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported settings schema {settings.SchemaVersion}.");
        }

        return settings;
    }

    public Task SaveAsync(RemoteSyncSettings settings, CancellationToken cancellationToken = default)
    {
        var issues = ConfigurationValidator.Validate(settings);
        if (issues.Count != 0)
        {
            throw new InvalidDataException($"Invalid settings: {string.Join(", ", issues.Select(x => x.MessageKey))}");
        }

        return AtomicFile.WriteJsonAsync(paths.SettingsFile, settings, cancellationToken);
    }
}

public sealed class HostTrustStore(ApplicationDataPaths paths)
{
    public async Task<TrustedHostKey?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var trust = await AtomicFile.ReadJsonAsync<TrustedHostKey>(paths.TrustFile, cancellationToken)
            .ConfigureAwait(false);
        if (trust is not null && trust.SchemaVersion != ProductConstants.TrustSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported host-trust schema {trust.SchemaVersion}.");
        }

        return trust;
    }

    public Task SaveAsync(TrustedHostKey trust, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(paths.TrustFile, trust, cancellationToken);

    public void Delete()
    {
        if (File.Exists(paths.TrustFile))
        {
            File.Delete(paths.TrustFile);
        }
    }
}

public sealed class SchedulerStateStore(ApplicationDataPaths paths)
{
    public async Task<SchedulerState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = await AtomicFile.ReadJsonAsync<SchedulerState>(paths.StateFile, cancellationToken)
            .ConfigureAwait(false) ?? new SchedulerState();
        if (state.SchemaVersion != ProductConstants.StateSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported state schema {state.SchemaVersion}.");
        }

        return state;
    }

    public Task SaveAsync(SchedulerState state, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(paths.StateFile, state, cancellationToken);
}

public sealed class DpapiCredentialStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("DB Backup Remote Sync credential v1"));

    private readonly ApplicationDataPaths _paths;
    private readonly SecurityIdentifier? _serviceSidOverride;

    public DpapiCredentialStore(ApplicationDataPaths paths, SecurityIdentifier? serviceSidOverride = null)
    {
        _paths = paths;
        _serviceSidOverride = serviceSidOverride;
    }

    public bool Exists => File.Exists(_paths.CredentialFile);

    public async Task SaveAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var clearBytes = Encoding.UTF8.GetBytes(password);
        byte[]? protectedBytes = null;

        try
        {
            protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.LocalMachine);
            await AtomicFile.WriteBytesAsync(_paths.CredentialFile, protectedBytes, cancellationToken)
                .ConfigureAwait(false);
            ApplyCredentialAcl(_paths.CredentialFile, ResolveServiceSid());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        var protectedBytes = await File.ReadAllBytesAsync(_paths.CredentialFile, cancellationToken)
            .ConfigureAwait(false);
        byte[]? clearBytes = null;

        try
        {
            clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }

    private SecurityIdentifier ResolveServiceSid() =>
        _serviceSidOverride ??
        (SecurityIdentifier)new NTAccount(ProductConstants.ServiceAccount)
            .Translate(typeof(SecurityIdentifier));

    private static void ApplyCredentialAcl(string path, SecurityIdentifier serviceSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, serviceSid);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddFullControl(FileSecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
}

public sealed class ServiceDirectoryAccessManager
{
    private readonly SecurityIdentifier _serviceSid;

    public ServiceDirectoryAccessManager(SecurityIdentifier? serviceSid = null)
    {
        _serviceSid = serviceSid ??
            (SecurityIdentifier)new NTAccount(ProductConstants.ServiceAccount)
                .Translate(typeof(SecurityIdentifier));
    }

    public void GrantModify(string directory)
    {
        Directory.CreateDirectory(directory);
        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl(AccessControlSections.Access);
        var rule = CreateRule();
        security.RemoveAccessRuleSpecific(rule);
        security.AddAccessRule(rule);
        directoryInfo.SetAccessControl(security);
    }

    public void RemoveManagedRule(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl(AccessControlSections.Access);
        security.RemoveAccessRuleSpecific(CreateRule());
        directoryInfo.SetAccessControl(security);
    }

    private FileSystemAccessRule CreateRule() => new(
        _serviceSid,
        FileSystemRights.Modify | FileSystemRights.Synchronize,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Allow);
}
