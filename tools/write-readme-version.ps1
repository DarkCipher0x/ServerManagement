# Writes the version into README.md (release pipeline; docs\RELEASE-PIPELINE.md): the version badge, and a Release
# history entry for <ver> from the opening paragraph of docs\release-notes\<ver>.md when there isn't one yet. build.ps1
# runs it first, so the README is never edited by hand for a release and tools\release-check.ps1 finds it in step.
# Usage: .\tools\write-readme-version.ps1 -Version 0.5.4
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$readmePath = Join-Path $root 'README.md'
$notesPath = Join-Path $root "docs\release-notes\$Version.md"
if (-not (Test-Path $notesPath)) { throw "docs\release-notes\$Version.md is missing: write the release notes first" }
$utf8 = New-Object Text.UTF8Encoding $false
$readme = [IO.File]::ReadAllText($readmePath, $utf8)
$before = $readme

# The badge (image URL and alt text).
$readme = [regex]::Replace($readme, 'alt="Version [^"]*"', "alt=""Version $Version""")
$readme = [regex]::Replace($readme, '(src="https://img\.shields\.io/badge/version-)[^"]*?(-[0-9a-f]{6}")', "`${1}$($Version -replace '-', '--')`$2")

# The history entry: the notes' first paragraph after the title, as one sentence-ish line.
if (-not [regex]::IsMatch($readme, '\*\*' + [regex]::Escape($Version) + ':?\*\*')) {   # "**0.5.4**" or "**0.5.3:**"
    $notes = [IO.File]::ReadAllLines($notesPath, $utf8)
    $para = New-Object System.Collections.Generic.List[string]
    $started = $false
    foreach ($l in $notes | Select-Object -Skip 1) {
        if ($l.Trim().StartsWith('#')) { if ($started) { break } else { continue } }
        if ($l.Trim().Length -eq 0) { if ($started) { break } else { continue } }
        $started = $true; $para.Add($l.Trim())
    }
    $summary = ($para -join ' ') -replace '\*\*', '' -replace '`', ''
    if ($summary.Length -gt 240) { $summary = $summary.Substring(0, 237).TrimEnd() + '...' }
    if ($summary.Length -eq 0) { $summary = 'see the release notes' }
    $dash = [string][char]0x2014   # an em dash, as in the rest of the history (this script stays pure ASCII)
    $entry = "**$Version** ($summary $dash [docs/release-notes/$Version.md](docs/release-notes/$Version.md)). "
    $m = [regex]::Match($readme, '(?m)^## Release history\r?\n\r?\n')
    if (-not $m.Success) { throw "README.md has no '## Release history' section" }
    $at = $m.Index + $m.Length
    $readme = $readme.Insert($at, $entry)
}
if ($readme -ne $before) { [IO.File]::WriteAllText($readmePath, $readme, $utf8); Write-Host "README.md: version $Version written." }
else { Write-Host "README.md already shows $Version." }
