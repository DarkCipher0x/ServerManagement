# Release pipeline step 6: the GitHub Release for a version whose tag publish-public.ps1 has pushed
# (docs\RELEASE-PIPELINE.md; modelled on CaseKeeper's script).
#
#   Release  "ServerManagement <ver>" on tag v<ver>
#   Notes    docs\release-notes\<ver>.md (without its title), the SmartScreen note and the SHA-256s
#   Assets   tools\release.json "releaseAssets" from dist\<ver>\ (MSI, portable zip, source, SBOM, policy templates,
#            the six PDFs) plus SHA256SUMS.txt for exactly those files. dist\ never goes into git.
#
# It is created as a DRAFT, every upload is checked against GitHub's own SHA-256 of what it stored, and only then is it
# published. A failed run leaves a draft (invisible to visitors) that the next run completes. A PUBLISHED release is
# never edited: a re-run verifies its assets' digests against dist\<ver>\ and reports "done", or stops on a mismatch.
# Sign-in: the token Git Credential Manager already holds, in memory only.
#
# Usage: .\tools\publish-github-release.ps1 -Version 0.5.3 [-DryRun] [-NotLatest]
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun, [switch]$NotLatest)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$cfg = Get-ReleaseConfig
$out = Join-Path $ReleaseRoot "dist\$Version"
$tag = "v$Version"
$api = $cfg.publicApi
$files = $cfg.releaseAssets | ForEach-Object { Join-Path $out $_.Replace('{ver}', $Version) }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "missing $f - run build.ps1 first" } }
$local = @{}
foreach ($f in $files) { $local[(Split-Path $f -Leaf)] = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant() }
$sumsText = (($files | ForEach-Object { $n = Split-Path $_ -Leaf; "$($local[$n])  $n" }) -join "`n") + "`n"
$local['SHA256SUMS.txt'] = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($sumsText))) -replace '-', '').ToLowerInvariant()

# --- notes ----------------------------------------------------------------------------------------------------------
$notesPath = Join-Path $ReleaseRoot "docs\release-notes\$Version.md"
if (-not (Test-Path $notesPath)) { throw "docs\release-notes\$Version.md is missing" }
$notes = ([IO.File]::ReadAllLines($notesPath, (New-Object Text.UTF8Encoding $false)) | Select-Object -Skip 1) -join "`n"
$fence = '```'
$body = @"
**Download** ``DarkCipherServerManagement-$Version-Setup.exe`` (the setup program: install, update, repair or uninstall), or ``DarkCipherServerManagement-$Version-x64.msi`` for silent and managed deployment (msiexec, Group Policy, Intune), or the portable zip below. The builds aren't code-signed yet, so Windows SmartScreen may show "unknown publisher": check the SHA-256 below first.

$($notes.Trim())

### SHA-256

$fence
$($sumsText.TrimEnd())
$fence
"@

$off = Get-PublishOffReason
if ($DryRun) {
    $preview = Join-Path $out 'release-preview.md'
    [IO.File]::WriteAllText($preview, "# ServerManagement $tag (draft preview)`n`n$body`n", (New-Object Text.UTF8Encoding $false))
    Write-Host "DRY RUN: would create 'ServerManagement $tag' on $api with $($files.Count + 1) assets ($([math]::Round((($files | Get-Item | Measure-Object Length -Sum).Sum) / 1MB, 1)) MB); notes preview: $preview" -ForegroundColor Cyan
    if ($off) { Write-Host "  (the kill switch is on: a real run would skip this step - $off)" -ForegroundColor Yellow }
    Write-ReleaseStep $Version 'github-release' 'dry-run' "$($files.Count + 1) assets; preview $preview"
    return
}
if ($off) { Write-ReleaseStep $Version 'github-release' 'skipped' $off; Write-Host "GitHub release skipped: $off" -ForegroundColor Yellow; return }

$h = Get-GitHubHeaders
function Get-Release {
    $page = 1
    while ($true) {
        $list = Invoke-RestMethod "$api/releases?per_page=100&page=$page" -Headers $h
        $hit = $list | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
        if ($hit -or $list.Count -lt 100) { return $hit }
        $page++
    }
}
function Test-Digests($release) {
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($n in $local.Keys) {
        $a = $release.assets | Where-Object { $_.name -eq $n } | Select-Object -First 1
        if (-not $a) { $bad.Add("$n missing"); continue }
        if ($a.digest -and $a.digest -ne "sha256:$($local[$n])") { $bad.Add("$n differs ($($a.digest))") }
    }
    $bad
}

# --- an existing release: published = verify only; draft = complete it ---------------------------------------------
$r = Get-Release
if ($r -and -not $r.draft) {
    $bad = Test-Digests $r
    if ($bad.Count -gt 0) { Write-ReleaseStep $Version 'github-release' 'failed' "published release differs: $($bad -join '; ')"; throw "GitHub release $tag is published but differs from dist\$Version`: $($bad -join '; '). It is never edited: investigate by hand." }
    Write-ReleaseStep $Version 'github-release' 'done' "$($r.html_url) (already published; $($local.Count) asset digests verified)"
    Write-Host "GitHub release $tag already published and verified: $($r.html_url)" -ForegroundColor Green
    return
}
try { Invoke-RestMethod "$api/git/ref/tags/$tag" -Headers $h | Out-Null }
catch { throw "tag $tag is not on GitHub yet - run tools\publish-public.ps1 -Version $Version first" }
if (-not $r) {
    $json = @{ tag_name = $tag; name = "ServerManagement $Version"; body = $body; draft = $true; prerelease = $false } | ConvertTo-Json
    $r = Invoke-RestMethod "$api/releases" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
    Write-Host "Draft release created: $($r.html_url)"
}

# --- upload what the draft lacks, each checked against GitHub's SHA-256 ----------------------------------------------
$up = $r.upload_url -replace '\{.*\}$', ''
$tmpSums = Join-Path ([IO.Path]::GetTempPath()) ('dcx-sums-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmpSums | Out-Null
$sumsFile = Join-Path $tmpSums 'SHA256SUMS.txt'
[IO.File]::WriteAllBytes($sumsFile, [Text.Encoding]::ASCII.GetBytes($sumsText))
try {
    foreach ($f in @($files) + $sumsFile) {
        $n = Split-Path $f -Leaf
        $have = $r.assets | Where-Object { $_.name -eq $n } | Select-Object -First 1
        if ($have -and $have.digest -eq "sha256:$($local[$n])") { continue }
        if ($have) { Invoke-RestMethod "$api/releases/assets/$($have.id)" -Method Delete -Headers $h | Out-Null }   # a draft's bad upload only
        $ct = switch -Regex ($n) { '\.txt$|\.md$' { 'text/plain' } '\.zip$' { 'application/zip' } '\.json$' { 'application/json' } '\.pdf$' { 'application/pdf' } default { 'application/octet-stream' } }
        $a = Invoke-RestMethod "$up`?name=$n" -Method Post -Headers $h -ContentType $ct -InFile $f -TimeoutSec 1800
        if ($a.digest -and $a.digest -ne "sha256:$($local[$n])") { throw "GitHub stored $n with a different SHA-256 ($($a.digest))" }
        Write-Host ("  uploaded {0} ({1:N1} MB), SHA-256 confirmed" -f $n, ($a.size / 1MB))
    }
}
catch { Write-ReleaseStep $Version 'github-release' 'failed' "draft $($r.html_url): $($_.Exception.Message)"; Write-Host "Upload failed; the draft stays (invisible to visitors). Run again to finish it." -ForegroundColor Red; throw }
finally { Remove-Item $tmpSums -Recurse -Force -ErrorAction SilentlyContinue }

# --- every digest verified: publish -----------------------------------------------------------------------------------
$r = Invoke-RestMethod "$api/releases/$($r.id)" -Headers $h
$bad = Test-Digests $r
if ($bad.Count -gt 0) { Write-ReleaseStep $Version 'github-release' 'failed' "draft incomplete: $($bad -join '; ')"; throw "the draft is incomplete: $($bad -join '; ')" }
$patch = @{ draft = $false; make_latest = $(if ($NotLatest) { 'false' } else { 'true' }) } | ConvertTo-Json
$r = Invoke-RestMethod "$api/releases/$($r.id)" -Method Patch -Headers $h -ContentType 'application/json' -Body $patch
Write-ReleaseStep $Version 'github-release' 'done' "$($r.html_url) ($($local.Count) asset digests verified)"
Write-Host "GitHub release $tag published: $($r.html_url)" -ForegroundColor Green
