param([string]$HostPath = (Join-Path $PSScriptRoot '../../artifacts/vst2host-x86/SoundPreviewer.Vst2Host.exe'))
$ErrorActionPreference = 'Stop'
$start = [System.Diagnostics.ProcessStartInfo]::new([System.IO.Path]::GetFullPath($HostPath))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--self-test')
[void]$start.Environment.Remove('DOTNET_ROOT')
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    [void]$process.Start()
    $output = $process.StandardOutput.ReadToEndAsync()
    $errorOutput = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw 'TEST ONLY host self-test timed out.' }
    $text = $output.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw $errorOutput.GetAwaiter().GetResult() }
    $result = $text | ConvertFrom-Json
    if ($result.Architecture -ne 'x86' -or -not $result.BufferInterop -or -not $result.FxpParse -or -not $result.BadInput -or -not $result.FrameBoundaries -or -not $result.UnchangedChunkRejected -or -not $result.ProgramSlotBounds -or $result.PluginsLoaded -ne 0) { throw 'TEST ONLY host self-test failed.' }
    $result | Add-Member -NotePropertyName TestOnly -NotePropertyValue $true
    $result | ConvertTo-Json
} finally { $process.Dispose() }
