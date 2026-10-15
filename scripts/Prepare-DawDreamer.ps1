[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonPath,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$EnvironmentName = 'dawdreamer-cpython-x64',
    [AllowEmptyString()]
    [string]$Proxy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $workspace 'artifacts'
$requirements = Join-Path $PSScriptRoot 'renderers/requirements.txt'
$worker = Join-Path $PSScriptRoot 'renderers/dawdreamer_worker.py'

function Assert-ArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.Equals($artifacts, [StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a path outside this workspace's artifacts directory: $full"
    }
    $current = $full
    while (-not $current.Equals($workspace, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a linked artifacts path: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) {
            throw "Cannot verify the artifacts path: $full"
        }
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
for name in ('dawdreamer', 'numpy'):
    try:
        versions[name] = importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        versions[name] = None
print(json.dumps({
    'Executable': sys.executable,
    'BaseExecutable': getattr(sys, '_base_executable', sys.executable),
    'Major': sys.version_info.major,
    'Minor': sys.version_info.minor,
    'Bits': struct.calcsize('P') * 8,
    'Implementation': platform.python_implementation(),
    'IsVenv': sys.prefix != sys.base_prefix,
    'IsConda': (Path(sys.base_prefix) / 'conda-meta').is_dir() or 'anaconda' in sys.version.lower(),
    'HostVersion': versions['dawdreamer'],
    'NumpyVersion': versions['numpy'],
}))
'@
    $output = & $Executable -I -c $inspection 2>&1
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
    if ($Info.Implementation -ne 'CPython' -or $Info.Bits -ne 64 -or
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
    throw 'The renderer requirements or worker script is missing from this workspace.'
}
$pins = @(Get-Content -LiteralPath $requirements | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') } |
    ForEach-Object { $_.Trim() })
if ($pins.Count -ne 2 -or 'dawdreamer==0.8.3' -notin $pins -or 'numpy==1.26.4' -notin $pins) {
    throw 'Expected the audited pins dawdreamer==0.8.3 and numpy==1.26.4; review setup before changing dependencies.'
}
$environment = Assert-ArtifactPath (Join-Path $artifacts $EnvironmentName)
$environmentPython = Assert-ArtifactPath (Join-Path $environment 'Scripts/python.exe')
$wheelCache = Assert-ArtifactPath (Join-Path $artifacts ("dawdreamer-wheels-cp3{0}" -f $sourceInfo.Minor))
$diagnostics = Assert-ArtifactPath (Join-Path $artifacts 'diagnostics')
$probeResult = Assert-ArtifactPath (Join-Path $diagnostics ("host-probe-{0}.json" -f $EnvironmentName))
if (-not (Test-Path -LiteralPath $environment)) {
    [IO.Directory]::CreateDirectory((Assert-ArtifactPath $artifacts)) | Out-Null
    Write-Host 'Creating a workspace-only virtual environment...'
    & $sourcePython -I -m venv $environment
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
    -not [IO.Path]::GetFullPath($environmentInfo.BaseExecutable).Equals(
        [IO.Path]::GetFullPath($sourceInfo.Executable), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The existing environment does not belong to the selected base Python. Choose a new EnvironmentName; nothing was replaced.'
}
if ($environmentInfo.HostVersion -eq '0.8.3' -and $environmentInfo.NumpyVersion -eq '1.26.4') {
    Write-Host 'The fixed dependencies are already installed; skipping package download and installation.'
}
else {
    [IO.Directory]::CreateDirectory($wheelCache) | Out-Null
    $downloadArguments = @('-I', '-m', 'pip', '--isolated', '--disable-pip-version-check', '--no-input',
        'download', '--only-binary=:all:', '--index-url', 'https://pypi.org/simple',
        '--dest', $wheelCache, '-r', $requirements)
    if ($PSBoundParameters.ContainsKey('Proxy')) {
        $downloadArguments += "--proxy=$Proxy"
    }
    Write-Host 'Downloading fixed binary wheels from the official Python package index...'
    & $environmentPython @downloadArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Official wheel download failed. Cached wheels remain available; no package installation was attempted.'
    }
    Write-Host 'Installing only from the workspace wheel cache...'
    & $environmentPython -I -m pip --isolated --disable-pip-version-check --no-input install --no-index --find-links $wheelCache -r $requirements
    if ($LASTEXITCODE -ne 0) {
        throw 'Offline installation into the workspace virtual environment failed.'
    }
    $environmentInfo = Get-PythonInfo $environmentPython
    if ($environmentInfo.HostVersion -ne '0.8.3' -or $environmentInfo.NumpyVersion -ne '1.26.4') {
        throw 'The environment does not contain the required fixed dependency versions after installation.'
    }
}
[IO.Directory]::CreateDirectory($diagnostics) | Out-Null
Write-Host 'Running the bounded host probe without a commercial plugin...'
& $environmentPython -I $worker --probe --result $probeResult
$probeExitCode = $LASTEXITCODE
if (-not (Test-Path -LiteralPath $probeResult -PathType Leaf)) {
    throw "The host probe returned no report (exit $probeExitCode)."
}
$probe = Get-Content -LiteralPath $probeResult -Raw | ConvertFrom-Json
if ($probeExitCode -ne 0 -or $probe.Success -ne $true -or $probe.CommercialPluginsLoaded -ne $false -or
    $probe.AudioWritten -ne $false -or $probe.ProbeKind -ne 'EmptyHostDsp') {
    throw "Host probe did not pass: $($probe.Message) Report: $probeResult"
}
[pscustomobject]@{
    Success = $true
    EnvironmentPythonPath = $environmentPython
    ProbeResultPath = $probeResult
    Message = 'The isolated host is ready for a separate real-plugin validation. No commercial plugin was loaded by this setup.'
}
