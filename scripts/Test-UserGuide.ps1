[CmdletBinding()]
param(
    [string]$GuideDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'UserGuide'),
    [switch]$RequireChm
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($GuideDirectory)
$chapters = @('index', 'quick-start', 'installation', 'synths', 'rendering', 'library', 'storage', 'appearance', 'troubleshooting')
$expected = @(foreach ($prefix in @('', 'en/')) { foreach ($chapter in $chapters) { "$prefix$chapter.md" } })
$pages = @(Get-ChildItem -LiteralPath $root -Filter '*.md' -File -Recurse)
if ($pages.Count -ne $expected.Count -or @($expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) }).Count) {
    throw 'The user guide must contain nine Chinese chapters and nine matching English chapters under en/.'
}
if (Get-ChildItem -LiteralPath $root -Filter '*.html' -File -Recurse) { throw 'HTML source pages must remain in build staging only.' }
$rendered = @{}
foreach ($page in $pages) { $rendered[$page.FullName] = (ConvertFrom-Markdown -Path $page.FullName).Html }
$links = 0
foreach ($page in $pages) {
    $html = $rendered[$page.FullName]
    if ($html -match '<script\b') { throw "Guide pages cannot contain scripts: $($page.Name)" }
    foreach ($match in [regex]::Matches($html, '(?<attribute>href|src)="(?<url>[^"]+)"')) {
        $url = [Net.WebUtility]::HtmlDecode($match.Groups['url'].Value)
        if ($url -match '^https://') {
            if ($match.Groups['attribute'].Value -eq 'src') { throw "Guide images must work offline: $url" }
            continue
        }
        if ($url -match '^[a-zA-Z][a-zA-Z0-9+.-]*:|^//') { throw "Unexpected guide URL: $url" }
        $parts = $url.Split('#', 2)
        $relative = [Uri]::UnescapeDataString($parts[0])
        $target = if ($relative) { [IO.Path]::GetFullPath((Join-Path $page.DirectoryName $relative)) } else { $page.FullName }
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Broken guide link in $($page.Name): $url" }
        if ($parts.Count -eq 2 -and $parts[1]) {
            $fragment = [regex]::Escape([Uri]::UnescapeDataString($parts[1]))
            if (-not $rendered.ContainsKey($target) -or $rendered[$target] -notmatch "id=`"$fragment`"") { throw "Missing guide anchor in $($page.Name): $url" }
        }
        $links++
    }
}
if ($RequireChm) {
    $chm = Join-Path $root 'SoundPreviewer.chm'
    if (-not (Test-Path -LiteralPath $chm) -or (Get-Item -LiteralPath $chm).Length -lt 4096) { throw 'Compiled CHM user guide is missing.' }
    $stream = [IO.File]::OpenRead($chm)
    try {
        $signature = [byte[]]::new(4)
        if ($stream.Read($signature, 0, 4) -ne 4 -or [Text.Encoding]::ASCII.GetString($signature) -ne 'ITSF') { throw 'Compiled guide is not a CHM file.' }
    } finally { $stream.Dispose() }
}
Write-Host "Markdown guide verified: $($pages.Count) chapters, $links local links and images; CHM required: $RequireChm."
