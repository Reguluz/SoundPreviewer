#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RequestPath,
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [string]$WorkerPath = (Join-Path $PSScriptRoot '..\src\SoundPreviewer.Worker\bin\Release\net8.0\SoundPreviewer.Worker.exe'),
    [string]$CorePath,
    [string]$RendererPath
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$ArtifactDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ArtifactDirectory))
if (-not $ArtifactDirectory.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactDirectory must be a new subdirectory inside this repository artifacts directory.'
}
if (Test-Path -LiteralPath $ArtifactDirectory) { throw 'ArtifactDirectory already exists; previous artifacts will not be overwritten.' }
$ancestor = $ArtifactDirectory
while ($ancestor.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Artifact output cannot traverse a directory link: $ancestor"
    }
    if ($ancestor.Equals($artifacts, [StringComparison]::OrdinalIgnoreCase)) { break }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
$RequestPath = [IO.Path]::GetFullPath($RequestPath)
$WorkerPath = [IO.Path]::GetFullPath($WorkerPath)
if ([string]::IsNullOrWhiteSpace($CorePath)) { $CorePath = Join-Path ([IO.Path]::GetDirectoryName($WorkerPath)) 'SoundPreviewer.Core.dll' }
$CorePath = [IO.Path]::GetFullPath($CorePath)
foreach ($path in @($RequestPath, $WorkerPath, $CorePath)) {
    if (-not [IO.File]::Exists($path)) { throw "Required file does not exist: $path" }
}
if ((Get-Item -LiteralPath $RequestPath).Length -gt 1MB) { throw 'Request exceeds 1 MiB.' }
Add-Type -Path $CorePath
$options = [SoundPreviewer.Core.Models.LibraryJson]::Options
$request = [Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($RequestPath), [SoundPreviewer.Core.Models.RenderRequest], $options)
if ($null -eq $request -or $null -eq $request.Preset -or $null -eq $request.Plugin -or $null -eq $request.Template) { throw 'Request must contain Preset, Plugin and Template.' }
if (-not [string]::IsNullOrWhiteSpace($RendererPath)) { $request.Plugin.ExecutablePath = [IO.Path]::GetFullPath($RendererPath) }
if ($request.IsInitializationBaseline -or $request.Preset.BankSlot -ne $null -or $request.Preset.InternalPath -or
    $request.Preset.Synth -ne $request.Plugin.Synth -or
    -not [IO.Path]::GetExtension($request.Preset.SourcePath).Equals('.fxp', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an existing verified normal standalone FXP request with matching preset/plugin synth identities.'
}
if (-not [SoundPreviewer.Core.Services.InitBaselineService]::Supports($request.Plugin)) { throw 'This backend has no fresh-instance/no-preset baseline implementation.' }
$request.Template.NoteOrder = [SoundPreviewer.Core.Models.PreviewNoteOrder]::MiddleHighLow
$request.Template.Validate()
$request.Plugin.InitBaselinePath = ''
$inputs = [ordered]@{
    Request = $RequestPath
    Source = [IO.Path]::GetFullPath($request.Preset.SourcePath)
    Plugin = [IO.Path]::GetFullPath($request.Plugin.PluginPath)
    Renderer = [IO.Path]::GetFullPath($request.Plugin.ExecutablePath)
    Worker = $WorkerPath
    Core = $CorePath
    ValidationScript = $PSCommandPath
}
foreach ($path in $inputs.Values) { if (-not [IO.File]::Exists($path)) { throw "Input file does not exist: $path" } }
$rendererDirectory = [IO.Path]::GetDirectoryName($inputs.Renderer)
$workerDirectory = [IO.Path]::GetDirectoryName($WorkerPath)
if ($request.Plugin.Backend -eq [SoundPreviewer.Core.Models.RenderBackend]::DawDreamer) {
    $inputs.RendererScript = Join-Path $workerDirectory 'Renderers\dawdreamer_worker.py'
    if (-not [IO.File]::Exists($inputs.RendererScript)) { throw 'The worker is missing its bundled DawDreamer script.' }
} else {
    foreach ($name in @('SoundPreviewer.Vst2Host.dll', 'Jacobi.Vst.Core.dll', 'Jacobi.Vst.Host.Interop.dll', 'Ijwhost.dll', 'SoundPreviewer.Vst2Host.runtimeconfig.json', 'SoundPreviewer.Vst2Host.deps.json')) {
        $inputs[$name] = Join-Path $rendererDirectory $name
    }
}
$managedWorker = Join-Path $workerDirectory 'SoundPreviewer.Worker.dll'
if ([IO.File]::Exists($managedWorker)) { $inputs.ManagedWorker = $managedWorker }
if ([IO.File]::Exists($request.OutputPath)) { $inputs.OriginalWav = [IO.Path]::GetFullPath($request.OutputPath) }

function Get-Snapshot {
    $snapshot = [ordered]@{}
    foreach ($key in $inputs.Keys) {
        $path = [IO.Path]::GetFullPath($inputs[$key])
        if (-not [IO.File]::Exists($path)) { $snapshot[$key] = [ordered]@{ Path = $path; Exists = $false }; continue }
        $item = Get-Item -LiteralPath $path
        $snapshot[$key] = [ordered]@{ Path = $path; Exists = $true; Bytes = $item.Length;
            LastWriteUtc = $item.LastWriteTimeUtc.ToString('O'); Sha256 = [SoundPreviewer.Core.Models.Fingerprints]::HashFile($path) }
    }
    return $snapshot
}
function Convert-TypedJson($Value, [Type]$Type) {
    [Text.Json.JsonSerializer]::Serialize($Value, $Type, $options) | ConvertFrom-Json -Depth 24
}
function Write-NewJson([string]$Name, $Value) {
    $path = Join-Path $ArtifactDirectory $Name
    $stream = [IO.FileStream]::new($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $writer.Write(($Value | ConvertTo-Json -Depth 24)) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}
function Get-AudioReport([string]$Path) {
    $audio = [SoundPreviewer.Core.Audio.WaveFile]::Read($Path, $cancellation.Token)
    $metrics = [SoundPreviewer.Core.Audio.WaveFile]::Analyze($audio, $cancellation.Token)
    if ($metrics.HasNonFinite -or $metrics.IsSilent) { throw 'Validation audio is nonfinite or silent.' }
    [ordered]@{ Path = $Path; Sha256 = [SoundPreviewer.Core.Models.Fingerprints]::HashFile($Path, $cancellation.Token);
        Frames = $audio.Frames; Metrics = (Convert-TypedJson $metrics ([SoundPreviewer.Core.Models.AudioMetrics])) }
}

$before = Get-Snapshot
if ($request.Preset.ContentHash -and -not $request.Preset.ContentHash.Equals($before.Source.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The source differs from its scanned SHA256. No renderer was started.'
}
$request.Preset.ContentHash = $before.Source.Sha256
$request.Plugin.PluginHash = $before.Plugin.Sha256
$request.Plugin.ExecutableHash = $before.Renderer.Sha256
$request.OutputPath = Join-Path $ArtifactDirectory 'normal.wav'
$request.CacheKey = [SoundPreviewer.Core.Models.Fingerprints]::CacheKey($request.Preset, $request.Plugin, $request.Template)
$settings = [SoundPreviewer.Core.Models.AppSettings]::new()
$settings.Plugins.Clear()
$settings.Plugins.Add($request.Plugin)
$settings.Template = $request.Template
$settings.PreviewDirectory = Join-Path $ArtifactDirectory 'baselines'
[IO.Directory]::CreateDirectory($ArtifactDirectory) | Out-Null
Write-NewJson 'normal-request.json' (Convert-TypedJson $request ([SoundPreviewer.Core.Models.RenderRequest]))
$rounds = if ($request.Template.QuickPreview) { 4 } else { 1 }
$cancellation = [Threading.CancellationTokenSource]::new()
$cancellation.CancelAfter([TimeSpan]::FromSeconds(7 * [Math]::Clamp($request.Plugin.TimeoutSeconds, 5, 3600) * $rounds + 60))
$watch = [Diagnostics.Stopwatch]::StartNew()
$timings = [ordered]@{}
$summary = [ordered]@{ Success = $false; Synth = $request.Plugin.Synth.ToString(); RequestPath = $RequestPath;
    ArtifactDirectory = $ArtifactDirectory; BaselineKind = [SoundPreviewer.Core.Services.InitBaselineService]::BaselineKind;
    PolicyVersion = [SoundPreviewer.Core.Audio.InitAudioComparer]::PolicyVersion; InputsBefore = $before;
    NoteOrder = $request.Template.NoteOrder.ToString(); RenderedPitches = @($request.Template.Pitches);
    ManualAudiblePresetConfirmationRequired = $true; TimingsSeconds = $timings }
$failure = $null
try {
    $stageStart = $watch.Elapsed.TotalSeconds
    $first = [SoundPreviewer.Core.Services.InitBaselineService]::GenerateAsync($settings, $request.Plugin, $WorkerPath, $cancellation.Token).GetAwaiter().GetResult()
    $summary.Baseline1 = Convert-TypedJson $first ([SoundPreviewer.Core.Services.InitBaselineState])
    $summary.Baseline1MetadataPath = $request.Plugin.InitBaselinePath
    if (-not $first.CanCompare) { throw "First baseline failed: $($first.Detail)" }
    $summary.Baseline1Inspect = Convert-TypedJson ([SoundPreviewer.Core.Services.InitBaselineService]::InspectAsync($settings, $request.Plugin, $cancellation.Token).GetAwaiter().GetResult()) ([SoundPreviewer.Core.Services.InitBaselineState])
    if (-not $summary.Baseline1Inspect.CanCompare) { throw 'First baseline inspection failed.' }
    $summary.Baseline1Metadata = [IO.File]::ReadAllText($summary.Baseline1MetadataPath) | ConvertFrom-Json -Depth 24
    $summary.Baseline1Result = $summary.Baseline1Metadata.Result
    $summary.Baseline1Audio = Get-AudioReport $first.WavPath
    $references = @($summary.Baseline1Metadata.References)
    if ($references.Count -ne 3) { throw 'A calibrated startup baseline must contain exactly three independent reference captures.' }
    $referenceSignatures = [Collections.Generic.List[SoundPreviewer.Core.Audio.InitAudioSignature]]::new()
    $referenceReports = @()
    foreach ($reference in $references) {
        if ($null -eq $reference -or $null -eq $reference.Result -or [string]::IsNullOrWhiteSpace($reference.WavPath) -or
            [string]::IsNullOrWhiteSpace($reference.WavSha256)) { throw 'A startup reference is missing its WAV, fingerprint or note result.' }
        $referenceReport = Get-AudioReport $reference.WavPath
        if (-not $referenceReport.Sha256.Equals($reference.WavSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A reference WAV differs from the fingerprint in its baseline metadata.'
        }
        $referenceResult = [Text.Json.JsonSerializer]::Deserialize(($reference.Result | ConvertTo-Json -Depth 24), [SoundPreviewer.Core.Models.RenderResult], $options)
        if (-not $referenceResult.Success -or
            -not [IO.Path]::GetFullPath($referenceResult.OutputPath).Equals([IO.Path]::GetFullPath($reference.WavPath), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A reference result is unsuccessful or points to a different WAV.'
        }
        [SoundPreviewer.Core.Audio.WorkerProcessClient]::ValidateNotes($referenceResult, $request.Template)
        $referenceAudio = [SoundPreviewer.Core.Audio.WaveFile]::Read($reference.WavPath, $cancellation.Token)
        $referenceSignatures.Add([SoundPreviewer.Core.Audio.InitAudioComparer]::CreateSignature($referenceAudio, $referenceResult.Notes, $cancellation.Token))
        $referenceReports += [ordered]@{ Index = $referenceReports.Count; Audio = $referenceReport;
            Result = (Convert-TypedJson $referenceResult ([SoundPreviewer.Core.Models.RenderResult])) }
        $summary.ReferenceReports = $referenceReports
    }
    $signature = [SoundPreviewer.Core.Audio.InitAudioComparer]::CreateCalibratedSignature($referenceSignatures, $cancellation.Token)
    $summary.ReferenceCount = $referenceSignatures.Count
    $timings.FirstBaseline = $watch.Elapsed.TotalSeconds - $stageStart

    $stageStart = $watch.Elapsed.TotalSeconds
    $second = [SoundPreviewer.Core.Services.InitBaselineService]::GenerateAsync($settings, $request.Plugin, $WorkerPath, $cancellation.Token).GetAwaiter().GetResult()
    $summary.Baseline2 = Convert-TypedJson $second ([SoundPreviewer.Core.Services.InitBaselineState])
    $summary.Baseline2MetadataPath = $request.Plugin.InitBaselinePath
    if (-not $second.CanCompare) { throw "Second baseline failed: $($second.Detail)" }
    $summary.Baseline2Inspect = Convert-TypedJson ([SoundPreviewer.Core.Services.InitBaselineService]::InspectAsync($settings, $request.Plugin, $cancellation.Token).GetAwaiter().GetResult()) ([SoundPreviewer.Core.Services.InitBaselineState])
    if (-not $summary.Baseline2Inspect.CanCompare) { throw 'Second baseline inspection failed.' }
    $summary.Baseline2Metadata = [IO.File]::ReadAllText($summary.Baseline2MetadataPath) | ConvertFrom-Json -Depth 24
    $summary.Baseline2Result = $summary.Baseline2Metadata.Result
    $summary.Baseline2Audio = Get-AudioReport $second.WavPath
    $secondAudio = [SoundPreviewer.Core.Audio.WaveFile]::Read($second.WavPath, $cancellation.Token)
    $pair = [SoundPreviewer.Core.Audio.InitAudioComparer]::Compare($signature, $secondAudio, $second.Notes, $cancellation.Token)
    $summary.BaselineComparison = Convert-TypedJson $pair ([SoundPreviewer.Core.Audio.InitAudioComparison])
    $timings.SecondBaselineAndComparison = $watch.Elapsed.TotalSeconds - $stageStart
    if (-not $pair.IsMatch -or $pair.MatchedReferences -lt 2) { throw 'An independent startup holdout did not match at least two complete three-note references within the calibrated policy; protection is not accepted as validated.' }

    $stageStart = $watch.Elapsed.TotalSeconds
    $client = [SoundPreviewer.Core.Audio.WorkerProcessClient]::new()
    $normal = $client.RenderAsync($request, $WorkerPath, $cancellation.Token).GetAwaiter().GetResult()
    $summary.NormalResult = Convert-TypedJson $normal ([SoundPreviewer.Core.Models.RenderResult])
    Write-NewJson 'normal-result.json' $summary.NormalResult
    if (-not $normal.Success) { throw "Normal FXP rendering failed: $($normal.Message)" }
    if (@($normal.Notes | Where-Object { $null -eq $_.Metrics -or $_.Metrics.IsSilent -or $_.Metrics.HasNonFinite -or $_.Metrics.Rms -lt 0.00001 }).Count -gt 0) {
        throw 'Normal FXP must have three audible finite note segments; silence cannot establish a useful difference from startup.'
    }
    $summary.NormalAudio = Get-AudioReport $normal.OutputPath
    $normalAudio = [SoundPreviewer.Core.Audio.WaveFile]::Read($normal.OutputPath, $cancellation.Token)
    $normalComparison = [SoundPreviewer.Core.Audio.InitAudioComparer]::Compare($signature, $normalAudio, $normal.Notes, $cancellation.Token)
    $summary.NormalComparison = Convert-TypedJson $normalComparison ([SoundPreviewer.Core.Audio.InitAudioComparison])
    $timings.NormalAndComparison = $watch.Elapsed.TotalSeconds - $stageStart
    if ($normalComparison.IsMatch) { throw 'The target FXP matches startup audio; this validation rejects it instead of claiming successful preset identity.' }
    $summary.Success = $true
} catch {
    $failure = $_
    $summary.Error = $_.Exception.ToString()
} finally {
    $cancellation.Cancel()
    $cancellation.Dispose()
    $watch.Stop()
    $summary.ElapsedSeconds = $watch.Elapsed.TotalSeconds
    $summary.InputsAfter = Get-Snapshot
    $summary.InputsUnchanged = ($before | ConvertTo-Json -Depth 8 -Compress) -ceq ($summary.InputsAfter | ConvertTo-Json -Depth 8 -Compress)
    if (-not $summary.InputsUnchanged) { $summary.Success = $false; $summary.Error = 'An input preset, plugin, runtime, worker or script changed during validation.' }
    Write-NewJson 'summary.json' $summary
}
$summary | ConvertTo-Json -Depth 24
if ($null -ne $failure) { throw $failure }
if (-not $summary.Success) { throw $summary.Error }
