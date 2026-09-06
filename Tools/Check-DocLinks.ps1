<#
.SYNOPSIS
    Verifies every relative link and heading anchor in the project's CLAUDE.md / README / GEMINI docs.

.DESCRIPTION
    The architecture guidance is split across a root CLAUDE.md and three nested ones, which cross-link
    heavily. Splitting them once already broke a dozen links, and a link into a heading that has since
    been reworded fails silently -- markdown has no compiler. Run this after editing any of them.

    Anchors are slugified the way GitHub does it: lowercase, drop everything that is not a letter,
    digit, space or hyphen, then spaces to hyphens. That is why "### Highlighting - `MaterialPropertyBlock`"
    becomes #highlighting--materialpropertyblock.

.EXAMPLE
    powershell -File Tools/Check-DocLinks.ps1
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$docs = @(
    'CLAUDE.md'
    'GEMINI.md'
    'README.md'
    'Assets\Scripts\CLAUDE.md'
    'Assets\Scripts\Plateau\CLAUDE.md'
    'Assets\Scripts\Bash\CLAUDE.md'
)

function Get-Slug([string]$heading) {
    $s = $heading -replace '^#{1,6}\s+', ''
    $s = $s.ToLower()
    $s = $s -replace '[^a-z0-9 \-]', ''
    return ($s -replace ' ', '-')
}

$broken = 0
$checked = 0

foreach ($doc in $docs) {
    $path = Join-Path $root $doc
    if (-not (Test-Path $path)) {
        Write-Host "MISSING DOC   $doc" -ForegroundColor Red
        $broken++
        continue
    }

    $dir = Split-Path $path -Parent
    $text = [System.IO.File]::ReadAllText($path)

    foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^https?:') { continue }

        $checked++
        $parts = $target -split '#', 2
        $targetFile = if ($parts[0] -eq '') { $path } else { Join-Path $dir $parts[0] }

        if (-not (Test-Path $targetFile)) {
            Write-Host ("BROKEN FILE   {0} -> {1}" -f $doc, $target) -ForegroundColor Red
            $broken++
            continue
        }

        if ($parts.Count -eq 2) {
            $anchors = @(
                [System.IO.File]::ReadAllLines($targetFile) |
                    Where-Object { $_ -match '^#{1,6}\s' } |
                    ForEach-Object { Get-Slug $_ }
            )
            if ($anchors -notcontains $parts[1]) {
                Write-Host ("BROKEN ANCHOR {0} -> {1}" -f $doc, $target) -ForegroundColor Red
                $broken++
            }
        }
    }
}

if ($broken -eq 0) {
    Write-Host "OK - $checked relative link(s) across $($docs.Count) documents all resolve." -ForegroundColor Green
    exit 0
}

Write-Host "$broken broken link(s) out of $checked checked." -ForegroundColor Red
exit 1
