[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$VisualStudioDirectory
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This packaging step supplies the Windows x64 C++ runtime.' }
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifacts = Join-Path $projectDirectory 'artifacts'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Runtime packaging output must stay beneath artifacts.'
}
for ($ancestor = $output; $ancestor -ne $projectDirectory; $ancestor = Split-Path -Parent $ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing a linked runtime output: $ancestor"
    }
}
if (-not $VisualStudioDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio C++ Redistributable files are required to package the local runtime.' }
    $VisualStudioDirectory = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VisualStudioDirectory) { throw 'Could not locate the installed Visual Studio C++ tools.' }
}
$redist = Join-Path $VisualStudioDirectory 'VC/Redist/MSVC'
$candidates = foreach ($version in (Get-ChildItem -LiteralPath $redist -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+$' | Sort-Object { [Version]$_.Name } -Descending)) {
    $x64 = Join-Path $version.FullName 'x64'
    if (Test-Path -LiteralPath $x64) { Get-ChildItem -LiteralPath $x64 -Directory | Where-Object Name -Like 'Microsoft.VC*.CRT' }
}
$source = $candidates | Select-Object -First 1
if (-not $source) { throw 'No x64 release C++ Redistributable directory was found.' }
$files = foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $file = Get-Item -LiteralPath (Join-Path $source.FullName $name)
    $version = $file.VersionInfo
    if ($version.FileMajorPart -ne 14 -or $version.FileMinorPart -lt 40) { throw "The renderer requires C++ runtime 14.40 or later: $name" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft*') {
        throw "The redistributable file is not validly signed by Microsoft: $name"
    }
    [pscustomobject]@{ Name = $name; Version = $version.FileVersion; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); SourcePath = $file.FullName }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath $file.SourcePath -Destination (Join-Path $output $file.Name) -Force }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'third-party/vc-runtime/NOTICE.md') -Destination $output -Force
[ordered]@{ Source = 'Microsoft Visual Studio C++ Redistributable'; Architecture = 'x64'; Files = @($files | Select-Object Name, Version, Sha256) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
Write-Host 'Prepared the signed application-local C++ runtime.'
