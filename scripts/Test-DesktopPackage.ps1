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
$resources = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ApplicationPath))
$uvName = if ($Runtime -eq 'win-x64') { 'uv.exe' } else { 'uv' }
$setupScript = if ($Runtime -eq 'win-x64') { 'Prepare-DawDreamer.ps1' } else { 'Prepare-DawDreamer.sh' }
foreach ($file in @('Renderers/dawdreamer_worker.py', 'Renderers/serum2_state.py', 'Renderers/requirements.txt',
    'Renderers/requirements-serum2.txt', "Renderers/uv/$uvName", 'Renderers/uv/NOTICE.md',
    'Renderers/uv/LICENSE-MIT.txt', 'Renderers/uv/LICENSE-APACHE.txt', $setupScript, 'Config/preset-categories.default.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $resources $file) -PathType Leaf)) { throw "Published application resource is missing: $file" }
}
if ($Runtime -eq 'win-x64') {
    foreach ($file in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'NOTICE.md', 'manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $resources "Renderers/runtime-win-x64/$file") -PathType Leaf)) {
            throw "Published private renderer C++ runtime resource is missing: $file"
        }
    }
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
    if (-not $result.PreviewShortcutChecked -or $result.PreviewShortcutHintViewports -ne 2) {
        throw "Published application did not verify the Space shortcut and visible hint; see $dataDirectory"
    }
    if (-not $result.ClipboardPathChecked -or -not $result.LibrarySpaceChecked -or
        -not $result.SingleRowFiltersChecked -or -not $result.PlaybackProgressDockChecked) {
        throw "Published application did not verify copy-path and compact library layout; see $dataDirectory"
    }
    if (-not $result.Loaded -or -not $result.FormatsChecked -or -not $result.StopChecked -or -not $result.PluginScanDefaultsChecked -or -not $result.TemplatePersistenceChecked -or -not $result.PresetFileTransferChecked -or -not $result.SourceRevealCommandChecked -or -not $result.SourceCountsChecked -or -not $result.RendererSettingsChecked -or -not $result.CrossSourceDuplicatesChecked -or -not $result.DirectoryConfigurationChecked -or -not $result.CategoryRulesAvailableChecked -or -not $result.RenderProgressCountersChecked -or -not $result.EffectiveConcurrencyDisplayChecked -or -not $result.QueueSelectionCountsChecked -or -not $result.ProgressLayoutStableChecked) {
        throw "Published application smoke test did not complete; see $dataDirectory"
    }
    # Launch the shipped worker too: bundling the desktop alone must not hide a
    # missing worker dependency. Invalid input exercises its real JSON protocol
    # without opening any synthesizer, fixture audio or user library.
    $request = Join-Path $dataDirectory 'worker-invalid-request.json'
    $response = Join-Path $dataDirectory 'worker-invalid-result.json'
    [IO.File]::WriteAllText($request, '{"Template":{"SampleRate":1}}')
    $workerName = if ($Runtime -eq 'win-x64') { 'SoundPreviewer.Worker.exe' } else { 'SoundPreviewer.Worker' }
    $workerStart = [Diagnostics.ProcessStartInfo]::new((Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ApplicationPath))) $workerName))
    $workerStart.UseShellExecute = $false
    $workerStart.CreateNoWindow = $true
    $workerStart.RedirectStandardError = $true
    $workerStart.RedirectStandardOutput = $true
    foreach ($argument in @('--request', $request, '--result', $response)) { $workerStart.ArgumentList.Add($argument) }
    $worker = [Diagnostics.Process]::new()
    $worker.StartInfo = $workerStart
    try {
        [void]$worker.Start()
        $workerError = $worker.StandardError.ReadToEndAsync()
        $workerOutput = $worker.StandardOutput.ReadToEndAsync()
        if (-not $worker.WaitForExit(20000)) { $worker.Kill($true); throw 'Published worker validation timed out.' }
        $workerErrorText = $workerError.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $dataDirectory 'worker-stderr.txt'), $workerErrorText)
        [IO.File]::WriteAllText((Join-Path $dataDirectory 'worker-stdout.txt'), $workerOutput.GetAwaiter().GetResult())
        if ($worker.ExitCode -ne 1 -or -not (Test-Path -LiteralPath $response -PathType Leaf)) {
            throw "Published worker did not return a protocol validation result (exit $($worker.ExitCode)): $workerErrorText"
        }
        $workerResult = Get-Content -LiteralPath $response -Raw | ConvertFrom-Json
        if ($workerResult.Success -or [string]::IsNullOrWhiteSpace($workerResult.Message)) { throw 'Published worker did not reject the invalid sample-rate request.' }
    }
    finally { $worker.Dispose() }
    $result | Add-Member -NotePropertyName PublishedWorkerProtocolChecked -NotePropertyValue $true
    $result | ConvertTo-Json
}
finally { $process.Dispose() }
