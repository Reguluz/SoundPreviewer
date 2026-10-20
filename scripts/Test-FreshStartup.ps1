[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'osx-arm64', 'osx-x64')][string]$Runtime,
    [string]$ApplicationDirectory
)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifacts = Join-Path $projectDirectory 'artifacts'
$published = if ($ApplicationDirectory) { [IO.Path]::GetFullPath($ApplicationDirectory) } else { Join-Path $artifacts "publish-$Runtime" }
$executable = if ($Runtime -eq 'win-x64') { 'SoundPreviewer.exe' } else { 'SoundPreviewer' }
if (-not (Test-Path -LiteralPath (Join-Path $published $executable) -PathType Leaf)) { throw 'Publish the application first.' }
# A fresh-start check must never copy a real library or personal settings into uploaded diagnostics.
$userData = Get-ChildItem -LiteralPath $published -Recurse -Force | Where-Object {
    $_.Name -in @('Preview', 'local-data', 'directories.json', 'library-location.json', 'preset-categories.json') -or
    $_.Extension -in @('.db', '.db-wal', '.db-shm', '.wav', '.log')
} | Select-Object -First 1
if ($userData) { throw "The application source contains user data; refusing a fresh-start copy: $($userData.FullName)" }

$diagnostics = [IO.Path]::GetFullPath((Join-Path $artifacts "fresh-startup-smoke-$Runtime-$([Guid]::NewGuid().ToString('N'))"))
$application = Join-Path $diagnostics 'application'
$preview = Join-Path $application 'Preview'
$config = Join-Path $application 'Config'
$emptyUserData = Join-Path $diagnostics 'empty-user-data'
$backups = Join-Path $diagnostics 'preserved'
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$preservedHashes = @{}
$results = @()
$phase = 'prepare'

function Assert-PrivatePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($diagnostics + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
        throw "Fresh-start operations must remain beneath this run's diagnostics: $full"
    }
    $ancestor = $full
    while ($ancestor.StartsWith($diagnostics, $pathComparison)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a linked fresh-start target: $ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $full
}

function Preserve-Directory([string]$Path, [string]$Name) {
    $source = Assert-PrivatePath $Path
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { return }
    $target = Assert-PrivatePath (Join-Path $backups $Name)
    if (Test-Path -LiteralPath $target) { throw "A preservation directory already exists: $target" }
    $files = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
    if ($files | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw "Refusing to preserve a linked fixture directory: $source"
    }
    foreach ($file in $files | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $saved = Assert-PrivatePath (Join-Path $target $relative)
        $preservedHashes[$saved] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    # Keep every fixture directory; no recursive delete is used by this check.
    Move-Item -LiteralPath $source -Destination $target
}

function Assert-PreservedFiles {
    foreach ($file in $preservedHashes.Keys) {
        [void](Assert-PrivatePath $file)
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $preservedHashes[$file]) {
            throw "A preserved fresh-start fixture changed: $file"
        }
    }
}

foreach ($parent in @($projectDirectory, $artifacts)) {
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing a linked diagnostics parent: $parent"
    }
}
if (Test-Path -LiteralPath $diagnostics) { throw 'Fresh-start diagnostics must use a new run directory.' }
New-Item -ItemType Directory -Path $application, $emptyUserData, $backups | Out-Null
Get-ChildItem -LiteralPath $published -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $application -Recurse }

try {
    foreach ($phase in @('both-missing', 'config-missing', 'preview-missing')) {
        if ($phase -eq 'both-missing') {
            Preserve-Directory $config 'both-missing-Config'
            if (Test-Path -LiteralPath $preview) { throw 'The clean application copy unexpectedly contains Preview.' }
        }
        elseif ($phase -eq 'config-missing') {
            Preserve-Directory $config 'config-missing-Config'
            # Prevent a stale successful smoke result from satisfying this launch.
            $previousResult = Assert-PrivatePath (Join-Path $preview 'ui-smoke.json')
            if (Test-Path -LiteralPath $previousResult -PathType Leaf) {
                Move-Item -LiteralPath $previousResult -Destination (Assert-PrivatePath (Join-Path $backups 'both-missing-ui-smoke.json'))
            }
        }
        else {
            $directoryFile = Assert-PrivatePath (Join-Path $config 'directories.json')
            $directories = Get-Content -LiteralPath $directoryFile -Raw | ConvertFrom-Json
            $directories | Add-Member -NotePropertyName TestOnlyFreshStartupField -NotePropertyValue 'preserve-when-preview-is-missing'
            $directories | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $directoryFile -Encoding utf8
            $categoryFile = Assert-PrivatePath (Join-Path $config 'preset-categories.json')
            $rules = Get-Content -LiteralPath $categoryFile -Raw | ConvertFrom-Json
            $rules.categories = @($rules.categories) + @([pscustomobject]@{
                label = 'TEST ONLY fresh startup'; tokens = @('testonlyfreshstartup'); prefixes = @()
            })
            # Only edit the personal rules fixture; the bundled default is retained unchanged.
            $rules | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $categoryFile -Encoding utf8
            $customCategoryHash = (Get-FileHash -LiteralPath $categoryFile -Algorithm SHA256).Hash
            $locator = Assert-PrivatePath (Join-Path $config 'library-location.json')
            [pscustomobject]@{
                FormatVersion = 2; DataDirectory = '../Preview'; SavedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            } | ConvertTo-Json | Set-Content -LiteralPath $locator -Encoding utf8
            Preserve-Directory $preview 'preview-missing-Preview'
        }

        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $application $executable))
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('--test-installed-storage', '--test-user-data-directory', $emptyUserData)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $timedOut = -not $process.WaitForExit(120000)
            if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
            [IO.File]::WriteAllText((Join-Path $diagnostics "$phase-stdout.txt"), $stdout.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $diagnostics "$phase-stderr.txt"), $stderr.GetAwaiter().GetResult())
            if ($timedOut) { throw "Fresh-start $phase timed out: $diagnostics" }
            if ($process.ExitCode -ne 0) { throw "Fresh-start $phase failed with exit $($process.ExitCode): $diagnostics" }
        }
        finally { $process.Dispose() }

        $smokePath = Join-Path $preview 'ui-smoke.json'
        $result = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
        $directoryFile = Join-Path $config 'directories.json'
        $categoryFile = Join-Path $config 'preset-categories.json'
        if (-not $result.Loaded -or -not $result.InstalledStorageChecked -or -not $result.CategoryRulesAvailableChecked -or
            -not $result.DirectoryConfigurationChecked -or $result.Presets -ne 0 -or $result.StoredPreviewRecords -ne 0 -or
            -not [IO.Path]::GetFullPath($result.LibraryDirectory).Equals([IO.Path]::GetFullPath($preview), $pathComparison) -or
            -not [IO.Path]::GetFullPath($result.PreviewDirectory).Equals([IO.Path]::GetFullPath($preview), $pathComparison) -or
            -not [IO.Path]::GetFullPath($result.DirectoryConfigurationPath).Equals([IO.Path]::GetFullPath($directoryFile), $pathComparison) -or
            -not (Test-Path -LiteralPath $directoryFile -PathType Leaf) -or -not (Test-Path -LiteralPath $categoryFile -PathType Leaf)) {
            throw "Fresh-start $phase did not recreate an empty installation-local Preview and valid Config: $diagnostics"
        }
        $directories = Get-Content -LiteralPath $directoryFile -Raw | ConvertFrom-Json
        $rules = Get-Content -LiteralPath $categoryFile -Raw | ConvertFrom-Json
        if ($directories.FormatVersion -ne 1 -or $null -eq $directories.Sources -or $null -eq $directories.PluginDirectories -or
            $rules.version -ne 1 -or @($rules.categories).Count -lt 1) {
            throw "Fresh-start $phase did not write readable directory settings and personal classification rules."
        }
        if ($phase -eq 'preview-missing') {
            if ($directories.TestOnlyFreshStartupField -ne 'preserve-when-preview-is-missing' -or
                (Get-FileHash -LiteralPath $categoryFile -Algorithm SHA256).Hash -ne $customCategoryHash -or
                @($rules.categories | Where-Object { $_.label -eq 'TEST ONLY fresh startup' }).Count -ne 1) {
                throw 'Recreating Preview overwrote existing directory configuration or custom category rules.'
            }
            $missingWarnings = @($result.StartupWarnings | Where-Object {
                $_.Contains($preview, $pathComparison) -and $_ -match '不可用|不存在|missing|unavailable'
            })
            if ($missingWarnings.Count -eq 0) { throw 'The missing remembered Preview did not produce an explicit startup warning.' }
        }
        Assert-PreservedFiles
        Copy-Item -LiteralPath $smokePath -Destination (Join-Path $diagnostics "$phase-ui-smoke.json")
        $results += [pscustomobject]@{
            Phase = $phase; ExitCode = 0; Presets = $result.Presets; PreviewDirectory = $preview; ConfigurationDirectory = $config
            CategoryRulesAvailable = $true; OriginalFilesPreserved = $true; StartupWarnings = @($result.StartupWarnings)
            ExistingCustomConfigurationPreserved = ($phase -eq 'preview-missing')
        }
    }
}
catch {
    [pscustomobject]@{ Phase = $phase; Error = $_.Exception.ToString() } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $diagnostics 'failure.json') -Encoding utf8
    throw
}
finally {
    $preservedHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $diagnostics 'preserved-data-hashes.json') -Encoding utf8
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $diagnostics 'result.json') -Encoding utf8
}
$results | ConvertTo-Json -Depth 8
