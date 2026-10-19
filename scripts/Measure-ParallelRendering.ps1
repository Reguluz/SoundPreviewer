[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 8)]
    [string[]]$PresetPath,
    [string]$RendererPath,
    [string]$WorkerPath,
    [string]$TestAssembly,
    [int[]]$SlotOrder = @(1, 2),
    [ValidateRange(1, 3)]
    [int]$WarmRuns = 2,
    [ValidateRange(1, 60)]
    [int]$TimeoutMinutes = 15,
    [switch]$Execute
)

# Preparation only unless -Execute is supplied. Execution loads the user's real
# plugin, which may itself read or write its normal preferences/licensing state.
# This harness writes its libraries and audio only to a new artifacts directory.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($env:OS -ne 'Windows_NT') { throw 'This small-sample harness currently supports Windows DLL plugins only; the application queue supports both desktop platforms.' }
if ($SlotOrder.Count -ne 2 -or @($SlotOrder | Sort-Object -Unique) -join ',' -ne '1,2') { throw 'SlotOrder must be 1,2 or 2,1.' }

function Resolve-InputFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

$requestFile = Resolve-InputFile $RequestPath
$request = Get-Content -LiteralPath $requestFile -Raw | ConvertFrom-Json
if ($RendererPath) { $request.Plugin.ExecutablePath = Resolve-InputFile $RendererPath }
$request.Plugin.ExecutablePath = Resolve-InputFile $request.Plugin.ExecutablePath
$request.Plugin.PluginPath = Resolve-InputFile $request.Plugin.PluginPath
if ([IO.Path]::GetExtension($request.Plugin.PluginPath) -ne '.dll') { throw 'This benchmark currently accepts Windows DLL plugins only, not VST bundles.' }
$inputs = @($PresetPath | ForEach-Object { Resolve-InputFile $_ })
if (@($inputs | Select-Object -Unique).Count -ne $inputs.Count) { throw 'Select distinct input files.' }
$hashes = @($inputs | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() })
if (@($hashes | Select-Object -Unique).Count -ne $inputs.Count) {
    throw 'Each input must have different content; identical cache keys would measure singleflight instead of parallel generation.'
}
if (-not $TestAssembly) { $TestAssembly = Join-Path $repository 'tests/SoundPreviewer.Tests/bin/Release/net8.0/SoundPreviewer.Tests.dll' }
if (-not $WorkerPath) { $WorkerPath = Join-Path (Split-Path -Parent $TestAssembly) 'SoundPreviewer.Worker.exe' }
if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf) -and [IO.Path]::GetExtension($WorkerPath) -eq '.exe') {
    $WorkerPath = [IO.Path]::ChangeExtension($WorkerPath, '.dll')
}

$artifacts = Join-Path $repository 'artifacts'
$output = Join-Path $artifacts ('parallel-benchmark-' + [Guid]::NewGuid().ToString('N'))
foreach ($path in @($artifacts, $output)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing a linked benchmark output: $path"
    }
}
New-Item -ItemType Directory -Path $output | Out-Null
$manifest = [ordered]@{
    Request = $request
    PresetPaths = $inputs
    WorkerPath = [IO.Path]::GetFullPath($WorkerPath)
    ArtifactDirectory = $output
    Slots = $SlotOrder
    WarmRuns = $WarmRuns
    TimeoutMinutes = $TimeoutMinutes
}
$manifestPath = Join-Path $output 'manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
Write-Host "Prepared benchmark: $manifestPath"
Write-Host 'Cold = new library and baseline; warm-force = new audio with existing baseline; cache-reuse is reported separately.'
if (-not $Execute) {
    Write-Host 'No renderer was started. Add -Execute when real plugin execution is intended; no build is performed.'
    return
}
$TestAssembly = Resolve-InputFile $TestAssembly
$WorkerPath = Resolve-InputFile $WorkerPath
& dotnet $TestAssembly --benchmark-parallel $manifestPath
if ($LASTEXITCODE -ne 0) { throw "Benchmark failed; inspect $output/report.json and the isolated libraries." }
Write-Host "Results: $(Join-Path $output 'report.json')"
