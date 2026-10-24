# Writes the sanitised public source archive of one release (0.4.0 rule: every dist\<ver>\ carries the source it was built
# from, fit for publication):
#   dist\<ver>\DarkCipherServerManagement-<ver>-source.zip
# What goes in: every file git tracks, minus the private working notes (docs\history\), release output, local tool caches
# and anything .gitignore'd. Before zipping, every text file is scanned for things that must never be published: local
# user paths, private e-mail addresses, and the terms listed in tools\privacy-terms.local.txt (one per line; that file is
# ignored by git and never leaves this PC). A hit fails the build with the file and line. Usage: .\tools\write-public-source.ps1 -Version 0.4.0
param([Parameter(Mandatory)][string]$Version, [switch]$NoChecksums)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$git = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $git)) { $git = 'git' }
$dist = Join-Path $root "dist\$Version"
New-Item -ItemType Directory -Force $dist | Out-Null

# 1. The file list: tracked files (committed or staged) — the repository is the source of truth for what is public.
$files = & $git ls-files --cached --others --exclude-standard | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) }
if ($LASTEXITCODE) { throw 'git ls-files failed' }
# Release pipeline: an ALLOWLIST decides what is public (tools\public-allowlist.txt). Every listed file must match an
# allowed line or a private (!) line; a file that matches neither stops the build, so nothing new is published by default.
. (Join-Path $PSScriptRoot 'release-common.ps1')
$list = Get-PublicAllowlist
$classes = $files | ForEach-Object { [pscustomobject]@{ Path = $_; Class = (Get-PublicClass $_ $list) } }
$unlisted = @($classes | Where-Object Class -eq 'unlisted' | ForEach-Object Path)
if ($unlisted.Count -gt 0) {
    $unlisted | Select-Object -First 40 | ForEach-Object { Write-Host "  unlisted: $_" -ForegroundColor Red }
    throw "$($unlisted.Count) file(s) are neither allowed nor private in tools\public-allowlist.txt (above): decide, add a line, rebuild"
}
$files = @($classes | Where-Object Class -eq 'allow' | ForEach-Object Path)
if ($files.Count -lt 100) { throw "only $($files.Count) files listed - is this the repository root?" }

# 2. The privacy scan. Generic rules first; the local term list adds names that must not appear (kept out of the repo).
# Keys, tokens and user paths in the self-test are synthetic fixtures (documented example keys, made-up names), so those
# three rules skip SelfTest\ — the term list still applies there. Private LAN ranges (192.168.x.x) are not identifying
# and appear in examples throughout; they are not a rule. E-mail: documentation TLDs, .local / .lan lab names, service
# accounts of fakes, protocol extension names (…@openssh.com) and hostile webhook URLs with user info on Microsoft's
# Workflows host (…@x.logic.azure.com, 0.5.0 pentest fixtures) are allowed.
$rules = @(
    @{ Name = 'a local user path'; Rx = '(?i)[A-Z]:\\Users\\(?!Public\\|demo\\|<)[^\\\s"'']+'; SkipTests = $true },
    @{ Name = 'a private e-mail address'; Rx = '(?i)\b[A-Z0-9._%+-]+@(?!example\.|[a-z0-9.-]*\.(test|example|invalid|local|lan)\b|darkciphersystems\.com|anthropic\.com|users\.noreply\.github\.com|[a-z0-9.-]*gserviceaccount\.com|openssh\.com|[a-z0-9.-]*logic\.azure\.com)[A-Z0-9.-]+\.[A-Z]{2,}\b'; SkipTests = $false },
    @{ Name = 'an API key or token'; Rx = '\b(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|glsa_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\b'; SkipTests = $true },
    @{ Name = 'a private key'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----'; SkipTests = $true }
)
$termsFile = Join-Path $PSScriptRoot 'privacy-terms.local.txt'
$terms = @()
if (Test-Path $termsFile) { $terms = Get-Content $termsFile -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() } }
else { Write-Warning "tools\privacy-terms.local.txt not found: only the generic privacy rules ran" }
# A term matches as a whole word (so a name doesn't match an ordinary word that contains it); a term with its own
# punctuation (a domain, a path) matches anywhere.
$termRx = $terms | ForEach-Object { if ($_ -match '^\w+$') { '(?i)\b' + [regex]::Escape($_) + '\b' } else { '(?i)' + [regex]::Escape($_) } }
$textExt = '.cs', '.xaml', '.ps1', '.md', '.json', '.wxs', '.html', '.js', '.css', '.txt', '.props', '.csproj', '.sln', '.xml', '.yml', '.yaml', '.config', '.gitignore', '.editorconfig', '.cmd', '.bat', '.sh', '.resx'
$hits = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $ext = [IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($ext -notin $textExt -and $ext -ne '') { continue }
    $isTest = $f -match '/SelfTest/'
    $lines = [IO.File]::ReadAllLines((Join-Path $root $f))
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        foreach ($r in $rules) { if (-not ($isTest -and $r.SkipTests) -and $l -match $r.Rx) { $hits.Add("$f($($i + 1)): $($r.Name): $($Matches[0])") } }
        foreach ($t in $termRx) { if ($l -match $t) { $hits.Add("$f($($i + 1)): a private term") } }
    }
}
if ($hits.Count -gt 0) {
    $hits | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "privacy scan: $($hits.Count) line(s) must not be published (above). Fix the source; the public archive was not written."
}
# Binary files that identify a person (screenshots of a desktop, exported keys) have no place in the source: refuse them.
$binaryBad = $files | Where-Object { $_ -match '\.(pfx|p12|pem|key|kdbx|ppk)$' }
if ($binaryBad) { throw "key material in the tree: $($binaryBad -join ', ')" }

# 3. The archive, with the folder name as its top level.
$zip = Join-Path $dist "DarkCipherServerManagement-$Version-source.zip"
if (Test-Path $zip) { Remove-Item $zip }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('dcx-source-' + [guid]::NewGuid().ToString('N'))
$top = Join-Path $stage "DarkCipherServerManagement-$Version-source"
foreach ($f in $files) {
    $dest = Join-Path $top $f
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $root $f) $dest
}
# A note for the reader of the archive.
[IO.File]::WriteAllText((Join-Path $top 'SOURCE-ARCHIVE.txt'), @"
DarkCipher Systems
ServerManagement $Version - source archive

This is the source tree the $Version release was built from, as published: every tracked file except the maintainers'
working notes. Build it with .\build.ps1 (docs\BUILD.md). Licence: MIT (LICENSE). Third-party components: THIRD-PARTY-NOTICES.md.
Scanned before publication for local paths, private addresses and keys (tools\write-public-source.ps1).
"@, (New-Object Text.UTF8Encoding $false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force
Write-Host ("Source: {0} ({1} files, {2:N1} MB), privacy scan clean ({3} rule(s), {4} private term(s))" -f $zip, $files.Count, ((Get-Item $zip).Length / 1MB), $rules.Count, $terms.Count)
if (-not $NoChecksums) { & (Join-Path $PSScriptRoot 'write-checksums.ps1') -Version $Version }
