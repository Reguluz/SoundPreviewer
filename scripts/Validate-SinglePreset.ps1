#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [string]$WorkerPath = (Join-Path $PSScriptRoot '..\src\SoundPreviewer.Worker\bin\Release\net8.0-windows\SoundPreviewer.Worker.exe'),
    [string]$CorePath,
    [switch]$Play,
    [ValidateRange(0.0, 1.0)]
    [double]$Volume = 0.2
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$artifactPrefix = $artifacts + [IO.Path]::DirectorySeparatorChar
$RequestPath = [IO.Path]::GetFullPath($RequestPath)
$WorkerPath = [IO.Path]::GetFullPath($WorkerPath)
if ([string]::IsNullOrWhiteSpace($CorePath)) { $CorePath = Join-Path ([IO.Path]::GetDirectoryName($WorkerPath)) 'SoundPreviewer.Core.dll' }
$CorePath = [IO.Path]::GetFullPath($CorePath)
foreach ($path in @($RequestPath, $WorkerPath, $CorePath)) {
    if (-not [IO.File]::Exists($path)) { throw "Required file does not exist: $path" }
}
if ((Get-Item -LiteralPath $RequestPath).Length -gt 1MB) { throw 'Request exceeds the 1 MiB protocol limit.' }
Add-Type -Path $CorePath
$options = [SoundPreviewer.Core.Models.LibraryJson]::Options
$request = [System.Text.Json.JsonSerializer]::Deserialize(
    [IO.File]::ReadAllText($RequestPath), [SoundPreviewer.Core.Models.RenderRequest], $options)
if ($null -eq $request -or $null -eq $request.Preset -or $null -eq $request.Plugin -or $null -eq $request.Template) {
    throw 'Request must contain exactly one Preset, Plugin and Template.'
}
if ($request.Preset.BankSlot -ne $null -or -not [string]::IsNullOrWhiteSpace($request.Preset.InternalPath)) {
    throw 'This tool validates one standalone preset, not a bank slot or package entry.'
}
if ($request.Preset.Synth -ne $request.Plugin.Synth -or $request.Preset.Synth -eq [SoundPreviewer.Core.Models.SynthKind]::Unknown) {
    throw 'Preset and plugin must identify the same supported synth.'
}
$request.Template.NoteOrder = [SoundPreviewer.Core.Models.PreviewNoteOrder]::MiddleHighLow
$request.Template.Validate()
$sourcePath = [IO.Path]::GetFullPath($request.Preset.SourcePath)
$pluginPath = [IO.Path]::GetFullPath($request.Plugin.PluginPath)
foreach ($path in @($sourcePath, $pluginPath, $request.Plugin.ExecutablePath)) {
    if (-not [IO.File]::Exists($path)) { throw "Preset, plugin DLL or renderer does not exist: $path" }
}
$outputPath = [IO.Path]::GetFullPath($request.OutputPath)
if (-not $outputPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetExtension($outputPath).Equals('.wav', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath must be a new WAV inside this repository''s artifacts directory.'
}
$outputDirectory = [IO.Path]::GetDirectoryName($outputPath)
$ancestor = $outputDirectory
while ($ancestor.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and
        ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Artifact output cannot traverse a directory link: $ancestor"
    }
    if ($ancestor.Equals($artifacts, [StringComparison]::OrdinalIgnoreCase)) { break }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
$stem = [IO.Path]::GetFileNameWithoutExtension($outputPath)
$resultPath = Join-Path $outputDirectory ($stem + '-result.json')
$summaryPath = Join-Path $outputDirectory ($stem + '-summary.json')
foreach ($path in @($outputPath, $resultPath, $summaryPath)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite a previous validation artifact: $path" }
    if ($path.Equals($RequestPath, [StringComparison]::OrdinalIgnoreCase) -or
        $path.Equals($sourcePath, [StringComparison]::OrdinalIgnoreCase) -or
        $path.Equals($pluginPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'An output path overlaps an input file.' }
}

function Get-InputFingerprint([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [ordered]@{
        Path = $item.FullName
        Bytes = $item.Length
        LastWriteUtc = $item.LastWriteTimeUtc.ToString('O')
        Sha256 = [SoundPreviewer.Core.Models.Fingerprints]::HashFile($item.FullName)
    }
}

function Write-NewUtf8([string]$Path, [string]$Text) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $writer.Write($Text) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

$sourceBefore = Get-InputFingerprint $sourcePath
$pluginBefore = Get-InputFingerprint $pluginPath
if (-not [string]::IsNullOrWhiteSpace($request.Preset.ContentHash) -and
    -not $sourceBefore.Sha256.Equals($request.Preset.ContentHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The source SHA256 differs from the scanned request. No renderer was started.'
}
$request.Preset.ContentHash = $sourceBefore.Sha256
if ([string]::IsNullOrWhiteSpace($request.Preset.Id)) {
    $request.Preset.Id = [SoundPreviewer.Core.Models.Fingerprints]::PresetId($request.Preset.Synth, $sourcePath)
}
$request.Plugin.PluginHash = $pluginBefore.Sha256
$request.Plugin.ExecutableHash = [SoundPreviewer.Core.Models.Fingerprints]::HashFile($request.Plugin.ExecutablePath)
$request.CacheKey = [SoundPreviewer.Core.Models.Fingerprints]::CacheKey($request.Preset, $request.Plugin, $request.Template)
$request.OutputPath = $outputPath
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$cancellation = [Threading.CancellationTokenSource]::new()
$captureRounds = if ($request.Template.QuickPreview) {
    if ($request.Plugin.Backend -eq [SoundPreviewer.Core.Models.RenderBackend]::VitalCli) { 3 } else { 4 }
} else { 1 }
$cancellation.CancelAfter([TimeSpan]::FromSeconds([Math]::Clamp($request.Plugin.TimeoutSeconds, 5, 3600) * $captureRounds + 30))
$result = $null
$failure = $null
$loadEvidence = switch ($request.Plugin.Backend.ToString()) {
    'DawDreamer' {
        'The DawDreamer bridge requires every load_preset call to return true; this is not an independent audible preset-identity confirmation.'
    }
    'ExternalWorker' {
        if ([IO.Path]::GetFileName($request.Plugin.ExecutablePath).Equals('SoundPreviewer.Vst2Host.exe', [StringComparison]::OrdinalIgnoreCase)) {
            'The built-in VST.NET host uses SetChunk return values/program-state changes or FXP parameter application as loading evidence; this does not independently prove audible preset identity.'
        } else {
            'The configured external renderer reports successful loading and rendering through its result contract; its loading evidence and audible preset identity require adapter-specific review.'
        }
    }
    'VitalCli' {
        'Vital native CLI does not independently report successful target-preset loading; generated audio still requires manual preset-identity confirmation.'
    }
    default { 'No adapter-specific preset-loading evidence is assumed from this backend.' }
}
$summary = [ordered]@{
    Success = $false
    RequestPath = $RequestPath
    WorkerPath = $WorkerPath
    PresetName = $request.Preset.Name
    Synth = $request.Preset.Synth.ToString()
    PluginVersion = $request.Plugin.Version
    CacheKey = $request.CacheKey
    QuickPreview = $request.Template.QuickPreview
    NoteOrder = $request.Template.NoteOrder.ToString()
    RenderedPitches = @($request.Template.Pitches)
    MaximumCaptureRounds = $captureRounds
    EffectiveRequest = ([System.Text.Json.JsonSerializer]::Serialize($request, [SoundPreviewer.Core.Models.RenderRequest], $options) | ConvertFrom-Json -Depth 12)
    OutputPath = $outputPath
    ResultPath = $resultPath
    SourceBefore = $sourceBefore
    PluginBefore = $pluginBefore
    Notes = @()
    PlaybackRequested = [bool]$Play
    PlaybackCompleted = $false
    ManualPresetConfirmationRequired = $true
    LoadEvidence = $loadEvidence
}
try {
    Write-Host ("Rendering exactly one preset: {0}" -f $request.Preset.Name)
    $client = [SoundPreviewer.Core.Audio.WorkerProcessClient]::new()
    $result = $client.RenderAsync($request, $WorkerPath, $cancellation.Token).GetAwaiter().GetResult()
    if (-not $result.Success) { throw ("Worker failed: {0}" -f $result.Message) }
    $audio = [SoundPreviewer.Core.Audio.WaveFile]::Read($result.OutputPath, $cancellation.Token)
    $metrics = [SoundPreviewer.Core.Audio.WaveFile]::Analyze($audio, $cancellation.Token)
    if ($metrics.HasNonFinite -or $metrics.SampleRate -ne $request.Template.SampleRate -or $audio.Frames -le 0) {
        throw 'Committed WAV is nonfinite, empty or has an unexpected sample rate.'
    }
    [SoundPreviewer.Core.Audio.WorkerProcessClient]::ValidateNotes($result, $request.Template)
    $previousEnd = 0
    $noteReports = @()
    $silentNotes = @()
    $frameTolerance = 1.5 / $audio.SampleRate
    $nativeBlockTolerance = 64.0 / 44100 + $frameTolerance
    $vitalNative = $request.Plugin.Backend -eq [SoundPreviewer.Core.Models.RenderBackend]::VitalCli
    if ($request.Template.QuickPreview -and -not $result.AdapterVersion.Contains([SoundPreviewer.Core.Models.Fingerprints]::QuickPreviewPolicy, [StringComparison]::Ordinal)) {
        throw 'A new quick preview must declare the compact-preview policy version.'
    }
    foreach ($note in $result.Notes) {
        $first = [int][Math]::Round($note.StartSeconds * $audio.SampleRate)
        $frames = [int][Math]::Round($note.DurationSeconds * $audio.SampleRate)
        if ($first -lt 0 -or [Math]::Abs($first - $previousEnd) -gt 1 -or $frames -le 0 -or $first + $frames -gt $audio.Frames) {
            throw 'The three note segments must cover contiguous nonempty WAV frame ranges.'
        }
        if ($request.Template.QuickPreview) {
            $allowedHolds = if ($vitalNative) { @(1.0, 2.0, 4.0) } else { @(0.5, 1.0, 2.0, 4.0) }
            $captureHold = $allowedHolds | Where-Object { [Math]::Abs($_ - $note.ActualHoldSeconds) -le $nativeBlockTolerance } | Select-Object -First 1
            if ($null -eq $captureHold) { throw 'Actual quick-preview hold is not a bounded capture stage.' }
            $expectedTail = if ($vitalNative) { $captureHold * 0.3 } else { 0.08 }
            $expectedVelocity = if ($vitalNative) { 89 } else { $request.Template.Velocity }
            if ($note.DurationSeconds -gt 0.5 + $frameTolerance -or
                $note.ActualVelocity -ne $expectedVelocity -or
                [Math]::Abs($note.ActualTailSeconds - $expectedTail) -gt $nativeBlockTolerance -or
                [Math]::Abs($note.DurationSeconds + $note.TrimmedLeadingSeconds + $note.TrimmedTrailingSeconds -
                    $note.ActualHoldSeconds - $note.ActualTailSeconds) -gt $frameTolerance) {
                throw 'Compact duration, real capture velocity/tail or trimmed-frame provenance is invalid.'
            }
        } else {
            if ([Math]::Abs($note.DurationSeconds - $request.Template.HoldSeconds - $request.Template.TailSeconds) -gt 0.01 -or
                $note.ActualVelocity -ne $request.Template.Velocity -or
                [Math]::Abs($note.ActualHoldSeconds - $request.Template.HoldSeconds) -gt 0.01 -or
                [Math]::Abs($note.ActualTailSeconds - $request.Template.TailSeconds) -gt 0.01 -or
                $note.TrimmedLeadingSeconds -ne 0 -or $note.TrimmedTrailingSeconds -ne 0) {
                throw 'Actual fixed-mode note velocity or duration differs from the requested template.'
            }
        }
        $samples = [float[]]::new($frames * $audio.Channels)
        [Array]::Copy($audio.Samples, $first * $audio.Channels, $samples, 0, $samples.Length)
        $segment = [SoundPreviewer.Core.Audio.WaveData]::new()
        $segment.SampleRate = $audio.SampleRate
        $segment.Channels = $audio.Channels
        $segment.Samples = $samples
        $segmentMetrics = [SoundPreviewer.Core.Audio.WaveFile]::Analyze($segment, $cancellation.Token)
        $bytes = [byte[]]::new($samples.Length * 4)
        [Buffer]::BlockCopy($samples, 0, $bytes, 0, $bytes.Length)
        $noteReports += [ordered]@{
            MidiNote = $note.MidiNote
            FirstFrame = $first
            Frames = $frames
            DurationSeconds = $frames / [double]$audio.SampleRate
            ActualHoldSeconds = $note.ActualHoldSeconds
            ActualTailSeconds = $note.ActualTailSeconds
            ActualVelocity = $note.ActualVelocity
            ActualNormalizedVelocity = $note.ActualNormalizedVelocity
            TrimmedLeadingSeconds = $note.TrimmedLeadingSeconds
            TrimmedTrailingSeconds = $note.TrimmedTrailingSeconds
            Rms = $segmentMetrics.Rms
            Peak = $segmentMetrics.Peak
            IsSilent = $segmentMetrics.IsSilent
            HasClipping = $segmentMetrics.HasClipping
            DecodedFloat32Sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        }
        $summary.Notes = $noteReports
        if ($segmentMetrics.HasNonFinite -or $segmentMetrics.IsSilent -or $segmentMetrics.Rms -lt 0.00001) {
            $silentNotes += $note.MidiNote
        }
        $previousEnd = $first + $frames
    }
    if ([Math]::Abs($previousEnd - $audio.Frames) -gt 1) { throw 'The note map does not cover the complete WAV.' }
    if ($silentNotes.Count -gt 0) { throw ("Silent or invalid MIDI segments: {0}; this is not an accepted three-register preview." -f ($silentNotes -join ', ')) }
    $summary.Audio = [ordered]@{
        Frames = $audio.Frames
        Channels = $audio.Channels
        SampleRate = $audio.SampleRate
        DurationSeconds = $audio.DurationSeconds
        Rms = $metrics.Rms
        Peak = $metrics.Peak
        HasClipping = $metrics.HasClipping
        WavSha256 = [SoundPreviewer.Core.Models.Fingerprints]::HashFile($outputPath, $cancellation.Token)
    }
    $summary.AdapterVersion = $result.AdapterVersion
    $summary.NeedsReview = $result.NeedsReview
    if ($Play) {
        $cancellation.CancelAfter([TimeSpan]::FromSeconds($audio.DurationSeconds + 30))
        $playWatch = [Diagnostics.Stopwatch]::StartNew()
        $player = [SoundPreviewer.Core.Audio.WavePlayer]::new()
        try {
            $player.Volume = $Volume
            $player.Load($audio)
            $player.Play(0)
            while ($player.IsPlaying) {
                $cancellation.Token.ThrowIfCancellationRequested()
                if ($playWatch.Elapsed.TotalSeconds -gt $audio.DurationSeconds + 10) { throw 'Playback did not finish within its bound.' }
                Start-Sleep -Milliseconds 100
            }
            $summary.PlaybackCompleted = $player.PositionSeconds -ge $audio.DurationSeconds - 0.02
            if (-not $summary.PlaybackCompleted) { throw 'Native playback did not reach the end of the real rendered WAV.' }
        } finally { $player.Dispose(); $playWatch.Stop() }
    }
    $summary.Success = $true
} catch {
    $failure = $_
    $summary.Error = $_.Exception.ToString()
} finally {
    $cancellation.Cancel()
    $cancellation.Dispose()
    $watch.Stop()
    $summary.ElapsedSeconds = $watch.Elapsed.TotalSeconds
    $summary.SourceAfter = Get-InputFingerprint $sourcePath
    $summary.PluginAfter = Get-InputFingerprint $pluginPath
    $summary.SourceUnchanged = $sourceBefore.Sha256 -eq $summary.SourceAfter.Sha256 -and
        $sourceBefore.Bytes -eq $summary.SourceAfter.Bytes -and $sourceBefore.LastWriteUtc -eq $summary.SourceAfter.LastWriteUtc
    $summary.PluginUnchanged = $pluginBefore.Sha256 -eq $summary.PluginAfter.Sha256 -and
        $pluginBefore.Bytes -eq $summary.PluginAfter.Bytes -and $pluginBefore.LastWriteUtc -eq $summary.PluginAfter.LastWriteUtc
    if (-not $summary.SourceUnchanged -or -not $summary.PluginUnchanged) {
        $summary.Success = $false
        $summary.Error = 'Source preset or plugin DLL changed during validation.'
    }
    if ($null -ne $result) {
        Write-NewUtf8 $resultPath ([System.Text.Json.JsonSerializer]::Serialize($result, [SoundPreviewer.Core.Models.RenderResult], $options))
    }
    Write-NewUtf8 $summaryPath ($summary | ConvertTo-Json -Depth 12)
}
$summary | ConvertTo-Json -Depth 12
if ($null -ne $failure) { throw $failure }
if (-not $summary.Success) { throw $summary.Error }
