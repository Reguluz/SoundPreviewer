[CmdletBinding()]
param(
    [string]$PythonPath,
    [string]$ApplicationPath,
    [string]$DataDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$EnvironmentName = 'dawdreamer-cpython-x64',
    [AllowEmptyString()]
    [string]$Proxy,
    [switch]$IncludeSerum2,
    [string]$ResultPath,
    [switch]$Offline,
    [string]$UvPath
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
    $DataDirectory = Join-Path $application 'Preview'
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
    'Version': platform.python_version(),
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

function Get-Serum2Requirements {
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
    return $serum2Requirements
}

function Invoke-PortableSetup {
    $uvVersion = '0.13.4'
    $pythonVersion = '3.12.15'
    $runtimeName = 'dawdreamer-portable-win-x64'
    $uvArchiveHash = 'ccec7508a07d9933d7cb1261880633efcf1f30d6e6eddab21823f1dae6f23225'
    $uvExecutableHash = 'b9d9656370ee1812e53e985d8bbb81870038106df98ef34cb45bcf4e0fe0df17'
    $renderers = Assert-DataPath (Join-Path $data 'renderers')
    $runtime = Assert-DataPath (Join-Path $renderers $runtimeName)
    $runtimePython = Join-Path $runtime 'python.exe'
    $manifest = Join-Path $runtime 'runtime.json'
    $diagnostics = Assert-DataPath (Join-Path $data 'diagnostics')
    $probeResult = Assert-DataPath (Join-Path $diagnostics 'host-probe-portable-win-x64.json')
    if ($ResultPath) { $resolvedResult = Assert-DataPath $ResultPath } else { $resolvedResult = Join-Path $diagnostics 'renderer-setup-win-x64.json' }
    $temporary = Assert-DataPath (Join-Path $renderers 'temp')
    foreach ($directory in @($renderers, $diagnostics, $temporary)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    $previousEnvironment = @{}
    $environmentNames = @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(UV_|PIP_|PYTHON)' } | ForEach-Object { $_.Name }) +
        @('VIRTUAL_ENV', 'CONDA_PREFIX', '__PYVENV_LAUNCHER__', 'TMP', 'TEMP', 'TMPDIR', 'XDG_CACHE_HOME', 'HTTP_PROXY', 'HTTPS_PROXY')
    $tls = [Net.ServicePointManager]::SecurityProtocol
    $consoleEncoding = [Console]::OutputEncoding
    $setupLock = $null
    $staging = $null
    $replacedRuntime = $null
    $publishedRuntime = $false

    function Write-Stage([string]$Message) { Write-Host ('SP_SETUP:' + $Message) }
    function Write-SetupJson([string]$Path, $Value) {
        $Path = Assert-DataPath $Path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
        $pending = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        [IO.File]::WriteAllText($pending, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($pending, $Path, [NullString]::Value) }
        else { [IO.File]::Move($pending, $Path) }
    }
    function Quote-NativeArgument([string]$Value) {
        # Windows CommandLineToArgvW escaping, compatible with Windows PowerShell 5.1.
        if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
        return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }
    function Invoke-SetupProcess([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 900) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $Executable
        $start.WorkingDirectory = if ($application.Length -lt 240) { $application } else { [IO.Path]::GetPathRoot($application) }
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Arguments = (@($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' ')
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                try { & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null }
                catch { }
                if (-not $process.HasExited) { $process.Kill() }
                $process.WaitForExit()
                throw '生成环境准备超时，已停止当前准备进程，可稍后重试。'
            }
            $output = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) {
                $details = ($errorText + [Environment]::NewLine + $output).Trim()
                if ($details.Length -gt 4000) { $details = $details.Substring($details.Length - 4000) }
                throw ('生成环境准备失败（' + $process.ExitCode + '）：' + $details)
            }
            return $output
        }
        finally { $process.Dispose() }
    }
    function Test-PortableDependencies($Info) {
        if ($Info.Version -ne $pythonVersion -or $Info.IsVenv -or
            $Info.HostVersion -ne '0.8.3' -or $Info.NumpyVersion -ne '1.26.4') { return $false }
        return -not $IncludeSerum2 -or ($Info.Serum2ConverterVersion -eq '0.1.3' -and
            $Info.CborVersion -eq '5.6.5' -and $Info.ZstandardVersion -eq '0.23.0')
    }
    function Test-ExistingSerum2($Record) {
        try {
            return $Record.Dependencies.'serum2-preset-loader' -eq '0.1.3' -and
                $Record.Dependencies.cbor2 -eq '5.6.5' -and $Record.Dependencies.zstandard -eq '0.23.0'
        }
        catch { return $false }
    }
    function Test-PortableFiles($Record, [string]$Root) {
        try {
            if (-not ($Record.PSObject.Properties.Name -contains 'Files') -or $null -eq $Record.Files) { return $false }
            $files = @($Record.Files.PSObject.Properties)
            if ($files.Count -lt 1 -or $files.Count -gt 64 -or $files.Name -notcontains 'python.exe') { return $false }
            foreach ($file in $files) {
                if ($file.Name -match '[\\:]' -or $file.Name.StartsWith('/') -or
                    @($file.Name.Split('/')) -contains '..' -or @($file.Name.Split('/')) -contains '.' -or
                    @($file.Name.Split('/')) -contains '' -or [string]$file.Value -notmatch '^[0-9a-fA-F]{64}$') { return $false }
                $path = Assert-DataPath (Join-Path $Root $file.Name)
                if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$file.Value) { return $false }
            }
            return $true
        }
        catch { return $false }
    }
    function Install-PrivateRuntimeLibraries([string]$Destination) {
        $sourceDirectory = Join-Path $application 'Renderers/runtime-win-x64'
        $names = @('MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll')
        if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
            foreach ($name in $names) {
                $source = Join-Path $sourceDirectory $name
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('应用私有 C++ 运行库不完整：' + $name + '，请完整解压新版程序后重试。') }
                $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($source)
                if ($version.FileMajorPart -ne 14 -or $version.FileMinorPart -lt 40 -or $version.CompanyName -notlike '*Microsoft*') {
                    throw ('应用私有 C++ 运行库版本不匹配：' + $name + '，要求 Microsoft 14.40 或以上。')
                }
                $target = Join-Path $Destination $name
                $copy = -not (Test-Path -LiteralPath $target -PathType Leaf)
                if (-not $copy) {
                    $installed = [Diagnostics.FileVersionInfo]::GetVersionInfo($target)
                    $sourceVersion = [Version]::new($version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart, $version.FilePrivatePart)
                    $installedVersion = [Version]::new($installed.FileMajorPart, $installed.FileMinorPart, $installed.FileBuildPart, $installed.FilePrivatePart)
                    $copy = $sourceVersion -gt $installedVersion
                }
                if ($copy) { Copy-Item -LiteralPath $source -Destination $target -Force }
            }
        }
        foreach ($name in $names) {
            $library = Join-Path $Destination $name
            if (-not (Test-Path -LiteralPath $library -PathType Leaf)) {
                throw ('缺少应用私有 C++ 运行库 ' + $name + '，请完整解压新版程序后重试；不会安装系统组件或修改系统 DLL。')
            }
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($library)
            if ($version.FileMajorPart -ne 14 -or $version.FileMinorPart -lt 40) { throw ('私有 C++ 运行库版本过旧：' + $name) }
        }
    }
    function Invoke-PortableProbe([string]$Executable) {
        Write-Stage '检查生成环境，不加载商业音源'
        if (Test-Path -LiteralPath $probeResult) { Remove-Item -LiteralPath $probeResult -Force }
        $executionError = $null
        try { [void](Invoke-SetupProcess $Executable @('-I', '-B', $worker, '--probe', '--result', $probeResult) 30) }
        catch { $executionError = $_.Exception.Message }
        if (-not (Test-Path -LiteralPath $probeResult -PathType Leaf) -or (Get-Item -LiteralPath $probeResult).Length -gt 1MB) {
            if ($executionError) { throw $executionError }
            throw '生成环境未返回有效自检结果。'
        }
        $probe = Get-Content -LiteralPath $probeResult -Raw | ConvertFrom-Json
        if ($executionError -or $probe.Success -ne $true -or $probe.CommercialPluginsLoaded -ne $false -or
            $probe.AudioWritten -ne $false -or $probe.ProbeKind -ne 'EmptyHostDsp' -or $probe.Finished -ne $true -or
            $probe.HostVersion -ne '0.8.3' -or $probe.NumpyVersion -ne '1.26.4') {
            throw ('生成环境自检未通过：' + $probe.Message)
        }
    }
    function Write-PortableManifest([string]$Executable, [string]$ManifestPath) {
        $inventory = @'
import hashlib, importlib.metadata, json, pathlib, sys
root = pathlib.Path(sys.executable).resolve().parent
names = ('dawdreamer', 'numpy', 'serum2-preset-loader', 'cbor2', 'zstandard')
dependencies, paths = {}, {root / 'python.exe'}
for library in root.glob('*.dll'):
    paths.add(library)
for name in names:
    try:
        distribution = importlib.metadata.distribution(name)
    except importlib.metadata.PackageNotFoundError:
        continue
    dependencies[name] = distribution.version
    for entry in distribution.files or ():
        text = str(entry).replace('\\', '/')
        if text.endswith(('.pyd', '.dll')) or text.endswith('.dist-info/METADATA') or (name == 'serum2-preset-loader' and text.endswith('.py')):
            path = pathlib.Path(distribution.locate_file(entry)).resolve()
            path.relative_to(root)
            paths.add(path)
if len(paths) > 64:
    raise ValueError('Runtime fingerprint inventory exceeds 64 files.')
files = {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(paths)}
print(json.dumps({'Dependencies': dependencies, 'Files': files}))
'@
        $raw = Invoke-SetupProcess $Executable @('-I', '-B', '-c', $inventory) 30
        $record = $raw | ConvertFrom-Json
        Write-SetupJson $ManifestPath ([ordered]@{
            SchemaVersion = 1; RuntimeKind = 'uv-managed-cpython'; Platform = 'win-x64'
            PythonExecutable = 'python.exe'; PythonVersion = $pythonVersion; UvVersion = $uvVersion
            Dependencies = $record.Dependencies; Files = $record.Files
        })
    }
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($name in ($environmentNames | Select-Object -Unique)) {
            $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
        $privateEnvironment = @{
            UV_CACHE_DIR = (Join-Path $renderers 'uv-cache'); UV_PYTHON_INSTALL_DIR = (Join-Path $renderers 'python-downloads')
            UV_TOOL_DIR = (Join-Path $renderers 'tools'); UV_TOOL_BIN_DIR = (Join-Path $renderers 'tools/bin')
            UV_PYTHON_BIN_DIR = (Join-Path $renderers 'tools/python-bin'); UV_PYTHON_INSTALL_BIN = '0'
            UV_PYTHON_INSTALL_REGISTRY = '0'; UV_PYTHON_NO_REGISTRY = '1'; UV_PYTHON_PREFERENCE = 'only-managed'
            UV_NO_CONFIG = '1'; UV_SYSTEM_CERTS = '1'; UV_LINK_MODE = 'copy'
            PYTHONDONTWRITEBYTECODE = '1'; PYTHONNOUSERSITE = '1'
            TMP = $temporary; TEMP = $temporary; TMPDIR = $temporary; XDG_CACHE_HOME = (Join-Path $renderers 'cache')
        }
        foreach ($name in $privateEnvironment.Keys) {
            if (-not $previousEnvironment.ContainsKey($name)) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
            [Environment]::SetEnvironmentVariable($name, $privateEnvironment[$name], 'Process')
        }
        if ($Proxy) {
            [Environment]::SetEnvironmentVariable('HTTP_PROXY', $Proxy, 'Process')
            [Environment]::SetEnvironmentVariable('HTTPS_PROXY', $Proxy, 'Process')
        }
        $lockPath = Assert-DataPath (Join-Path $renderers '.dawdreamer-setup-win-x64.lock')
        try { $setupLock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose) }
        catch { throw '另一个生成环境准备任务正在运行，请等待它完成后再试。' }
        if (-not (Test-Path -LiteralPath $runtime)) {
            # Recover the narrow rename window if a previous process was killed
            # after retaining its old runtime but before publishing the new tree.
            $backups = @(Get-ChildItem -LiteralPath $renderers -Directory | Where-Object {
                $_.Name -match ('^' + [regex]::Escape($runtimeName) + '-before-setup-[0-9a-f]{32}$')
            } | Sort-Object LastWriteTimeUtc -Descending)
            foreach ($backup in $backups) {
                try {
                    $ownedBackup = Assert-DataPath $backup.FullName
                    $backupManifest = Join-Path $ownedBackup 'runtime.json'
                    if (-not (Test-Path -LiteralPath $backupManifest -PathType Leaf) -or (Get-Item -LiteralPath $backupManifest).Length -gt 1MB -or
                        -not (Test-Path -LiteralPath (Join-Path $ownedBackup 'python.exe') -PathType Leaf)) { continue }
                    $record = Get-Content -LiteralPath $backupManifest -Raw | ConvertFrom-Json
                    if ($record.SchemaVersion -ne 1 -or $record.RuntimeKind -ne 'uv-managed-cpython' -or
                        $record.Platform -ne 'win-x64' -or $record.PythonExecutable -ne 'python.exe' -or
                        $record.PythonVersion -ne $pythonVersion -or $record.UvVersion -ne $uvVersion -or
                        -not (Test-PortableFiles $record $ownedBackup)) { continue }
                    if (-not $IncludeSerum2 -and (Test-ExistingSerum2 $record)) {
                        $selectedRequirements = Get-Serum2Requirements
                        $IncludeSerum2 = [switch]$true
                    }
                    $backupPython = Join-Path $ownedBackup 'python.exe'
                    $backupInfo = Get-PythonInfo $backupPython
                    Assert-CompatiblePython $backupInfo
                    if (-not (Test-PortableDependencies $backupInfo)) { continue }
                    [IO.Directory]::Move($ownedBackup, $runtime)
                    Write-Stage '已恢复上次中断前保留的私有环境'
                    break
                }
                catch { continue }
            }
        }
        $ready = $false
        if (Test-Path -LiteralPath $runtime) {
            if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or (Get-Item -LiteralPath $manifest).Length -gt 1MB) {
                throw '私有环境目录已存在但没有有效记录；原目录未更改，请先保留它并使用新的程序目录重试。'
            }
            $previous = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
            if ($previous.SchemaVersion -ne 1 -or $previous.RuntimeKind -ne 'uv-managed-cpython' -or
                $previous.Platform -ne 'win-x64' -or $previous.PythonExecutable -ne 'python.exe') {
                throw '现有私有环境的身份不匹配，未覆盖原文件。'
            }
            if (-not $IncludeSerum2 -and (Test-ExistingSerum2 $previous)) {
                $selectedRequirements = Get-Serum2Requirements
                $IncludeSerum2 = [switch]$true
            }
            if (Test-Path -LiteralPath $runtimePython -PathType Leaf) {
                try {
                    $runtimeInfo = Get-PythonInfo $runtimePython
                    Assert-CompatiblePython $runtimeInfo
                    $ready = (Test-PortableDependencies $runtimeInfo) -and (Test-PortableFiles $previous $runtime)
                }
                catch { Write-Stage '已有私有环境需要重新准备，将保留原目录' }
            }
        }
        if (-not $ready) {
            Write-Stage '准备应用内的 uv 工具'
            $tools = Assert-DataPath (Join-Path $renderers 'tools/uv-0.13.4')
            $uv = if ($UvPath) { [IO.Path]::GetFullPath($UvPath) } else { Join-Path $application 'Renderers/uv/uv.exe' }
            if (-not (Test-Path -LiteralPath $uv -PathType Leaf)) { $uv = Join-Path $tools 'uv.exe' }
            if (-not (Test-Path -LiteralPath $uv -PathType Leaf)) {
                if ($Offline) { throw '离线状态下找不到应用内的 uv；请先联网完成一次环境准备。' }
                [IO.Directory]::CreateDirectory($tools) | Out-Null
                $archivePath = Join-Path $tools 'uv-win.zip'
                $download = Join-Path $tools ('uv-' + [Guid]::NewGuid().ToString('N') + '.zip')
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $web = @{ Uri = 'https://github.com/astral-sh/uv/releases/download/0.13.4/uv-x86_64-pc-windows-msvc.zip'; OutFile = $download; UseBasicParsing = $true }
                if ($Proxy) { $web.Proxy = $Proxy }
                Invoke-WebRequest @web
                if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $uvArchiveHash) { throw 'uv 下载校验失败，未运行下载文件。' }
                if (Test-Path -LiteralPath $archivePath) { [IO.File]::Replace($download, $archivePath, [NullString]::Value) }
                else { [IO.File]::Move($download, $archivePath) }
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
                try {
                    $entry = @($archive.Entries | Where-Object { $_.FullName -eq 'uv.exe' })
                    if ($entry.Count -ne 1 -or $entry[0].Length -gt 64MB) { throw 'uv 压缩包结构无效。' }
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $uv, $false)
                }
                finally { $archive.Dispose() }
            }
            if ((Get-FileHash -LiteralPath $uv -Algorithm SHA256).Hash.ToLowerInvariant() -ne $uvExecutableHash) { throw '应用内 uv 与固定版本校验值不符，未运行。' }
            $staging = Assert-DataPath (Join-Path $renderers ('s-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
            if (Test-Path -LiteralPath $staging) { throw '临时环境目录发生冲突，请重试。' }
            [IO.Directory]::CreateDirectory($staging) | Out-Null
            $downloadDirectory = Join-Path $staging 'p'
            Write-Stage '下载独立 Python，仅保存到程序旁 Preview'
            $arguments = @('python', 'install', $pythonVersion, '--install-dir', $downloadDirectory, '--no-bin', '--no-registry', '--no-config', '--no-progress', '--color', 'never', '--system-certs')
            if ($Offline) { $arguments += '--offline' }
            [void](Invoke-SetupProcess $uv $arguments)
            $downloadedCandidate = Join-Path $downloadDirectory 'cpython-3.12.15-windows-x86_64-none'
            $candidate = Join-Path $staging 'r'
            [IO.Directory]::Move($downloadedCandidate, $candidate)
            $candidatePython = Join-Path $candidate 'python.exe'
            if (-not (Test-Path -LiteralPath $candidatePython -PathType Leaf)) { throw '独立 Python 的文件结构与固定版本不符。' }
            Install-PrivateRuntimeLibraries $candidate
            Write-Stage '准备固定版本的音源渲染依赖'
            # uv marks its downloaded base EXTERNALLY-MANAGED. This exception is
            # confined to our freshly created private candidate, never system Python.
            $arguments = @('pip', 'install', '--python', $candidatePython, '--system', '--break-system-packages', '--no-python-downloads', '--no-config', '--no-progress', '--color', 'never', '--system-certs',
                '--only-binary', ':all:', '--index-url', 'https://pypi.org/simple', '--link-mode', 'copy', '-r', $selectedRequirements)
            if ($Offline) { $arguments += '--offline' }
            [void](Invoke-SetupProcess $uv $arguments)
            $candidateInfo = Get-PythonInfo $candidatePython
            Assert-CompatiblePython $candidateInfo
            if (-not (Test-PortableDependencies $candidateInfo)) { throw '独立环境的依赖版本不符合固定配置。' }
            Invoke-PortableProbe $candidatePython
            Write-PortableManifest $candidatePython (Join-Path $candidate 'runtime.json')
            if (Test-Path -LiteralPath $runtime) {
                $replacedRuntime = Assert-DataPath ($runtime + '-before-setup-' + [Guid]::NewGuid().ToString('N'))
                [IO.Directory]::Move($runtime, $replacedRuntime)
            }
            [IO.Directory]::Move($candidate, $runtime)
            $publishedRuntime = $true
        }
        Write-Stage '确认移动后的独立环境可用'
        $finalInfo = Get-PythonInfo $runtimePython
        Assert-CompatiblePython $finalInfo
        if (-not (Test-PortableDependencies $finalInfo)) { throw '独立环境移动后校验失败。' }
        Invoke-PortableProbe $runtimePython
        Write-PortableManifest $runtimePython $manifest
        $result = [ordered]@{
            Success = $true; EnvironmentPythonPath = $runtimePython; DataDirectory = $data
            ProbeResultPath = $probeResult; IncludeSerum2 = [bool]$IncludeSerum2
            ManagedRuntime = $true; RuntimeKind = 'uv-managed-cpython'
            Message = '私有生成环境已准备完成；无需系统 Python，音源及音色兼容性仍须另行验证。'
        }
        Write-SetupJson $resolvedResult $result
        Write-Stage '私有生成环境已就绪'
        [pscustomobject]$result
    }
    catch {
        if ($publishedRuntime -and (Test-Path -LiteralPath $runtime)) {
            $failedRuntime = Assert-DataPath ($runtime + '-failed-' + [Guid]::NewGuid().ToString('N'))
            [IO.Directory]::Move($runtime, $failedRuntime)
        }
        if ($replacedRuntime -and (Test-Path -LiteralPath $replacedRuntime) -and -not (Test-Path -LiteralPath $runtime)) {
            [IO.Directory]::Move($replacedRuntime, $runtime)
        }
        Write-SetupJson $resolvedResult ([ordered]@{ Success = $false; Message = $_.Exception.Message; ManagedRuntime = $true; RuntimeKind = 'uv-managed-cpython' })
        throw
    }
    finally {
        if ($setupLock) { $setupLock.Dispose() }
        if ($staging -and (Test-Path -LiteralPath $staging)) {
            $owned = Assert-DataPath $staging
            if ([IO.Path]::GetDirectoryName($owned) -eq $renderers -and [IO.Path]::GetFileName($owned) -match '^s-[0-9a-f]{8}$') {
                Remove-Item -LiteralPath $owned -Recurse -Force
            }
        }
        foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
        [Net.ServicePointManager]::SecurityProtocol = $tls
        [Console]::OutputEncoding = $consoleEncoding
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'This renderer setup script supports Windows only.'
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
if ($IncludeSerum2) { $selectedRequirements = Get-Serum2Requirements }
if (-not $PythonPath) {
    Invoke-PortableSetup
    return
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
    if (-not $Offline) {
        Write-Host 'Downloading fixed binary wheels from the official Python package index...'
        & $environmentPython @downloadArguments
        if ($LASTEXITCODE -ne 0) {
            throw 'Official wheel download failed. Cached wheels remain available; no package installation was attempted.'
        }
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
$legacyResult = [pscustomobject]@{
    Success = $true
    EnvironmentPythonPath = $environmentPython
    DataDirectory = $data
    ProbeResultPath = $probeResult
    IncludeSerum2 = [bool]$IncludeSerum2
    Message = 'The isolated host is ready for a separate real-plugin validation. No commercial plugin was loaded by this setup.'
}
if ($ResultPath) {
    $resultFile = Assert-DataPath $ResultPath
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resultFile)) | Out-Null
    [IO.File]::WriteAllText($resultFile, ($legacyResult | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}
$legacyResult
