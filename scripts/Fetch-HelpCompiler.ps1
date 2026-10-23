[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The Microsoft CHM compiler requires Windows.' }
$workspace = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $workspace 'artifacts/packaging-tools/html-help-4.74.8702.1'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
# Fixed Microsoft.VisualCpp.HTMLHelpWorkshop.Msi 4.74.8702.1 payload from
# the Visual Studio 2022 catalog. No system installation or COM registration.
$hash = '77363339b34c627882d3e50540005c9ad4625f352167b3a8c164de169a44ec56'
$archive = Join-Path $directory 'cab1.cab'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://download.visualstudio.microsoft.com/download/pr/25fdf4b7-eb6a-4d83-8b6b-ae142bff1e07/$hash/cab1.cab" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Microsoft HTML Help compiler archive checksum mismatch.' }
& expand.exe '-F:*' $archive $directory | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'CHM compiler extraction failed.' }
foreach ($name in @('hhc.exe', 'hha.dll', 'itcc.dll')) {
    Copy-Item -LiteralPath (Join-Path $directory "FL_$name") -Destination (Join-Path $directory $name) -Force
}
Write-Output (Join-Path $directory 'hhc.exe')
