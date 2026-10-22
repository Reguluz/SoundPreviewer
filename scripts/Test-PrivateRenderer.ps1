[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [string]$UvPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This verification targets Windows x64.' }
$project = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifacts = Join-Path $project 'artifacts'
$source = [IO.Path]::GetFullPath($ApplicationDirectory)
if (Get-ChildItem -LiteralPath (Join-Path $source 'Renderers') -Recurse -Force | Where-Object {
    $_.Name -in @('Preview', 'local-data', 'settings.json', 'directories.json', 'library-location.json', 'preset-categories.json') -or
    $_.Extension -in @('.db', '.db-wal', '.db-shm', '.wav', '.m4a', '.log')
}) { throw 'The renderer resources contain personal data; refusing a private-runtime copy.' }
$diagnostics = Join-Path $artifacts ('private-renderer-win-x64-' + [Guid]::NewGuid().ToString('N'))
$application = Join-Path $diagnostics 'application'
$moved = Join-Path $diagnostics 'moved application 中文'
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$setupSource = Join-Path $source 'Prepare-DawDreamer.ps1'
if (-not (Test-Path -LiteralPath $setupSource -PathType Leaf)) { $setupSource = Join-Path $PSScriptRoot 'Prepare-DawDreamer.ps1' }
foreach ($path in @($diagnostics, $application, $moved)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture output escapes artifacts.' }
    if (Test-Path -LiteralPath $path) { throw 'Use a new isolated fixture directory.' }
}
New-Item -ItemType Directory -Path $application -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'Renderers') -Destination $application -Recurse
Copy-Item -LiteralPath $setupSource -Destination (Join-Path $application 'Prepare-DawDreamer.ps1')
$sourceHashes = @{}
Get-ChildItem -LiteralPath (Join-Path $source 'Renderers') -File -Recurse | ForEach-Object {
    $sourceHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$sourceHashes[$setupSource] = (Get-FileHash -LiteralPath $setupSource -Algorithm SHA256).Hash
$preview = Join-Path $application 'Preview'
$config = Join-Path $application 'Config'
New-Item -ItemType Directory -Path $preview, $config -Force | Out-Null
$personal = Join-Path $preview 'TEST-ONLY-personal-record.txt'
$custom = Join-Path $config 'TEST-ONLY-user-settings.json'
[IO.File]::WriteAllText($personal, 'TEST ONLY existing personal record; not an actual library.')
[IO.File]::WriteAllText($custom, '{"TEST-ONLY":"preserve"}')
$personalHash = (Get-FileHash -LiteralPath $personal -Algorithm SHA256).Hash
$configHash = (Get-FileHash -LiteralPath $custom -Algorithm SHA256).Hash
$leak = Join-Path $diagnostics 'TEST-ONLY-external-cache'
$poisoned = @{
    UV_CACHE_DIR = $leak
    UV_PYTHON_INSTALL_DIR = (Join-Path $leak 'python')
    UV_PYTHON_DOWNLOADS_JSON_URL = 'https://invalid.invalid/TEST-ONLY-ignored-python.json'
    UV_DEFAULT_INDEX = 'https://invalid.invalid/TEST-ONLY-ignored-index'
    PIP_INDEX_URL = 'https://invalid.invalid/TEST-ONLY-ignored-pip'
    PYTHONHOME = (Join-Path $leak 'wrong-python-home')
    PYTHONPATH = (Join-Path $leak 'wrong-python-path')
}
$before = @{}
foreach ($name in $poisoned.Keys) {
    $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $poisoned[$name], 'Process')
}
function Run-Setup([string]$App, [string]$Phase, [switch]$Offline, [switch]$ExpectFailure, [switch]$OmitIncludeSerum2) {
    $result = Join-Path $App 'Preview/diagnostics/TEST-ONLY-setup-result.json'
    $command = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $App 'Prepare-DawDreamer.ps1'),
        '-ApplicationPath', $App, '-ResultPath', $result)
    if (-not $OmitIncludeSerum2) { $command += '-IncludeSerum2' }
    if ($UvPath) { $command += @('-UvPath', [IO.Path]::GetFullPath($UvPath)) }
    if ($Offline) { $command += '-Offline' }
    $output = & $powershell @command 2>&1
    $exit = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $diagnostics ($Phase + '.log')), ($output -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    if ($ExpectFailure) {
        if ($exit -eq 0) { throw 'The deliberate probe failure was reported as success.' }
        return
    }
    if ($exit -ne 0) { throw ('Private renderer setup failed; see ' + (Join-Path $diagnostics ($Phase + '.log'))) }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if (-not $report.Success -or -not $report.IncludeSerum2 -or -not $report.ManagedRuntime -or $report.RuntimeKind -ne 'uv-managed-cpython' -or
        -not (Test-Path -LiteralPath $report.EnvironmentPythonPath -PathType Leaf)) { throw 'Invalid successful setup report.' }
    return $report
}
try {
    $first = Run-Setup $application 'first-online'
    $initialRuntime = Join-Path $application 'Preview/renderers/dawdreamer-portable-win-x64'
    $initialManifest = Join-Path $initialRuntime 'runtime.json'
    $manifestHash = (Get-FileHash -LiteralPath $initialManifest -Algorithm SHA256).Hash
    $pythonHash = (Get-FileHash -LiteralPath $first.EnvironmentPythonPath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath (Join-Path $initialRuntime 'pyvenv.cfg')) { throw 'Portable runtime must not use a base-path virtual environment.' }
    if (Test-Path -LiteralPath $leak) { throw 'Inherited global cache settings leaked outside the application.' }
    [IO.Directory]::Move($application, $moved)
    $relocated = Run-Setup $moved 'relocated-offline' -Offline -OmitIncludeSerum2
    $runtime = Join-Path $moved 'Preview/renderers/dawdreamer-portable-win-x64'
    $manifest = Join-Path $runtime 'runtime.json'
    if (-not $relocated.EnvironmentPythonPath.StartsWith($runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Relocated setup retained the old absolute interpreter path.' }
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $manifestHash -or
        (Get-FileHash -LiteralPath $relocated.EnvironmentPythonPath -Algorithm SHA256).Hash -ne $pythonHash) { throw 'Offline relocation unnecessarily changed the runtime identity.' }
    $probe = Get-Content -LiteralPath $relocated.ProbeResultPath -Raw | ConvertFrom-Json
    foreach ($library in $probe.RuntimeDlls) {
        if (-not $library.Path.StartsWith($runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The probe relied on a global C++ runtime instead of the private runtime.' }
    }
    $interruptedBackup = $runtime + '-before-setup-' + [Guid]::NewGuid().ToString('N')
    if (-not $interruptedBackup.StartsWith($diagnostics + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery fixture escapes diagnostics.' }
    [IO.Directory]::Move($runtime, $interruptedBackup)
    [void](Run-Setup $moved 'interrupted-promotion-recovery' -Offline)
    if (-not (Test-Path -LiteralPath $runtime -PathType Container) -or (Test-Path -LiteralPath $interruptedBackup) -or
        (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $manifestHash) { throw 'Interrupted promotion did not recover the retained runtime.' }
    $record = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $native = @($record.Files.PSObject.Properties | Where-Object { $_.Name -like '*dawdreamer*.pyd' })
    if ($native.Count -ne 1) { throw 'Expected one pinned DawDreamer native extension in the runtime inventory.' }
    $missingNative = Join-Path $runtime $native[0].Name
    if (-not [IO.Path]::GetFullPath($missingNative).StartsWith($runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Damaged-file fixture escapes the isolated runtime.' }
    [IO.File]::Move($missingNative, $missingNative + '.TEST-ONLY-missing')
    [void](Run-Setup $moved 'repair-missing-native-file' -OmitIncludeSerum2)
    if (-not (Test-Path -LiteralPath $missingNative -PathType Leaf) -or
        (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $manifestHash) { throw 'Preparation did not repair the missing native runtime file.' }
    $worker = Join-Path $moved 'Renderers/dawdreamer_worker.py'
    $originalWorker = [IO.File]::ReadAllBytes($worker)
    [IO.File]::WriteAllText($worker, 'raise RuntimeError("TEST ONLY intentional preparation failure")')
    try { Run-Setup $moved 'deliberate-failure' -Offline -ExpectFailure }
    finally { [IO.File]::WriteAllBytes($worker, $originalWorker) }
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $manifestHash -or
        (Get-FileHash -LiteralPath $relocated.EnvironmentPythonPath -Algorithm SHA256).Hash -ne $pythonHash) { throw 'Failed preparation damaged the previously valid runtime.' }
    if ((Get-FileHash -LiteralPath (Join-Path $moved 'Preview/TEST-ONLY-personal-record.txt') -Algorithm SHA256).Hash -ne $personalHash -or
        (Get-FileHash -LiteralPath (Join-Path $moved 'Config/TEST-ONLY-user-settings.json') -Algorithm SHA256).Hash -ne $configHash) { throw 'Preparation changed existing personal records.' }
    foreach ($path in $sourceHashes.Keys) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $sourceHashes[$path]) { throw 'Verification changed a source application resource.' }
    }
    if (Test-Path -LiteralPath $leak) { throw 'Private preparation wrote to a poisoned external cache.' }
    $final = Run-Setup $moved 'reopen-offline' -Offline
    $report = [ordered]@{
        Success = $true; Platform = 'win-x64'; Directory = $diagnostics
        WindowsPowerShellVersion = '5.1'; FirstSetup = $true; RelocatedOffline = $true; ReopenedOffline = $final.Success
        PrivateCppRuntime = $true; FailedSetupPreservedRuntime = $true; ExistingRecordsPreserved = $true
        InterruptedPromotionRecovered = $true; DamagedRuntimeRepaired = $true; ExistingSerum2Preserved = $true
        SourceFilesPreserved = $true; NoExternalCacheWrites = $true; IdentityUnchanged = $true
        PythonPath = $final.EnvironmentPythonPath
    }
    [IO.File]::WriteAllText((Join-Path $diagnostics 'report.json'), ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $report | ConvertTo-Json -Depth 5
}
finally {
    foreach ($name in $before.Keys) { [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process') }
}
