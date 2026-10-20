[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'osx-arm64', 'osx-x64')][string]$Runtime,
    [string]$ApplicationDirectory
)
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$published = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $projectDirectory "artifacts/publish-$Runtime" }
$executable = if ($Runtime -eq 'win-x64') { 'SoundPreviewer.exe' } else { 'SoundPreviewer' }
if (-not (Test-Path -LiteralPath (Join-Path $published $executable))) { throw 'Publish the application first.' }
# A separate application copy exercises the normal installed-directory contract.
# It cannot discover or write the developer's real library because the first launch
# supplies a private legacy fixture and the second must reopen this copy's Preview.
$diagnostics = Join-Path $projectDirectory "artifacts/installed-storage-smoke-$Runtime-$([Guid]::NewGuid().ToString('N'))"
$application = Join-Path $diagnostics 'application'
$legacy = Join-Path $diagnostics 'legacy-library'
New-Item -ItemType Directory -Path $application | Out-Null
Get-ChildItem -LiteralPath $published -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $application -Recurse }
$tests = Join-Path $projectDirectory 'tests/SoundPreviewer.Tests/bin/Release/net8.0/SoundPreviewer.Tests.dll'
& dotnet $tests --create-installed-storage-fixture $legacy
if ($LASTEXITCODE -ne 0) { throw 'Could not create the private legacy fixture.' }
$originals = @{}
Get-ChildItem -LiteralPath $legacy, "$legacy-audio" -File -Recurse | Where-Object { $_.Extension -in @('.db', '.wav', '.vital') } | ForEach-Object {
    $originals[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$preview = Join-Path $application 'Preview'
$results = @()
foreach ($phase in @('migrate', 'reopen')) {
    foreach ($name in @('ui-smoke.json', 'ui-error.txt', 'ui-startup-error.txt', 'ui-exit.json')) {
        $previous = Join-Path $preview $name
        if (Test-Path -LiteralPath $previous -PathType Leaf) { Remove-Item -LiteralPath $previous }
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $application $executable))
    $start.UseShellExecute = $false
    $start.ArgumentList.Add('--test-installed-storage')
    if ($phase -eq 'migrate') {
        $start.ArgumentList.Add('--data-directory')
        $start.ArgumentList.Add($legacy)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw "Installed storage $phase timed out: $diagnostics" }
        if ($process.ExitCode -ne 0) { throw "Installed storage $phase failed: $diagnostics" }
    }
    finally { $process.Dispose() }
    $result = Get-Content -LiteralPath (Join-Path $preview 'ui-smoke.json') -Raw | ConvertFrom-Json
    if (-not $result.InstalledStorageChecked -or -not $result.ExistingPreviewsLocal -or -not $result.GeneratedViewChecked -or $result.StoredPreviewRecords -ne 2 -or -not $result.TemplatePersistenceChecked -or
        -not $result.MetadataPersistenceChecked -or -not $result.SourceCountsChecked -or -not $result.RendererSettingsChecked -or $result.Presets -ne 3 -or
        [IO.Path]::GetFullPath($result.LibraryDirectory) -ne [IO.Path]::GetFullPath($preview) -or
        [IO.Path]::GetFullPath($result.PreviewDirectory) -ne [IO.Path]::GetFullPath($preview)) {
        throw "Installed storage $phase did not verify the fixed Preview root: $diagnostics"
    }
    foreach ($file in $originals.Keys) {
        if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $originals[$file]) {
            throw "The original legacy fixture was changed: $file"
        }
    }
    $results += @{ Phase = $phase; FixedPreviewDirectory = $preview; Presets = $result.Presets; OriginalFilesPreserved = $true }
    Copy-Item -LiteralPath (Join-Path $preview 'ui-smoke.json') -Destination (Join-Path $diagnostics "$phase-ui-smoke.json")
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $diagnostics 'result.json') -Encoding utf8
$results | ConvertTo-Json
