# Release pipeline step 7: the website hand-off (docs\RELEASE-PIPELINE.md). Writes dist\<ver>\website\ for the
# Website & Deployments session, in the manifest shape its sync-downloads.ps1 reads for the other manifest apps
# (AgentControl, MaskedMail, ServerDeploy):
#
#   release-manifest.json   every file: role, label, site path, bytes, SHA-256, GitHub asset URL; the mirror commit/tag;
#                           the release notes as markdown
#   downloads\servermanagement\  the Setup.exe (the main download), MSI, portable zip, User guide PDF, and update.json
#                           with its two signatures (where the app checks for updates); downloads\source\ the source zip.
#                           All byte-identical to dist\<ver>\
#                           (and so to the GitHub Release assets: publish-github-release.ps1 verified their digests)
#   SHA256SUMS.txt          for exactly those files
#   servermanagement-<ver>.json   the Discord card (generated from the release notes, not written by hand)
#
# It never deploys or posts anything: the website deploy needs DarkCipher0x's go and is the Website session's; the card is posted
# with DarkCipherSystems-Website\tools\announce-discord.ps1 after the site is live and DarkCipher0x has seen its preview.
# Before the GitHub step is done (a held release, or -DryRun) the GitHub fields are written as "pending"; run it again
# after publishing to fill them in.
# Pure ASCII (Windows PowerShell 5.1 reads scripts without a BOM as the ANSI code page).
#
# Usage: .\tools\website-handoff.ps1 -Version 0.6.9 [-DryRun]
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$cfg = Get-ReleaseConfig
$dist = Join-Path $ReleaseRoot "dist\$Version"
if (-not (Test-Path $dist)) { throw "dist\$Version doesn't exist: build it first" }
$utf8 = New-Object Text.UTF8Encoding $false
$repo = ($cfg.publicRepo -replace '^https://github.com/', '') -replace '\.git$', ''
$tag = "v$Version"

# 0.6.22: every ServerManagement file sits in downloads/servermanagement/, beside update.json and its two signatures, because
# the app checks https://darkciphersystems.com/downloads/servermanagement/update.json (Updater.DefaultEndpoint) and the
# manifest names its packages relative to itself. The branded setup program is the main download (DarkCipher0x, 2026-10-24).
$sm = 'downloads/servermanagement'
$roles = @(
    @{ File = "DarkCipherServerManagement-$Version-Setup.exe";        Role = 'installer'; Label = 'Download for Windows (setup program)'; Path = $sm },
    @{ File = "DarkCipherServerManagement-$Version-x64.msi";          Role = 'msi';       Label = 'MSI (silent / managed deployment)';  Path = $sm },
    @{ File = "DarkCipherServerManagement-$Version-x64-portable.zip"; Role = 'portable';  Label = 'Portable (.zip, no install)';        Path = $sm },
    @{ File = "DarkCipherServerManagement-$Version-User-Guide.pdf";   Role = 'guide';     Label = 'User guide (.pdf)';                  Path = $sm },
    @{ File = "DarkCipherServerManagement-$Version-source.zip";       Role = 'source';    Label = 'Source code (.zip)';                 Path = 'downloads/source' },
    # The in-app updates: not download links, but they must be on the site next to the packages update.json names.
    @{ File = 'update.json';        Role = 'update-manifest';  Label = 'Update information (signed)';     Path = $sm },
    @{ File = 'update.json.sig';    Role = 'update-signature'; Label = 'Update signature (ECDSA P-256)';   Path = $sm },
    @{ File = 'update.json.pq.sig'; Role = 'update-signature'; Label = 'Update signature (ML-DSA-65)';     Path = $sm }
)
foreach ($r in $roles) { if (-not (Test-Path (Join-Path $dist $r.File))) { throw "missing dist\$Version\$($r.File)" } }
# Add-ons are never published (DarkCipher0x, 2026-10-24): the hand-off holds only the files above, never dist\addons\.
foreach ($r in $roles) { if ($r.File -match '(?i)addon|\.dcxaddon') { throw "$($r.File) looks like an add-on: add-ons are never published" } }

# --- GitHub: filled in once the release is published ------------------------------------------------------------------
$published = Test-ReleaseStepDone $Version 'github-release'
$commit = 'pending'
if ($published) {
    $commit = (& $ReleaseGit -C $cfg.CloneFull rev-parse "$tag^{commit}").Trim()
    if ($commit -notmatch '^[0-9a-f]{40}$') { throw "tag $tag isn't in the public clone $($cfg.CloneFull)" }
}
$releaseUrl = if ($published) { "https://github.com/$repo/releases/tag/$tag" } else { 'pending' }

# --- the hand-off folder (rebuilt from scratch each run) -----------------------------------------------------------------
$out = if ($DryRun) { Join-Path ([IO.Path]::GetTempPath()) "dcx-website-dryrun-$Version" } else { Join-Path $dist 'website' }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $out 'downloads') | Out-Null

$files = foreach ($r in $roles) {
    $src = Join-Path $dist $r.File
    $dir = Join-Path $out ($r.Path -replace '/', '\')
    New-Item -ItemType Directory -Force $dir | Out-Null
    $dst = Join-Path $dir $r.File
    Copy-Item $src $dst
    $a = (Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant()
    $b = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($a -ne $b) { throw "copy of $($r.File) differs" }
    # The build's own .sha256 must agree too (release-check.ps1 checked it; a later change to dist\ would show here).
    $side = Join-Path $dist "$($r.File).sha256"
    if ((Test-Path $side) -and -not ([IO.File]::ReadAllText($side)).ToLowerInvariant().Contains($a)) { throw "$($r.File) no longer matches its .sha256" }
    [ordered]@{
        file = $r.File; role = $r.Role; label = $r.Label
        site_path = "$($r.Path)/$($r.File)"
        bytes = (Get-Item $src).Length; sha256 = $a
        github_asset = $(if ($published) { "https://github.com/$repo/releases/download/$tag/$($r.File)" } else { 'pending' })
    }
}
$sums = (($files | ForEach-Object { "$($_.sha256)  $($_.file)" }) -join "`n") + "`n"
[IO.File]::WriteAllBytes((Join-Path $out 'SHA256SUMS.txt'), [Text.Encoding]::ASCII.GetBytes($sums))

$notesPath = Join-Path $ReleaseRoot "docs\release-notes\$Version.md"
if (-not (Test-Path $notesPath)) { throw "docs\release-notes\$Version.md is missing" }
$notes = (([IO.File]::ReadAllLines($notesPath, $utf8) | Select-Object -Skip 1) -join "`n").Trim()
[IO.File]::WriteAllText((Join-Path $out "release-notes-$Version.md"), $notes + "`n", $utf8)

$manifest = [ordered]@{
    app = 'servermanagement'; version = $Version; released = (Get-Date).ToString('yyyy-MM-dd')
    github_release = $releaseUrl
    mirror = [ordered]@{ repo = $repo; tag = $tag; commit = $commit }
    rule = "Publish exactly these files under these paths; each equals its GitHub Release asset byte for byte. The source zip is the public repository at the tag (top folder DarkCipherServerManagement-<ver>-source/)."
    files = @($files)
    notes_markdown = $notes
}
[IO.File]::WriteAllText((Join-Path $out 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)

# --- the Discord card, from the notes: the "New" bullets' bold lead-ins become the highlights ------------------------------
$highlights = New-Object System.Collections.Generic.List[object]
# Only the "New" and "Fixed" sections: the beta notes are in the card's summary already.
$featureText = (([regex]::Matches($notes, '(?ms)^## (New|Fixed)\s*\n(.*?)(?=^## |\z)') | ForEach-Object { $_.Groups[2].Value }) -join "`n")
foreach ($m in [regex]::Matches($featureText, '(?m)^- \*\*(?<t>[^*]+)\*\*\s*(?<x>[^\n]*(?:\n  [^\n-][^\n]*)*)')) {
    if ($highlights.Count -ge 5) { break }
    $text = (($m.Groups['x'].Value -replace '\s*\n\s*', ' ').Trim()).TrimStart('.', ':', ' ')
    if ($text.Length -gt 300) { $text = $text.Substring(0, 297).TrimEnd() + '...' }
    $highlights.Add([ordered]@{ title = $m.Groups['t'].Value.Trim().TrimEnd('.'); text = $text })
}
# The documentation ships with every release (DarkCipher0x, 2026-10-24: note the docs in the website and social posts).
$highlights.Insert(0, [ordered]@{ title = 'Fully documented'; text = 'every release ships a user guide, an admin / business guide (Group Policy, AD / Entra sign-in, logging), a developer guide, a security report with the penetration-test results, an SBOM and Group Policy templates.' })
while ($highlights.Count -gt 5) { $highlights.RemoveAt($highlights.Count - 1) }
$card = [ordered]@{
    app = 'servermanagement'; release = 'servermanagement'; version = $Version; channel = 'servermanagement'
    title = "ServerManagement $Version public beta is out"
    content = "**ServerManagement** is out as a public beta: one encrypted Windows workstation for your servers, network, hypervisors and clouds. Built by an IT engineer, for engineers. Post-quantum ready. Free and open source (MIT)."
    summary = "Beta and not code-signed yet (check the SHA-256). Testers very welcome: many integrations haven't been run against a live account yet. Bugs and feature requests through darkciphersystems.com."
    highlights = $highlights.ToArray()   # not @($highlights): PS 5.1 throws "Argument types do not match" on a List in a literal
    notes = 'software.html#servermanagement-whats-new'
    download_page = 'software.html#servermanagement'
    links = @(
        [ordered]@{ emoji = '0x1F310'; label = 'Tour'; text = 'The full tour'; url = 'https://darkciphersystems.com/servermanagement.html' },
        [ordered]@{ emoji = '0x1F4BB'; label = 'GitHub'; text = 'Source code'; url = "https://github.com/$repo" },
        [ordered]@{ emoji = '0x1F4DA'; label = 'Docs'; text = 'Guides (PDF) and SBOM'; url = $(if ($published) { $releaseUrl } else { "https://github.com/$repo/releases" }) },
        [ordered]@{ emoji = '0x1F512'; label = 'Security'; text = 'Security report'; url = "https://github.com/$repo/blob/main/docs/SECURITY-REPORT.md" },
        [ordered]@{ emoji = '0x1F41E'; label = 'Report'; text = 'Report a bug'; url = 'https://darkciphersystems.com/report.html?app=ServerManagement' },
        [ordered]@{ emoji = '0x1F4A1'; label = 'Ideas'; text = 'Request a feature'; url = 'https://darkciphersystems.com/feature-requests.html?app=servermanagement' }
    )
    icon = 'assets/img/servermanagement.png'
    color = '#36d2cb'
    footer = 'Free & open source (MIT) - DarkCipher Systems - darkciphersystems.com'
}
[IO.File]::WriteAllText((Join-Path $out "servermanagement-$Version.json"), ($card | ConvertTo-Json -Depth 6), $utf8)

$state = if ($published) { 'ready to deploy' } else { 'GitHub fields pending (release not published yet)' }
if ($DryRun) {
    Write-Host "DRY RUN: website hand-off written to $out ($($files.Count) files; $state)" -ForegroundColor Yellow
    Write-ReleaseStep $Version 'website' 'dry-run' "hand-off preview $out"
    return
}
Write-ReleaseStep $Version 'website' $(if ($published) { 'handed-off' } else { 'prepared' }) "dist\$Version\website ($($files.Count) files; $state); deploy by the Website session with DarkCipher0x's go, then tools\verify-website.ps1"
Write-Host "Website hand-off: $out ($($files.Count) files; $state)" -ForegroundColor Green
