param(
    [string]$DataDirectory = (Join-Path $PSScriptRoot 'local-data'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) {
        $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build\dotnet'
        $env:APPDATA = Join-Path $PSScriptRoot '.build\AppData'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        dotnet restore SoundPreviewer.sln --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
        dotnet build SoundPreviewer.sln -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $application = Join-Path $PSScriptRoot 'src\SoundPreviewer.App\bin\Release\net8.0-windows\SoundPreviewer.exe'
    if (-not (Test-Path -LiteralPath $application)) { throw 'Application build is missing.' }
    Start-Process -FilePath $application -ArgumentList @('--data-directory', ('"{0}"' -f [IO.Path]::GetFullPath($DataDirectory)))
}
finally { Pop-Location }
