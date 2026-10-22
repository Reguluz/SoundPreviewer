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
if (Get-ChildItem -LiteralPath $published -Recurse -Force | Where-Object {
    $_.Name -in @('Preview', 'local-data', 'settings.json', 'directories.json', 'library-location.json', 'preset-categories.json') -or
    $_.Extension -in @('.db', '.db-wal', '.db-shm', '.wav', '.m4a', '.log')
}) { throw 'The application source contains user data; refusing an installed-storage copy.' }
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
Get-ChildItem -LiteralPath $legacy, "$legacy-audio" -File -Recurse | Where-Object { $_.Extension -in @('.db', '.wav', '.m4a', '.vital') } | ForEach-Object {
    $originals[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$preview = Join-Path $application 'Preview'
$directoryConfiguration = Join-Path $application 'Config/directories.json'
$legacyDirectoryConfiguration = Join-Path $application 'directories.json'
[IO.File]::WriteAllText($legacyDirectoryConfiguration, '{"FormatVersion":1,"LegacyMigrationTest":"preserve-original"}')
$originals[$legacyDirectoryConfiguration] = (Get-FileHash -LiteralPath $legacyDirectoryConfiguration -Algorithm SHA256).Hash
$manualPluginDirectory = Join-Path $diagnostics 'TEST ONLY manual plugin directory'
$results = @()
foreach ($phase in @('migrate', 'reopen')) {
    $previousBackups = @{}
    if ($phase -eq 'reopen') {
        Get-ChildItem -LiteralPath (Join-Path $preview 'Backups') -Filter 'library-before-install-*.db' -File | ForEach-Object {
            $previousBackups[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
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
    if ($null -eq $result.PSObject.Properties['StoragePreparationWindowShown'] -or
        [bool]$result.StoragePreparationWindowShown -ne ($phase -eq 'migrate')) {
        throw "Installed storage $phase did not verify that only a real migration opens the preparation window: $diagnostics"
    }
    if ($phase -eq 'reopen') {
        $currentBackups = @(Get-ChildItem -LiteralPath (Join-Path $preview 'Backups') -Filter 'library-before-install-*.db' -File)
        if ($currentBackups.Count -ne $previousBackups.Count) { throw 'Reopening a prepared library created another migration backup.' }
        foreach ($backup in $currentBackups) {
            if ($previousBackups[$backup.Name] -ne (Get-FileHash -LiteralPath $backup.FullName -Algorithm SHA256).Hash) {
                throw 'Reopening a prepared library changed a retained migration backup.'
            }
        }
    }
    if (-not $result.InstalledStorageChecked -or -not $result.ExistingPreviewsLocal -or -not $result.GeneratedViewChecked -or $result.StoredPreviewRecords -ne 2 -or -not $result.TemplatePersistenceChecked -or
        -not $result.MetadataPersistenceChecked -or -not $result.SourceCountsChecked -or -not $result.RendererSettingsChecked -or
        -not $result.CrossSourceDuplicatesChecked -or -not $result.DirectoryConfigurationChecked -or -not $result.CategoryRulesAvailableChecked -or -not $result.RenderProgressCountersChecked -or
        -not $result.EffectiveConcurrencyDisplayChecked -or $result.Presets -ne 3 -or
        -not $result.QueueSelectionCountsChecked -or -not $result.ProgressLayoutStableChecked -or
        [IO.Path]::GetFullPath($result.LibraryDirectory) -ne [IO.Path]::GetFullPath($preview) -or
        [IO.Path]::GetFullPath($result.PreviewDirectory) -ne [IO.Path]::GetFullPath($preview)) {
        throw "Installed storage $phase did not verify the fixed Preview root: $diagnostics"
    }
    if (-not (Test-Path -LiteralPath $directoryConfiguration -PathType Leaf) -or
        [IO.Path]::GetFullPath($result.DirectoryConfigurationPath) -ne [IO.Path]::GetFullPath($directoryConfiguration)) {
        throw "Installed storage $phase did not persist the directory JSON under the application Config directory."
    }
    $configuration = Get-Content -LiteralPath $directoryConfiguration -Raw | ConvertFrom-Json
    if ($configuration.LegacyMigrationTest -ne 'preserve-original' -or
        -not (Test-Path -LiteralPath (Join-Path $application 'Config/preset-categories.json') -PathType Leaf)) {
        throw 'The application did not consolidate directory settings and personal classification rules into Config.'
    }
    if ($phase -eq 'migrate') {
        $configuration.PluginDirectories = @($configuration.PluginDirectories) + @($manualPluginDirectory)
        $configuration | Add-Member -NotePropertyName TestOnlyUnknownField -NotePropertyValue 'preserve-across-reopen'
        $configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $directoryConfiguration -Encoding utf8
    }
    elseif ($manualPluginDirectory -notin $result.DirectoryPluginDirectories -or $configuration.TestOnlyUnknownField -ne 'preserve-across-reopen') {
        throw 'The reopened application did not load the manually edited directory JSON or preserve its unknown field.'
    }
    foreach ($file in $originals.Keys) {
        if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $originals[$file]) {
            throw "The original legacy fixture was changed: $file"
        }
    }
    $results += @{ Phase = $phase; FixedPreviewDirectory = $preview; Presets = $result.Presets; OriginalFilesPreserved = $true;
        StoragePreparationWindowShown = [bool]$result.StoragePreparationWindowShown; NoRepeatedMigrationBackup = $phase -eq 'reopen' }
    Copy-Item -LiteralPath (Join-Path $preview 'ui-smoke.json') -Destination (Join-Path $diagnostics "$phase-ui-smoke.json")
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $diagnostics 'result.json') -Encoding utf8
$results | ConvertTo-Json
