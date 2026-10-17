param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.build/dotnet'
$env:NUGET_PACKAGES = Join-Path $projectDirectory '.build/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$outputDirectory = Join-Path $projectDirectory "artifacts/publish-$Runtime"
Push-Location $projectDirectory
try {
    dotnet publish src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release -r $Runtime --self-contained true --disable-build-servers -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:NuGetAudit=false -m:1 -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($Runtime -eq 'win-x64') {
        [IO.File]::WriteAllText((Join-Path $outputDirectory 'Start-SoundPreviewer.cmd'), "@echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"%~dp0SoundPreviewer.exe`" %*`r`n")
    }
    Write-Host "Published: $outputDirectory"
}
finally { Pop-Location }
