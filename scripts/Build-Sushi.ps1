[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Vst2SdkDirectory,
    [Parameter(Mandatory = $true)][string]$SndFileDirectory,
    [string]$SourceDirectory,
    [string]$BuildDirectory,
    [string]$OutputDirectory,
    [string]$CMakePath = 'cmake',
    [ValidateRange(1, 32)][int]$Parallel = 4,
    [switch]$PrepareOnly
)

# Developer-only compatibility research. This script does not publish a package,
# obtain a VST2 SDK, or establish permission to distribute Sushi with closed plugins.
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This research build currently targets Windows x64 only.' }
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactsDirectory = Join-Path $projectDirectory 'artifacts'
$sourceCommit = 'c6ddde6b6b6c06229118432c8c7f7a7c61a61408'
$sourceUrl = 'https://github.com/elk-audio/sushi.git'
$patch = Join-Path $projectDirectory 'patches/Sushi1.4/soundpreviewer-state-files.patch'
$noticesDirectory = Join-Path $projectDirectory 'third-party/sushi'

function Get-ArtifactPath([string]$Path, [string]$Default) {
    $full = [IO.Path]::GetFullPath($(if ($Path) { $Path } else { Join-Path $artifactsDirectory $Default }))
    if (-not $full.StartsWith($artifactsDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Research output must remain beneath $artifactsDirectory"
    }
    $ancestor = $full
    while ($ancestor -ne $projectDirectory) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a linked research directory: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $full
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

$SourceDirectory = Get-ArtifactPath $SourceDirectory 'sushi-source-1.4.0'
$BuildDirectory = Get-ArtifactPath $BuildDirectory 'sushi-build-win-x64'
$OutputDirectory = Get-ArtifactPath $OutputDirectory 'sushi-research-win-x64'
if ($SourceDirectory -eq $BuildDirectory -or $SourceDirectory -eq $OutputDirectory -or $BuildDirectory -eq $OutputDirectory) {
    throw 'Source, build and output directories must be different.'
}
$Vst2SdkDirectory = [IO.Path]::GetFullPath($Vst2SdkDirectory)
$SndFileDirectory = [IO.Path]::GetFullPath($SndFileDirectory)
$sdkHeaders = @('pluginterfaces/vst2.x/aeffect.h', 'pluginterfaces/vst2.x/aeffectx.h')
foreach ($relative in $sdkHeaders) {
    if (-not (Test-Path -LiteralPath (Join-Path $Vst2SdkDirectory $relative) -PathType Leaf)) {
        throw "Supply an external VST 2.4 SDK directory; missing $relative. The script does not download or redistribute it."
    }
}
foreach ($relative in @('cmake/SndFileConfig.cmake', 'lib/sndfile.lib', 'bin/sndfile.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SndFileDirectory $relative) -PathType Leaf)) {
        throw "Supply the extracted official libsndfile 1.2.2 win64 development package; missing $relative."
    }
}
$git = (Get-Command git -ErrorAction Stop).Source
if (-not $PrepareOnly) { $CMakePath = (Get-Command $CMakePath -ErrorAction Stop).Source }

if (-not (Test-Path -LiteralPath $SourceDirectory)) {
    New-Item -ItemType Directory -Path (Split-Path $SourceDirectory -Parent) -Force | Out-Null
    Invoke-Checked $git @('clone', '--no-checkout', '--filter=blob:none', $sourceUrl, $SourceDirectory)
    Invoke-Checked $git @('-C', $SourceDirectory, 'checkout', '--detach', $sourceCommit)
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory '.git'))) { throw 'The source directory must be the dedicated Sushi Git checkout.' }
$actualCommit = (& $git -C $SourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $sourceCommit) { throw "Expected Sushi source $sourceCommit; found $actualCommit. No checkout was reset." }
$origin = (& $git -C $SourceDirectory remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $origin -notmatch '^(https://github\.com/elk-audio/sushi(?:\.git)?|git@github\.com:elk-audio/sushi\.git)$') {
    throw 'Use a checkout whose origin is the official elk-audio/sushi repository.'
}

# Initialize only pinned upstream modules used by the offline build. In particular,
# do not fetch vcpkg, RPC, device backends, VSTGUI, SDK documentation or test suites.
$modules = @('twine', 'elklog', 'third-party/rapidjson', 'third-party/oscpack',
    'third-party/freeverb', 'third-party/brickworks', 'third-party/elk-plugin-extensions', 'third-party/vst3sdk')
Invoke-Checked $git (@('-C', $SourceDirectory, 'submodule', 'update', '--init', '--') + $modules)
Invoke-Checked $git @('-C', (Join-Path $SourceDirectory 'twine'), 'submodule', 'update', '--init', '--', 'elk-warning-suppressor')
Invoke-Checked $git @('-C', (Join-Path $SourceDirectory 'elklog'), 'submodule', 'update', '--init', '--', 'third-party/spdlog')
Invoke-Checked $git @('-C', (Join-Path $SourceDirectory 'third-party/vst3sdk'), 'submodule', 'update', '--init', '--', 'base', 'cmake', 'pluginterfaces', 'public.sdk')

& $git -C $SourceDirectory apply --check $patch 2>$null
if ($LASTEXITCODE -eq 0) {
    Invoke-Checked $git @('-C', $SourceDirectory, 'apply', $patch)
}
else {
    & $git -C $SourceDirectory apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'The source is neither pristine nor patched as expected. No files were reset or overwritten.' }
}
$patchFiles = @('apps/CMakeLists.txt', 'apps/main.cpp', 'apps/soundpreviewer-utf8.manifest',
    'src/engine/json_configurator.cpp', 'src/engine/json_schemas/events_schema.json', 'src/engine/json_schemas/state_schema.json',
    'src/library/vst2x/vst2x_plugin_loader.cpp', 'src/library/vst2x/vst2x_wrapper.cpp',
    'third-party/CMakeLists.txt', 'src/engine/preview_state_probe.h')
$changes = & $git -C $SourceDirectory status --porcelain --untracked-files=all --ignore-submodules=all
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source changes.' }
foreach ($change in $changes) {
    if ($change.Substring(3) -notin $patchFiles) { throw "Unexpected source change: $change. Use a dedicated checkout; nothing was removed." }
}
if ($PrepareOnly) {
    Write-Host "Prepared research source: $SourceDirectory"
    return
}

$configure = @('-S', $SourceDirectory, '-B', $BuildDirectory, '-G', 'Visual Studio 17 2022', '-A', 'x64',
    '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded', '-DSMTG_USE_STATIC_CRT=ON',
    '-DSUSHI_WITH_VST2=ON', '-DSUSHI_WITH_VST3=ON', '-DSUSHI_LINK_VST3=ON',
    '-DSUSHI_TWINE_STATIC=ON', '-DSUSHI_BUILD_TWINE=ON', '-DSUSHI_BUILD_STANDALONE_APP=ON',
    '-DSUSHI_WITH_JACK=OFF', '-DSUSHI_WITH_LV2=OFF', '-DSUSHI_WITH_LINK=OFF',
    '-DSUSHI_WITH_RPC_INTERFACE=OFF', '-DSUSHI_WITH_SENTRY=OFF', '-DSUSHI_WITH_RASPA=OFF',
    '-DSUSHI_WITH_PORTAUDIO=OFF', '-DSUSHI_WITH_APPLE_COREAUDIO=OFF',
    '-DSUSHI_WITH_ALSA_MIDI=OFF', '-DSUSHI_WITH_RT_MIDI=OFF',
    '-DSUSHI_WITH_UNIT_TESTS=OFF', '-DSUSHI_WITH_LV2_MDA_TESTS=OFF',
    "-DVST_2_SDK_BASE_DIR=$Vst2SdkDirectory", "-DSndFile_DIR=$(Join-Path $SndFileDirectory 'cmake')")
Invoke-Checked $CMakePath $configure
Invoke-Checked $CMakePath @('--build', $BuildDirectory, '--config', 'Release', '--target', 'sushi', '--parallel', "$Parallel")

$executable = Join-Path $BuildDirectory 'apps/Release/sushi.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The build did not produce apps/Release/sushi.exe.' }
if (Test-Path -LiteralPath $OutputDirectory) {
    $userData = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force | Where-Object {
        $_.Name -in @('Preview', 'Config', 'directories.json', 'library-location.json', 'settings.json') -or
        $_.Extension -in @('.db', '.db-wal', '.db-shm', '.wav')
    } | Select-Object -First 1
    if ($userData) { throw "Output contains user data: $($userData.FullName). Nothing was removed." }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $OutputDirectory 'sushi.exe')
Copy-Item -LiteralPath (Join-Path $SndFileDirectory 'bin/sndfile.dll') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $noticesDirectory 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $noticesDirectory 'licenses') -Destination $OutputDirectory -Recurse -Force
$inputHashes = foreach ($relative in $sdkHeaders) {
    @{ Name = $relative; Sha256 = (Get-FileHash -LiteralPath (Join-Path $Vst2SdkDirectory $relative) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$outputHashes = foreach ($name in @('sushi.exe', 'sndfile.dll')) {
    @{ Name = $name; Sha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = @{
    Purpose = 'Local compatibility research only; not cleared for redistribution with proprietary plugins.'
    SourceUrl = $sourceUrl; SourceCommit = $sourceCommit
    PatchSha256 = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
    Target = 'win-x64'; Configuration = 'Release'; MsvcRuntime = 'MultiThreaded'; StaticVst3Crt = $true
    SndFilePackage = 'libsndfile-1.2.2-win64'; Vst2SdkInputHeaders = @($inputHashes)
    Submodules = @(& $git -C $SourceDirectory submodule status --recursive)
    Files = @($outputHashes); BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "Research-only build: $OutputDirectory"
