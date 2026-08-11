#Requires -Version 7.0

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$rootPrefix = $projectRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $artifactsDirectory.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The artifacts directory is outside the project root: $artifactsDirectory"
}

Push-Location $projectRoot
try {
    dotnet restore '.\DbBackup.RemoteSync.slnx' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet build '.\src\DbBackup.RemoteSync.Service\DbBackup.RemoteSync.Service.csproj' `
        --configuration $Configuration --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'The service build failed.' }

    dotnet build '.\src\DbBackup.RemoteSync.Configuration\DbBackup.RemoteSync.Configuration.csproj' `
        --configuration $Configuration --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'The configuration utility build failed.' }

    if (-not $SkipTests) {
        foreach ($testProject in @(
            '.\tests\DbBackup.RemoteSync.Core.Tests\DbBackup.RemoteSync.Core.Tests.csproj',
            '.\tests\DbBackup.RemoteSync.Windows.Tests\DbBackup.RemoteSync.Windows.Tests.csproj',
            '.\tests\DbBackup.RemoteSync.Sftp.Tests\DbBackup.RemoteSync.Sftp.Tests.csproj'
        )) {
            dotnet test $testProject --configuration $Configuration --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject" }
        }
    }

    if (Test-Path -LiteralPath $artifactsDirectory) {
        Remove-Item -LiteralPath $artifactsDirectory -Recurse -Force
    }

    dotnet publish '.\src\DbBackup.RemoteSync.Service\DbBackup.RemoteSync.Service.csproj' `
        --configuration $Configuration --runtime win-x64 --self-contained true --no-restore `
        -p:Version=$Version --output '.\artifacts\publish\service'
    if ($LASTEXITCODE -ne 0) { throw 'The service publish failed.' }

    dotnet publish '.\src\DbBackup.RemoteSync.Configuration\DbBackup.RemoteSync.Configuration.csproj' `
        --configuration $Configuration --runtime win-x64 --self-contained true --no-restore `
        -p:Version=$Version --output '.\artifacts\publish\configuration'
    if ($LASTEXITCODE -ne 0) { throw 'The configuration utility publish failed.' }

    foreach ($culture in @('en-us', 'ru-ru')) {
        $cultureSuffix = if ($culture -eq 'ru-ru') { 'ru-RU' } else { 'en-US' }
        dotnet build '.\installer\DbBackup.RemoteSync.Installer.wixproj' `
            --configuration $Configuration --no-restore `
            -p:Version=$Version -p:Cultures=$culture `
            -p:OutputName="DB-Backup-Remote-Sync-$Version-$cultureSuffix"
        if ($LASTEXITCODE -ne 0) { throw "The $culture MSI build failed." }

        $stagingDirectory = Join-Path $artifactsDirectory "installer-staging\$culture"
        $builtMsi = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -Filter '*.msi')
        if ($builtMsi.Count -ne 1) {
            throw "Expected one $culture MSI under $stagingDirectory; found $($builtMsi.Count)."
        }
        $packageDirectory = Join-Path $artifactsDirectory "package\$culture"
        [void] (New-Item -ItemType Directory -Path $packageDirectory -Force)
        Copy-Item -LiteralPath $builtMsi[0].FullName -Destination $packageDirectory
    }

    Get-ChildItem -LiteralPath (Join-Path $artifactsDirectory 'package') -Recurse -Filter '*.msi' |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            Set-Content -LiteralPath ($_.FullName + '.sha256') `
                -Value "$hash  $($_.Name)" -Encoding ascii -NoNewline
        }
}
finally {
    Pop-Location
}
