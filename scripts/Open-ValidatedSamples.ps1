#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$SummaryPath,
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\artifacts\validated-samples'),
    [string]$AppPath = (Join-Path $PSScriptRoot '..\artifacts\publish\SoundPreviewer.exe'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$prefix = $artifacts + [IO.Path]::DirectorySeparatorChar
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$AppPath = [IO.Path]::GetFullPath($AppPath)
if (-not $DataDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The sample library must be inside this repository''s artifacts directory.'
}
$ancestor = $DataDirectory
while ($ancestor.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The sample library cannot traverse a directory link: $ancestor"
    }
    if ($ancestor.Equals($artifacts, [StringComparison]::OrdinalIgnoreCase)) { break }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
$databasePath = Join-Path $DataDirectory 'library.db'
if (Test-Path -LiteralPath $databasePath) { throw 'Use a new sample-library directory; existing libraries are not overwritten.' }
$previewDirectory = Join-Path $DataDirectory 'Previews'
if ((Test-Path -LiteralPath $previewDirectory) -and ((Get-Item -LiteralPath $previewDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The sample preview directory cannot be a directory link.'
}
if (-not [IO.File]::Exists($AppPath)) { throw 'Publish the desktop application before opening the validated samples.' }
Add-Type -Path (Join-Path ([IO.Path]::GetDirectoryName($AppPath)) 'SoundPreviewer.Core.dll')
$options = [SoundPreviewer.Core.Models.LibraryJson]::Options
$settings = [SoundPreviewer.Core.Models.AppSettings]::new()
$settings.PreviewDirectory = $previewDirectory
$settings.PlaybackVolume = 0.2
$records = [Collections.Generic.List[SoundPreviewer.Core.Models.PresetRecord]]::new()
$templateJson = $null
$scannedDirectories = @{}
foreach ($path in $SummaryPath) {
    $fullPath = [IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $fullPath).Length -gt 1MB) {
        throw 'Validation summaries must be local artifacts smaller than 1 MiB.'
    }
    $summary = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if (-not $summary.Success -or -not $summary.SourceUnchanged -or -not $summary.PluginUnchanged) {
        throw "The sample did not pass source/plugin validation: $fullPath"
    }
    $request = [Text.Json.JsonSerializer]::Deserialize(($summary.EffectiveRequest | ConvertTo-Json -Depth 16), [SoundPreviewer.Core.Models.RenderRequest], $options)
    $result = [Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($summary.ResultPath), [SoundPreviewer.Core.Models.RenderResult], $options)
    if (-not $result.Success -or -not [IO.File]::Exists($result.OutputPath)) { throw 'The validated output is no longer available.' }
    if (-not $result.OutputPath.Equals($request.OutputPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not $result.OutputPath.Equals($summary.OutputPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The summary, request and result must refer to the same validated WAV.'
    }
    if ([SoundPreviewer.Core.Models.Fingerprints]::HashFile($request.Preset.SourcePath) -ne $request.Preset.ContentHash -or
        [SoundPreviewer.Core.Models.Fingerprints]::HashFile($request.Plugin.PluginPath) -ne $request.Plugin.PluginHash -or
        [SoundPreviewer.Core.Models.Fingerprints]::HashFile($result.OutputPath) -ne $summary.Audio.WavSha256) {
        throw 'An input or preview changed after validation; repeat the single-preset test.'
    }
    [SoundPreviewer.Core.Audio.WorkerProcessClient]::ValidateNotes($result, $request.Template)
    $currentTemplate = [Text.Json.JsonSerializer]::Serialize($request.Template, [SoundPreviewer.Core.Models.PreviewTemplate], $options)
    if ($null -ne $templateJson -and $currentTemplate -ne $templateJson) { throw 'Use one shared audition template for this sample library.' }
    $templateJson = $currentTemplate
    $settings.Template = $request.Template
    $preset = $request.Preset
    $preset.SourceId = 'validated-single-samples'
    $preset.ContentMd5 = (Get-FileHash -LiteralPath $preset.SourcePath -Algorithm MD5).Hash.ToLowerInvariant()
    $source = Get-Item -LiteralPath $preset.SourcePath
    $preset.FileSize = $source.Length
    $preset.LastWriteUtc = $source.LastWriteTimeUtc
    $preset.Status = [SoundPreviewer.Core.Models.PresetStatus]::NeedsReview
    $preset.PreviewPath = $result.OutputPath
    $preset.CacheKey = $request.CacheKey
    $preset.AttemptedCacheKey = $request.CacheKey
    $preset.Notes = $result.Notes
    $preset.Metrics = [SoundPreviewer.Core.Audio.WaveFile]::Validate($result.OutputPath)
    $preset.TemplateJson = $templateJson
    $preset.AdapterVersion = $result.AdapterVersion
    $preset.PluginVersion = $request.Plugin.Version
    $preset.RenderedUtc = [DateTime]::UtcNow
    $preset.Message = $result.Message
    $preset.Package = '单音色实际渲染验收'
    $preset.Tags.Add('单样本验证')
    $records.Add($preset)
    $settings.Plugins.RemoveAll([Predicate[SoundPreviewer.Core.Models.PluginConfiguration]]{ param($plugin) $plugin.Synth -eq $request.Plugin.Synth }) | Out-Null
    $settings.Plugins.Add($request.Plugin)
    $pluginDirectory = [IO.Path]::GetDirectoryName($request.Plugin.PluginPath)
    if (-not $scannedDirectories.ContainsKey($pluginDirectory)) {
        $scanner = [SoundPreviewer.Core.Scanning.PluginScanService]::new()
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
        try {
            $scannedDirectories[$pluginDirectory] = $scanner.ScanAsync([string[]]@($pluginDirectory), $deadline.Token).GetAwaiter().GetResult()
        } finally { $deadline.Dispose() }
    }
    $pluginMetadata = $scannedDirectories[$pluginDirectory].Plugins | Where-Object {
        $_.Synth -eq $request.Plugin.Synth -and $_.BinaryPath.Equals($request.Plugin.PluginPath, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if ($null -eq $pluginMetadata) { throw 'The validated plugin no longer passes read-only plugin discovery.' }
    if (-not ($settings.DiscoveredPlugins | Where-Object { $_.Id -eq $pluginMetadata.Id })) {
        $settings.DiscoveredPlugins.Add($pluginMetadata)
    }
}
if ($records.Count -eq 0) { throw 'No validated single-preset result was provided.' }
$store = [SoundPreviewer.Core.Storage.LibraryStore]::new($databasePath)
$store.Initialize()
$store.SaveSettings($settings)
$store.UpsertMany($records)
Write-Host ("Validated sample library: {0} ({1} presets; no scan roots)" -f $DataDirectory, $records.Count)
if (-not $NoLaunch) {
    Start-Process -FilePath $AppPath -ArgumentList @('--data-directory', ('"{0}"' -f $DataDirectory)) -WindowStyle Normal
}
