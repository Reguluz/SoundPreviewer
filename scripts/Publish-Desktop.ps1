[CmdletBinding()]
param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $projectDirectory 'artifacts/dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $projectDirectory '.build/nuget-packages' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

function Clear-PublishDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $artifacts = Join-Path $projectDirectory 'artifacts'
    if (-not $full.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish output must be beneath $artifacts"
    }
    $current = $full
    while ($current -ne $projectDirectory) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a linked publish directory: $current"
        }
        $current = Split-Path -Parent $current
    }
    if (Test-Path -LiteralPath $full) {
        $userData = Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object {
            $_.Name -in @('Preview', 'local-data', 'settings.json', 'library-location.json', 'directories.json', 'preset-categories.json') -or $_.Extension -in @('.db', '.db-wal', '.db-shm', '.wav', '.log')
        } | Select-Object -First 1
        if ($userData) { throw "Existing publish output contains user data ($($userData.FullName)). Preserve or move the old directory before publishing; no files were deleted." }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

$outputDirectory = Join-Path $projectDirectory "artifacts/publish-$Runtime"
$hostDirectory = Join-Path $projectDirectory 'artifacts/vst2host-x86'
Clear-PublishDirectory $outputDirectory
Clear-PublishDirectory $hostDirectory
Push-Location $projectDirectory
try {
    # The x86 host needs its own runtime; the x64 application's runtime cannot run it.
    dotnet restore src/SoundPreviewer.Vst2Host/SoundPreviewer.Vst2Host.csproj --configfile NuGet.Config -r win-x86 -p:SelfContained=true -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'x86 host restore failed.' }
    dotnet publish src/SoundPreviewer.Vst2Host/SoundPreviewer.Vst2Host.csproj -c Release -r win-x86 --self-contained true --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -m:1 -o $hostDirectory
    if ($LASTEXITCODE -ne 0) { throw 'x86 host publish failed.' }
    dotnet restore src/SoundPreviewer.App/SoundPreviewer.App.csproj --configfile NuGet.Config -r $Runtime -p:SelfContained=true -p:PublishSingleFile=true -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop restore failed.' }
    dotnet publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release -r $Runtime --self-contained true --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -m:1 -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    foreach ($file in @('SoundPreviewer.exe', 'SoundPreviewer.Worker.exe', 'Renderers/dawdreamer_worker.py', 'Renderers/serum2_state.py', 'Vst2Host-x86/SoundPreviewer.Vst2Host.exe', 'Vst2Host-x86/coreclr.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputDirectory $file))) { throw "Missing published file: $file" }
    }
    [IO.File]::WriteAllText((Join-Path $outputDirectory 'Start-SoundPreviewer.cmd'), "@echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"%~dp0SoundPreviewer.exe`" %*`r`n")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Prepare-DawDreamer.ps1') -Destination $outputDirectory
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'docs/releases.md') -Destination (Join-Path $outputDirectory 'RELEASES.md')
    Write-Host "Published: $outputDirectory"
}
finally { Pop-Location }
