[CmdletBinding()]
param([string]$GuideDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) '操作说明'))

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($GuideDirectory)
$pages = @(Get-ChildItem -LiteralPath $root -Filter '*.html' -File)
if ($pages.Count -lt 2 -or -not (Test-Path -LiteralPath (Join-Path $root 'index.html') -PathType Leaf)) {
    throw 'The offline user guide must have an index and separate topic pages.'
}
$links = 0
foreach ($page in $pages) {
    $html = [IO.File]::ReadAllText($page.FullName)
    if ($html -notmatch '<meta charset="utf-8"' -or $html -match '<script\b') {
        throw "Guide pages must be UTF-8 static HTML without scripts: $($page.Name)"
    }
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
            -not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Broken or non-local guide link in $($page.Name): $url" }
        if ($parts.Count -eq 2 -and $parts[1]) {
            $fragment = [regex]::Escape([Uri]::UnescapeDataString($parts[1]))
            if ([IO.File]::ReadAllText($target) -notmatch "id=`"$fragment`"") { throw "Missing guide anchor in $($page.Name): $url" }
        }
        $links++
    }
}
Write-Host "Offline user guide verified: $($pages.Count) pages, $links local links and images."
