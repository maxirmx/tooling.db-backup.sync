#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $MsiPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The installer smoke test must run from an elevated process.'
}

$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$installLog = Join-Path ([System.IO.Path]::GetTempPath()) 'DbBackupRemoteSync-install.log'
$uninstallLog = Join-Path ([System.IO.Path]::GetTempPath()) 'DbBackupRemoteSync-uninstall.log'
$installed = $false

try {
    $install = Start-Process msiexec.exe `
        -ArgumentList @('/i', "`"$resolvedMsi`"", '/qn', '/norestart', '/l*v', "`"$installLog`"") `
        -Wait -PassThru
    if ($install.ExitCode -notin @(0, 3010)) {
        throw "MSI installation failed with exit code $($install.ExitCode). Log: $installLog"
    }
    $installed = $true

    $service = Get-CimInstance Win32_Service -Filter "Name='DbBackupRemoteSync'"
    if ($null -eq $service) { throw 'The installed Windows service was not found.' }
    if ($service.StartMode -ne 'Auto') { throw "Unexpected service start mode: $($service.StartMode)" }
    if ($service.StartName -ne 'NT SERVICE\DbBackupRemoteSync') {
        throw "Unexpected service identity: $($service.StartName)"
    }

    $serviceRegistry = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\DbBackupRemoteSync'
    if ($serviceRegistry.DelayedAutoStart -ne 1) { throw 'Delayed automatic start was not configured.' }
    if (-not (Test-Path -LiteralPath "$env:ProgramFiles\DB Backup Remote Sync\DbBackup.RemoteSync.Service.exe")) {
        throw 'The service executable was not installed.'
    }
    if (-not (Test-Path -LiteralPath "$env:ProgramFiles\DB Backup Remote Sync\DbBackup.RemoteSync.Configuration.exe")) {
        throw 'The configuration executable was not installed.'
    }
}
finally {
    if ($installed) {
        $uninstall = Start-Process msiexec.exe `
            -ArgumentList @('/x', "`"$resolvedMsi`"", '/qn', '/norestart', '/l*v', "`"$uninstallLog`"") `
            -Wait -PassThru
        if ($uninstall.ExitCode -notin @(0, 3010)) {
            throw "MSI uninstall failed with exit code $($uninstall.ExitCode). Log: $uninstallLog"
        }
        if (Get-Service -Name DbBackupRemoteSync -ErrorAction SilentlyContinue) {
            throw 'The Windows service remains after uninstall.'
        }
        if (Test-Path -LiteralPath "$env:ProgramData\DB Backup Remote Sync") {
            throw 'The service ProgramData directory remains after uninstall.'
        }
    }
}
