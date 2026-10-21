[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'osx-arm64', 'osx-x64')][string]$Runtime,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifacts = Join-Path $projectDirectory 'artifacts'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$version = '0.13.4'
$assets = @{
    'win-x64' = @{ Name = 'uv-x86_64-pc-windows-msvc.zip'; Sha256 = 'ccec7508a07d9933d7cb1261880633efcf1f30d6e6eddab21823f1dae6f23225'; Executable = 'uv.exe'; Member = 'uv.exe' }
    'osx-arm64' = @{ Name = 'uv-aarch64-apple-darwin.tar.gz'; Sha256 = 'e0b1d4743ce64c55d9f0f4a444afa6b81125680070ee04e853fcd0f6c1ebf8f1'; Executable = 'uv'; Member = 'uv-aarch64-apple-darwin/uv' }
    'osx-x64' = @{ Name = 'uv-x86_64-apple-darwin.tar.gz'; Sha256 = '7be2dcd316122f4b3e5a0a7889f6248266c14425374e105ff7dc9779184603a6'; Executable = 'uv'; Member = 'uv-x86_64-apple-darwin/uv' }
}

function Assert-ArtifactDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Tool staging must stay beneath $artifacts"
    }
    $ancestor = $full
    while ($ancestor -ne $projectDirectory) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a linked tool directory: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}

Assert-ArtifactDirectory $output
$cache = Join-Path $artifacts "packaging-tools/uv/$version"
Assert-ArtifactDirectory $cache
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$asset = $assets[$Runtime]
$archive = Join-Path $cache $asset.Name
if (Test-Path -LiteralPath $archive) {
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Sha256) {
        throw "Cached uv archive does not match the fixed official checksum: $archive"
    }
} else {
    $download = Join-Path $cache ("download-" + [Guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/astral-sh/uv/releases/download/$version/$($asset.Name)" -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Sha256) {
            throw 'Official uv download failed checksum validation.'
        }
        Move-Item -LiteralPath $download -Destination $archive
    } finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
}
$staging = Join-Path $cache ('extract-' + [Guid]::NewGuid().ToString('N'))
Assert-ArtifactDirectory $staging
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    if ($Runtime -eq 'win-x64') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $entry = $zip.GetEntry($asset.Member)
            if ($null -eq $entry) { throw 'The official archive has no expected uv executable.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $asset.Executable))
        } finally { $zip.Dispose() }
        $extracted = Join-Path $staging $asset.Executable
    } else {
        & tar -xzf $archive -C $staging $asset.Member
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the expected uv executable.' }
        $extracted = Join-Path $staging $asset.Member
        & chmod +x $extracted
        if ($LASTEXITCODE -ne 0) { throw 'Could not mark uv executable.' }
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item -LiteralPath $extracted -Destination (Join-Path $output $asset.Executable) -Force
    if ($Runtime -ne 'win-x64') {
        & chmod +x (Join-Path $output $asset.Executable)
        if ($LASTEXITCODE -ne 0) { throw 'Could not mark bundled uv executable.' }
    }
    foreach ($name in @('NOTICE.md', 'LICENSE-MIT.txt', 'LICENSE-APACHE.txt')) {
        Copy-Item -LiteralPath (Join-Path $projectDirectory "third-party/uv/$name") -Destination $output -Force
    }
    Write-Host "Prepared private uv $version for $Runtime"
} finally {
    Assert-ArtifactDirectory $staging
    Remove-Item -LiteralPath $staging -Recurse -Force
}
