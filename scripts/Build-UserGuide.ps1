[CmdletBinding()]
param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'CHM compilation requires Windows.' }
$workspace = Split-Path $PSScriptRoot -Parent
$source = Join-Path $workspace 'UserGuide'
& (Join-Path $PSScriptRoot 'Test-UserGuide.ps1') -GuideDirectory $source
if (-not $CompilerPath) { $CompilerPath = & (Join-Path $PSScriptRoot 'Fetch-HelpCompiler.ps1') }
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$outputDirectory = Join-Path $workspace 'artifacts/user-guide'
$build = Join-Path $workspace ('artifacts/user-guide-build/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $build, $outputDirectory -Force | Out-Null
$topics = @('index', 'quick-start', 'installation', 'synths', 'rendering', 'library', 'storage', 'appearance', 'troubleshooting')
$languages = @(@{ Prefix = ''; Name = '简体中文（默认）' }, @{ Prefix = 'en/'; Name = 'English' })
$chapters = @(foreach ($language in $languages) { foreach ($topic in $topics) { $language.Prefix + $topic } })
$titles = @{}
$files = [Collections.Generic.List[string]]::new()
foreach ($chapter in $chapters) {
    $markdown = [IO.File]::ReadAllText((Join-Path $source "$chapter.md"))
    $titles[$chapter] = [regex]::Match($markdown, '(?m)^# (.+)$').Groups[1].Value.Trim()
    $body = (ConvertFrom-Markdown -InputObject $markdown).Html
    $body = [regex]::Replace($body, 'href="([^":]*?)\.md(?=["#])', 'href="$1.html')
    $title = [Net.WebUtility]::HtmlEncode($titles[$chapter])
    $htmlLanguage = if ($chapter.StartsWith('en/')) { 'en' } else { 'zh-CN' }
    $html = @"
<!doctype html>
<html lang="$htmlLanguage"><head><meta http-equiv="Content-Type" content="text/html; charset=utf-8">
<title>$title - SoundPreviewer</title>
<style>body{font-family:Microsoft YaHei,Arial,sans-serif;font-size:15px;line-height:1.8;padding:20px;color:#222;background:white}a{color:#166b85}img{max-width:100%;height:auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:7px;text-align:left}code{background:#f3f3f3}h2{margin-top:28px;border-bottom:1px solid #ddd}pre{white-space:pre-wrap}</style>
</head><body>$body</body></html>
"@
    $htmlPath = Join-Path $build "$chapter.html"
    New-Item -ItemType Directory -Path (Split-Path $htmlPath -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))
    $files.Add("$chapter.html")
}
Copy-Item -LiteralPath (Join-Path $source 'images') -Destination $build -Recurse
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $build 'images') -File) { $files.Add('images/' + $file.Name) }
$toc = '<!doctype html><html><head><meta http-equiv="Content-Type" content="text/html; charset=gb2312"></head><body><ul>'
foreach ($language in $languages) {
    $toc += '<li><object type="text/sitemap"><param name="Name" value="' + $language.Name + '"><param name="Local" value="' + $language.Prefix + 'index.html"></object><ul>'
    foreach ($topic in $topics) {
        $chapter = $language.Prefix + $topic
        $toc += '<li><object type="text/sitemap"><param name="Name" value="' + [Net.WebUtility]::HtmlEncode($titles[$chapter]) + '"><param name="Local" value="' + $chapter + '.html"></object></li>'
    }
    $toc += '</ul></li>'
}
$toc += '</ul></body></html>'
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$chinese = [Text.Encoding]::GetEncoding(936)
[IO.File]::WriteAllText((Join-Path $build 'contents.hhc'), $toc, $chinese)
$project = @"
[OPTIONS]
Compatibility=1.1 or later
Compiled file=SoundPreviewer.chm
Contents file=contents.hhc
Default topic=index.html
Display compile progress=No
Full-text search=No
Language=0x804 Chinese (PRC)
Title=SoundPreviewer User Guide

[FILES]
$($files -join "`r`n")
"@
[IO.File]::WriteAllText((Join-Path $build 'guide.hhp'), $project, $chinese)
$process = Start-Process -FilePath $CompilerPath -ArgumentList 'guide.hhp' -WorkingDirectory $build -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $build 'compiler-stdout.txt') -RedirectStandardError (Join-Path $build 'compiler-stderr.txt')
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'CHM compiler timed out.' }
# HHC returns 1 on success. Validate the output rather than assuming Unix exit semantics.
$compiled = Join-Path $build 'SoundPreviewer.chm'
if ($process.ExitCode -notin @(0, 1) -or -not (Test-Path -LiteralPath $compiled)) { throw "CHM compilation failed; see $build" }
# hh.exe interprets '#' in source paths as a topic fragment. Verify from a
# private temporary directory so repositories such as Projects/C#/ also work.
$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('SoundPreviewer-CHM-' + [Guid]::NewGuid().ToString('N'))
$verify = Join-Path $verificationRoot 'extracted'
New-Item -ItemType Directory -Path $verify -Force | Out-Null
$verificationChm = Join-Path $verificationRoot 'SoundPreviewer.chm'
Copy-Item -LiteralPath $compiled -Destination $verificationChm
$decompile = Start-Process -FilePath (Join-Path $env:WINDIR 'hh.exe') -ArgumentList @('-decompile', 'extracted', 'SoundPreviewer.chm') -WorkingDirectory $verificationRoot -WindowStyle Hidden -PassThru
if (-not $decompile.WaitForExit(15000)) { $decompile.Kill(); throw 'CHM verification extraction timed out.' }
foreach ($name in $files) {
    $extracted = Join-Path $verify $name
    if (-not (Test-Path -LiteralPath $extracted) -or (Get-FileHash -LiteralPath $extracted).Hash -ne (Get-FileHash -LiteralPath (Join-Path $build $name)).Hash) { throw "CHM content verification failed: $name; see $build" }
}
if (-not (Test-Path -LiteralPath (Join-Path $verify 'contents.hhc'))) { throw 'CHM chapter contents are missing.' }
Copy-Item -LiteralPath $compiled -Destination (Join-Path $outputDirectory 'SoundPreviewer.chm') -Force
Write-Host "Compiled and extracted $($chapters.Count) chapters and all images: $outputDirectory/SoundPreviewer.chm"
