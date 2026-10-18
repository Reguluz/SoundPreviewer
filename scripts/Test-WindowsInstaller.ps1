[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or -not $IsWindows) {
    throw 'Installer acceptance checks may run only on a Windows GitHub Actions runner.'
}
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifacts = Join-Path $projectDirectory 'artifacts'
$runId = [Guid]::NewGuid().ToString('N')
$diagnostics = [IO.Path]::GetFullPath((Join-Path $artifacts "installer-smoke-$runId"))
$installDirectory = Join-Path $diagnostics 'application'
if (-not $diagnostics.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $diagnostics)) { throw 'Installer smoke target must be a new directory beneath workspace artifacts.' }
foreach ($path in @($projectDirectory, $artifacts)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing a linked installer smoke target: $path"
    }
}
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{39F6B6EE-0C06-4E99-9F2F-0397C4D32612}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing SoundPreviewer installation is registered; refusing to replace it.' }
[xml]$versionFile = Get-Content -LiteralPath (Join-Path $projectDirectory 'Version.props')
$version = [string]$versionFile.Project.PropertyGroup.Version
$installer = Join-Path $artifacts "releases/SoundPreviewer-$version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'Build the Windows installer before running installer acceptance checks.' }
New-Item -ItemType Directory -Path $diagnostics | Out-Null

function Invoke-InstallerProcess([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Installer acceptance operation timed out.' }
        if ($process.ExitCode -ne 0) { throw "Installer acceptance operation failed: $($process.ExitCode). Logs: $diagnostics" }
    }
    finally { $process.Dispose() }
}

# Use only uniquely named fixtures beneath the actual stable user-data root.
# They are never opened by the UI, so the check does not require an audio device.
$userData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SoundPreviewer'
$fixtureDirectory = Join-Path $userData "installer-smoke-$runId"
$marker = Join-Path $fixtureDirectory 'preserve.txt'
$database = Join-Path $fixtureDirectory 'library.db'
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
[IO.File]::WriteAllText($marker, "TEST ONLY installer preservation fixture $runId")
$createDatabase = @'
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as database:
    database.execute('CREATE TABLE installer_preservation (cache_key TEXT, favorite INTEGER)')
    database.execute('INSERT INTO installer_preservation VALUES (?, ?)', ('TEST-ONLY-existing-cache-record', 1))
    database.execute('PRAGMA user_version=1')
'@
& python -c $createDatabase $database
if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated SQLite preservation fixture.' }
$hashes = @{}
foreach ($file in @($marker, $database)) { $hashes[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
[IO.File]::WriteAllText((Join-Path $diagnostics 'fixture-hashes.json'), ($hashes | ConvertTo-Json))
function Assert-PreservedUserData {
    foreach ($file in $hashes.Keys) {
        if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hashes[$file]) {
            throw "Installer changed existing user-data fixture: $file"
        }
    }
}

$uninstalled = $false
try {
    foreach ($phase in @('install', 'upgrade')) {
        $arguments = @('/CURRENTUSER', "/DIR=$installDirectory", '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/LOG=$(Join-Path $diagnostics "$phase.log")")
        Invoke-InstallerProcess $installer $arguments
        foreach ($file in @('SoundPreviewer.exe', 'SoundPreviewer.Worker.exe', 'Vst2Host-x86/SoundPreviewer.Vst2Host.exe', 'unins000.exe')) {
            if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $file))) { throw "Installed package is missing $file" }
        }
        Assert-PreservedUserData
        & (Join-Path $PSScriptRoot 'Test-DesktopPackage.ps1') -Runtime win-x64 -ApplicationPath (Join-Path $installDirectory 'SoundPreviewer.exe')
        Assert-PreservedUserData
    }
    Invoke-InstallerProcess (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $diagnostics 'uninstall.log')")
    $uninstalled = $true
    if (Test-Path -LiteralPath (Join-Path $installDirectory 'SoundPreviewer.exe')) { throw 'Uninstall left the installed application executable behind.' }
    Assert-PreservedUserData
    [IO.File]::WriteAllText((Join-Path $diagnostics 'result.json'), (@{
        Installed = $true; OverwriteUpgrade = $true; InstalledUiChecked = $true; Uninstalled = $true
        ExistingUserDataPreserved = $true; Version = $version; DataHashes = $hashes
    } | ConvertTo-Json))
    Write-Host 'Installer install, same-package overwrite upgrade, UI startup, and uninstall preserve existing user data.'
}
finally {
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (-not $uninstalled -and (Test-Path -LiteralPath $uninstaller)) {
        try { Invoke-InstallerProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $diagnostics 'cleanup-uninstall.log')") }
        catch { Write-Warning "Installer cleanup failed: $_" }
    }
    # Remove only these two fixture files, never the user's data root or other libraries.
    foreach ($file in @($marker, $database)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    if ((Test-Path -LiteralPath $fixtureDirectory) -and -not (Get-ChildItem -LiteralPath $fixtureDirectory -Force)) {
        Remove-Item -LiteralPath $fixtureDirectory
    }
}
