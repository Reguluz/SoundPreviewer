[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonPath,
    [string]$ApplicationPath,
    [string]$DataDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$EnvironmentName = 'dawdreamer-cpython-x64',
    [AllowEmptyString()]
    [string]$Proxy,
    [switch]$IncludeSerum2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ApplicationPath) {
    $ApplicationPath = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Renderers/dawdreamer_worker.py')) {
        $PSScriptRoot
    } else { Split-Path -Parent $PSScriptRoot }
}
if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Container)) {
    throw 'ApplicationPath must identify an existing source or published application folder.'
}
$application = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ApplicationPath).ProviderPath)
if (-not $DataDirectory) {
    $DataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SoundPreviewer'
}
if (-not [IO.Path]::IsPathRooted($DataDirectory)) {
    throw 'DataDirectory must be an absolute path to the persistent user data folder.'
}
$data = [IO.Path]::GetFullPath($DataDirectory)
$resources = Join-Path $application 'Renderers'
if (-not (Test-Path -LiteralPath (Join-Path $resources 'dawdreamer_worker.py') -PathType Leaf)) {
    $resources = Join-Path $application 'scripts/renderers'
}
$requirements = Join-Path $resources 'requirements.txt'
$serum2Requirements = Join-Path $resources 'requirements-serum2.txt'
$worker = Join-Path $resources 'dawdreamer_worker.py'

function Assert-DataPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $data.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.Equals($data, [StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a path outside the selected persistent data directory: $full"
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a linked renderer data path: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Get-PythonInfo([string]$Executable) {
    $inspection = @'
import importlib.metadata
import json
from pathlib import Path
import platform
import struct
import sys
versions = {}
for name in ('dawdreamer', 'numpy', 'serum2-preset-loader', 'cbor2', 'zstandard'):
    try:
        versions[name] = importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        versions[name] = None
print(json.dumps({
    'Executable': sys.executable,
    'BaseExecutable': getattr(sys, '_base_executable', sys.executable),
    'Prefix': sys.prefix,
    'Platform': sys.platform,
    'Architecture': platform.machine(),
    'Major': sys.version_info.major,
    'Minor': sys.version_info.minor,
    'Bits': struct.calcsize('P') * 8,
    'Implementation': platform.python_implementation(),
    'IsVenv': sys.prefix != sys.base_prefix,
    'IsConda': (Path(sys.base_prefix) / 'conda-meta').is_dir() or 'anaconda' in sys.version.lower(),
    'HostVersion': versions['dawdreamer'],
    'NumpyVersion': versions['numpy'],
    'Serum2ConverterVersion': versions['serum2-preset-loader'],
    'CborVersion': versions['cbor2'],
    'ZstandardVersion': versions['zstandard'],
}))
'@
    $output = & $Executable -I -B -c $inspection 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Python inspection failed: $($output -join [Environment]::NewLine)"
    }
    try {
        return ($output -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch {
        throw "Python did not return valid inspection metadata: $Executable"
    }
}

function Assert-CompatiblePython($Info) {
    if ($Info.Platform -ne 'win32' -or $Info.Architecture -notin @('AMD64', 'x86_64') -or
        $Info.Implementation -ne 'CPython' -or $Info.Bits -ne 64 -or
        $Info.Major -ne 3 -or $Info.Minor -notin @(11, 12)) {
        throw 'This bridge requires standard Windows x64 CPython 3.11 or 3.12.'
    }
    if ($Info.IsConda) {
        throw 'Conda can load outdated private C++ runtime DLLs. Supply a standard CPython installation; no DLLs will be replaced.'
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'This renderer setup script supports Windows only.'
}
if (-not [IO.Path]::IsPathRooted($PythonPath) -or -not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    throw 'PythonPath must be the absolute path to an existing standard CPython python.exe.'
}
$sourcePython = (Resolve-Path -LiteralPath $PythonPath).ProviderPath
Write-Host 'Checking the explicitly selected Python installation...'
$sourceInfo = Get-PythonInfo $sourcePython
Assert-CompatiblePython $sourceInfo
if ($sourceInfo.IsVenv) {
    throw 'PythonPath must point to the base CPython installation, not an existing virtual environment.'
}
if (-not (Test-Path -LiteralPath $requirements -PathType Leaf) -or
    -not (Test-Path -LiteralPath $worker -PathType Leaf)) {
    throw 'The renderer requirements or worker script is missing from the selected application folder.'
}
$pins = @(Get-Content -LiteralPath $requirements | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') } |
    ForEach-Object { $_.Trim() })
if ($pins.Count -ne 2 -or 'dawdreamer==0.8.3' -notin $pins -or 'numpy==1.26.4' -notin $pins) {
    throw 'Expected the audited pins dawdreamer==0.8.3 and numpy==1.26.4; review setup before changing dependencies.'
}
$selectedRequirements = $requirements
if ($IncludeSerum2) {
    if (-not (Test-Path -LiteralPath $serum2Requirements -PathType Leaf)) {
        throw 'The optional Serum 2 requirements file is missing.'
    }
    $serum2Pins = @(Get-Content -LiteralPath $serum2Requirements | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') } |
        ForEach-Object { $_.Trim() })
    if ($serum2Pins.Count -ne 4 -or '-r requirements.txt' -notin $serum2Pins -or
        'serum2-preset-loader==0.1.3' -notin $serum2Pins -or 'cbor2==5.6.5' -notin $serum2Pins -or
        'zstandard==0.23.0' -notin $serum2Pins) {
        throw 'Expected the audited Serum 2 converter, CBOR and zstandard pins; review setup before changing dependencies.'
    }
    $selectedRequirements = $serum2Requirements
}
$renderers = Assert-DataPath (Join-Path $data 'renderers')
$environment = Assert-DataPath (Join-Path $renderers $EnvironmentName)
$environmentPython = Assert-DataPath (Join-Path $environment 'Scripts/python.exe')
$wheelCache = Assert-DataPath (Join-Path $renderers ("wheels-cp3{0}-x64" -f $sourceInfo.Minor))
$diagnostics = Assert-DataPath (Join-Path $data 'diagnostics')
$probeResult = Assert-DataPath (Join-Path $diagnostics ("host-probe-{0}.json" -f $EnvironmentName))
if (-not (Test-Path -LiteralPath $environment)) {
    [IO.Directory]::CreateDirectory($renderers) | Out-Null
    Write-Host 'Creating a virtual environment in the persistent user data folder...'
    & $sourcePython -I -B -m venv $environment
    if ($LASTEXITCODE -ne 0) {
        throw 'Virtual environment creation failed; no global pip command was run.'
    }
}
if (-not (Test-Path -LiteralPath $environmentPython -PathType Leaf)) {
    throw 'The selected environment directory already exists but has no Python executable. Choose a new EnvironmentName; nothing was deleted.'
}
$environmentInfo = Get-PythonInfo $environmentPython
Assert-CompatiblePython $environmentInfo
if (-not $environmentInfo.IsVenv -or
    -not [IO.Path]::GetFullPath($environmentInfo.Prefix).Equals($environment, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFullPath($environmentInfo.BaseExecutable).Equals(
        [IO.Path]::GetFullPath($sourceInfo.Executable), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The existing environment does not belong to the selected base Python. Choose a new EnvironmentName; nothing was replaced.'
}
$dependenciesReady = $environmentInfo.HostVersion -eq '0.8.3' -and $environmentInfo.NumpyVersion -eq '1.26.4'
if ($IncludeSerum2) {
    $dependenciesReady = $dependenciesReady -and $environmentInfo.Serum2ConverterVersion -eq '0.1.3' -and
        $environmentInfo.CborVersion -eq '5.6.5' -and $environmentInfo.ZstandardVersion -eq '0.23.0'
}
if ($dependenciesReady) {
    Write-Host 'The fixed dependencies are already installed; skipping package download and installation.'
}
else {
    [IO.Directory]::CreateDirectory($wheelCache) | Out-Null
    $downloadArguments = @('-I', '-B', '-m', 'pip', '--isolated', '--disable-pip-version-check', '--no-input', '--cache-dir', (Join-Path $wheelCache 'pip-cache'),
        'download', '--only-binary=:all:', '--index-url', 'https://pypi.org/simple',
        '--dest', $wheelCache, '-r', $selectedRequirements)
    if ($PSBoundParameters.ContainsKey('Proxy')) {
        $downloadArguments += "--proxy=$Proxy"
    }
    Write-Host 'Downloading fixed binary wheels from the official Python package index...'
    & $environmentPython @downloadArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Official wheel download failed. Cached wheels remain available; no package installation was attempted.'
    }
    Write-Host 'Installing only from the persistent wheel cache...'
    & $environmentPython -I -B -m pip --isolated --disable-pip-version-check --no-input --cache-dir (Join-Path $wheelCache 'pip-cache') install --no-index --find-links $wheelCache -r $selectedRequirements
    if ($LASTEXITCODE -ne 0) {
        throw 'Offline installation into the persistent virtual environment failed.'
    }
    $environmentInfo = Get-PythonInfo $environmentPython
    if ($environmentInfo.HostVersion -ne '0.8.3' -or $environmentInfo.NumpyVersion -ne '1.26.4') {
        throw 'The environment does not contain the required fixed dependency versions after installation.'
    }
    if ($IncludeSerum2 -and ($environmentInfo.Serum2ConverterVersion -ne '0.1.3' -or
        $environmentInfo.CborVersion -ne '5.6.5' -or $environmentInfo.ZstandardVersion -ne '0.23.0')) {
        throw 'The environment does not contain the required fixed Serum 2 dependency versions after installation.'
    }
}
[IO.Directory]::CreateDirectory($diagnostics) | Out-Null
Write-Host 'Running the bounded host probe without a commercial plugin...'
& $environmentPython -I -B $worker --probe --result $probeResult
$probeExitCode = $LASTEXITCODE
if (-not (Test-Path -LiteralPath $probeResult -PathType Leaf)) {
    throw "The host probe returned no report (exit $probeExitCode)."
}
if ((Get-Item -LiteralPath $probeResult).Length -gt 1MB) {
    throw "The host probe returned an oversized report: $probeResult"
}
$probe = Get-Content -LiteralPath $probeResult -Raw | ConvertFrom-Json
if ($probe -isnot [pscustomobject]) {
    throw "The host probe report is not a JSON object: $probeResult"
}
if ($probeExitCode -ne 0 -or $probe.Success -ne $true -or $probe.CommercialPluginsLoaded -ne $false -or
    $probe.AudioWritten -ne $false -or $probe.ProbeKind -ne 'EmptyHostDsp' -or $probe.Finished -ne $true -or
    $probe.HostVersion -ne '0.8.3' -or $probe.NumpyVersion -ne '1.26.4') {
    throw "Host probe did not pass: $($probe.Message) Report: $probeResult"
}
[pscustomobject]@{
    Success = $true
    EnvironmentPythonPath = $environmentPython
    DataDirectory = $data
    ProbeResultPath = $probeResult
    IncludeSerum2 = [bool]$IncludeSerum2
    Message = 'The isolated host is ready for a separate real-plugin validation. No commercial plugin was loaded by this setup.'
}
