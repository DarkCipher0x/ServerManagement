# Release pipeline step 5: the sanitised source into the public mirror on GitHub (docs\RELEASE-PIPELINE.md).
#
# Source of truth: dist\<ver>\DarkCipherServerManagement-<ver>-source.zip, which write-public-source.ps1 built from the
# allowlist and privacy-scanned. Nothing else from this repository is ever copied: not its git history (old commits may
# hold what the privacy gate later caught), not dist\ or publish\.
#
# Before anything is committed: every image is on tools\approved-images.txt (someone has looked at it), no key, debug
# symbol or certificate files, and tools\secret-scan.ps1 is clean over the exact tree to be pushed. Before pushing:
# origin and branch are the ones in tools\release.json. One commit "ServerManagement <ver> - sanitised public source"
# and an annotated tag v<ver>, as DarkCipher0x with the GitHub no-reply address. Never a force push.
#
# Re-running is safe: a tag v<ver> that already points at the same tree is "done"; one that points at a different tree
# stops with an error (a published version never changes). A commit and tag made but not pushed are pushed on the next run.
# The kill switch (DCX_PUBLISH_OFF=1 or %USERPROFILE%\.darkcipher\publish-off) stops it before anything leaves the PC.
#
# Usage: .\tools\publish-public.ps1 -Version 0.5.3 [-DryRun] [-NoPush]
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun, [switch]$NoPush)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$cfg = Get-ReleaseConfig
$repo = $cfg.CloneFull
$zip = Join-Path $ReleaseRoot "dist\$Version\DarkCipherServerManagement-$Version-source.zip"
$tag = "v$Version"
$name, $mail = ($cfg.author -replace '^(.*) <(.*)>$', '$1|$2').Split('|')
$identity = @('-c', "user.name=$name", '-c', "user.email=$mail", '-c', 'core.safecrlf=false')

function Git { & $ReleaseGit -C $repo @args; if ($LASTEXITCODE) { throw "git $($args -join ' ') failed in $repo" } }

if (-not (Test-Path $zip)) { throw "no sanitised source for $Version ($zip): run build.ps1 first" }
$off = Get-PublishOffReason
if ($off -and -not $DryRun) { Write-ReleaseStep $Version 'public' 'skipped' $off; Write-Host "Public mirror skipped: $off" -ForegroundColor Yellow; return }
Assert-PublicRemote $cfg

# 1. The clone: clean and up to date.
if (& $ReleaseGit -C $repo status --porcelain) { throw "the public clone $repo has local changes - commit or discard them first" }
if (-not $DryRun -and -not $NoPush) { Git pull -q --ff-only origin $cfg.publicBranch }

# 2. Unpack the archive to a private staging folder and run every gate on it.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('dcx-public-' + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)
try {
    $top = Join-Path $stage "DarkCipherServerManagement-$Version-source"
    if (-not (Test-Path (Join-Path $top 'README.md'))) { throw "the archive has no DarkCipherServerManagement-$Version-source\README.md" }
    Remove-Item (Join-Path $top 'SOURCE-ARCHIVE.txt') -ErrorAction SilentlyContinue   # describes the zip; the repo has its README

    # 3. Already published? Compare the staged tree with the tag's tree (git's own hashing, LF-normalised like a commit).
    #    A version already pushed with this exact tree is verified, not re-published, so the gates below are for new pushes.
    $existing = & $ReleaseGit -C $repo tag -l $tag
    if ($existing) {
        $probe = Join-Path ([IO.Path]::GetTempPath()) ('dcx-probe-' + [guid]::NewGuid().ToString('N'))
        & $ReleaseGit init -q $probe; Copy-Item (Join-Path $top '*') $probe -Recurse -Force
        & $ReleaseGit -C $probe -c core.safecrlf=false add -A 2>$null
        $want = (& $ReleaseGit -C $probe write-tree).Trim()
        Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
        $have = (& $ReleaseGit -C $repo rev-parse "$tag^{tree}").Trim()
        if ($want -ne $have) { throw "$tag already exists in $repo with a different tree: a published version never changes (bump the version)" }
        if ($DryRun) { Write-Host "DRY RUN: $tag is already published with this exact tree; nothing to do." -ForegroundColor Cyan; return }
        $remote = & $ReleaseGit -C $repo ls-remote --tags origin "refs/tags/$tag"
        if (-not $remote -and -not $NoPush) { Git push -q origin $cfg.publicBranch; Git push -q origin $tag }
        Write-ReleaseStep $Version 'public' 'done' "tag $tag (tree $have) already published with this exact tree"
        Write-Host "Public source ${Version}: $tag already published with this exact tree." -ForegroundColor Green
        return
    }

    # 4. The gates, on exactly the tree that will be pushed: images seen by eye, no key or symbol files, secret scan.
    $approved = [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'approved-images.txt')) | Where-Object { $_ -match '^[0-9a-f]{64}\s' } | ForEach-Object { $_.Substring(0, 64) }
    $images = Get-ChildItem $top -Recurse -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|gif|bmp|ico|svg|webp|tiff?)$' }
    $unseen = $images | Where-Object { $approved -notcontains (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ($unseen) { throw "image(s) nobody has looked at yet - view each, then add its SHA-256 to tools\approved-images.txt:`n  " + (($unseen | ForEach-Object { $_.FullName.Substring($top.Length + 1) }) -join "`n  ") }
    $forbidden = Get-ChildItem $top -Recurse -File | Where-Object { $_.Extension -match '^\.(pdb|pfx|p12|snk|key|pem|ppk|kdbx)$' }
    if ($forbidden) { throw "files that must never be published: " + (($forbidden | ForEach-Object { $_.FullName.Substring($top.Length + 1) }) -join ', ') }
    $scanner = & (Join-Path $PSScriptRoot 'secret-scan.ps1') -Path $top | Select-Object -Last 1

    if ($DryRun) {
        $count = (Get-ChildItem $top -Recurse -File).Count
        Write-Host "DRY RUN: would replace $repo with $count files, commit 'ServerManagement $Version - sanitised public source' as $($cfg.author), tag $tag, push to $($cfg.publicRepo) ($($cfg.publicBranch)). Gates passed: images, file types, $scanner." -ForegroundColor Cyan
        Write-ReleaseStep $Version 'public' 'dry-run' "$count files; $scanner"
        return
    }

    # 5. Replace the public tree with the release's (everything but .git).
    Get-ChildItem $repo -Force | Where-Object { $_.Name -ne '.git' } | Remove-Item -Recurse -Force
    Get-ChildItem $top -Force | Move-Item -Destination $repo
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

# 6. One commit and an annotated tag per release.
Git @identity add -A
if (& $ReleaseGit -C $repo status --porcelain) { Git @identity commit -q -m "ServerManagement $Version - sanitised public source" }
else { Write-Host "The public tree already matches $Version; tagging only." }
Git @identity tag -a $tag -m "ServerManagement $Version"
$files = (& $ReleaseGit -C $repo ls-files | Measure-Object).Count
$commit = (& $ReleaseGit -C $repo rev-parse --short HEAD).Trim()

# 7. Push (skipped with -NoPush: the commit and tag wait in the clone; the next run pushes them).
if ($NoPush) { Write-ReleaseStep $Version 'public' 'failed' "commit $commit and tag $tag made locally, not pushed (-NoPush)"; Write-Host "Public source $Version committed and tagged in $repo ($files files), not pushed (-NoPush)." -ForegroundColor Yellow; return }
Git push -q origin $cfg.publicBranch
Git push -q origin $tag
Write-ReleaseStep $Version 'public' 'done' "commit $commit, tag $tag, $files files; $scanner"
Write-Host "Public source $Version pushed to GitHub ($files files, commit $commit, tag $tag)." -ForegroundColor Green
