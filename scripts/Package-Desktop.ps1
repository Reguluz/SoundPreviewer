[CmdletBinding()]
param([string]$InnoSetupCompiler)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
[xml]$versionFile = Get-Content -LiteralPath (Join-Path $projectDirectory 'Version.props')
$version = [string]$versionFile.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version.props must contain a semantic version.' }
$numericVersion = ($version -split '-', 2)[0]
$publishDirectory = Join-Path $projectDirectory 'artifacts/publish-win-x64'
$releaseDirectory = Join-Path $projectDirectory 'artifacts/releases'
foreach ($file in @('SoundPreviewer.exe', 'SoundPreviewer.Worker.exe', 'Vst2Host-x86/coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $file))) { throw "Publish first; missing $file" }
}
if (Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force | Where-Object { $_.Name -in @('Preview', 'local-data', 'settings.json', 'library-location.json', 'directories.json', 'preset-categories.json') -or $_.Extension -in @('.db', '.db-wal', '.db-shm', '.wav', '.log') }) {
    throw 'Publish output contains local user data; publish into a clean directory again.'
}
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
if (-not $InnoSetupCompiler) {
    $installedCompiler = Get-ChildItem -Path "${env:ProgramFiles(x86)}/Inno Setup */ISCC.exe", "$env:ProgramFiles/Inno Setup */ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -Last 1
    $InnoSetupCompiler = if ($installedCompiler) { $installedCompiler.FullName } else { (Get-Command ISCC.exe -ErrorAction Stop).Source }
}
& $InnoSetupCompiler /Q "/DAppVersion=$version" "/DAppNumericVersion=$numericVersion" "/DPublishDirectory=$publishDirectory" "/DReleaseDirectory=$releaseDirectory" (Join-Path $PSScriptRoot 'packaging/SoundPreviewer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$zip = Join-Path $releaseDirectory "SoundPreviewer-$version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$files = @((Join-Path $releaseDirectory "SoundPreviewer-$version-win-x64-setup.exe"), $zip)
$checksums = foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing package: $file" }
    "{0}  {1}" -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
}
[IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS-win-x64.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Host "Packages: $releaseDirectory"
