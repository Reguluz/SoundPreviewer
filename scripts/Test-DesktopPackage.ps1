[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'osx-arm64', 'osx-x64')][string]$Runtime,
    [string]$ApplicationPath
)
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $ApplicationPath) {
    $executable = if ($Runtime -eq 'win-x64') { 'SoundPreviewer.exe' } else { 'SoundPreviewer' }
    $ApplicationPath = Join-Path $projectDirectory "artifacts/publish-$Runtime/$executable"
}
$dataDirectory = Join-Path $projectDirectory "artifacts/ui-smoke-$Runtime-$([Guid]::NewGuid().ToString('N'))"
$start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($ApplicationPath))
$start.UseShellExecute = $false
$start.ArgumentList.Add('--smoke-test')
$start.ArgumentList.Add('--data-directory')
$start.ArgumentList.Add($dataDirectory)
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    [void]$process.Start()
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Published application smoke test timed out.' }
    if ($process.ExitCode -ne 0) { throw "Published application smoke test failed; see $dataDirectory" }
    $result = Get-Content -LiteralPath (Join-Path $dataDirectory 'ui-smoke.json') -Raw | ConvertFrom-Json
    if (-not $result.Loaded -or -not $result.FormatsChecked -or -not $result.StopChecked -or -not $result.PluginScanDefaultsChecked -or -not $result.TemplatePersistenceChecked) {
        throw "Published application smoke test did not complete; see $dataDirectory"
    }
    $result | ConvertTo-Json
}
finally { $process.Dispose() }
