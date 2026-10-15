param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/vst2host-x86'), [switch]$Offline)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SoundPreviewer.Vst2Host.csproj'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
New-Item -ItemType Directory -Path (Join-Path $workspace '.build/vst2-nuget') -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $workspace '.build/dotnet'
$env:APPDATA = Join-Path $workspace '.build/AppData'
$env:NUGET_PACKAGES = Join-Path $workspace '.build/nuget-packages'
$restoreArguments = @('restore', $project, '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'))
if ($Offline) { $restoreArguments += @('--source', (Join-Path $workspace '.build/vst2-nuget')) }
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Optional VST2 host restore failed.' }
& dotnet publish $project --no-restore --configuration Release --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Optional VST2 host build failed.' }
