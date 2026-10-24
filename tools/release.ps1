# The release pipeline for one built version (docs\RELEASE-PIPELINE.md). build.ps1 runs it last; run it by hand to
# publish an already-built dist\<ver>\, to resume after a failure, or to rehearse with -DryRun.
#
#   check           tools\release-check.ps1          pre-publish checks (local, always)
#   public          tools\publish-public.ps1         secret scan + mirror push + annotated tag
#   github-release  tools\publish-github-release.ps1 draft -> uploads verified -> published
#   website         tools\website-handoff.ps1        dist\<ver>\website\ for the Website session (then verify-website.ps1)
#   discord         the card from the hand-off, posted with the website's announce-discord.ps1 on DarkCipher0x's go
#   (website and discord each have a switch in tools\release.json)
#
# Steps run in that order and each needs the one before it. A failure stops the run and is recorded in
# dist\<ver>\release-state.json; nothing done earlier is undone. -Resume skips steps already recorded as done.
# The kill switch (DCX_PUBLISH_OFF=1 or %USERPROFILE%\.darkcipher\publish-off) turns every public step off.
#
# Usage: .\tools\release.ps1 -Version 0.5.4 [-DryRun] [-Resume] [-Backfill] [-NotLatest]
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun, [switch]$Resume, [switch]$Backfill, [switch]$NotLatest)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$cfg = Get-ReleaseConfig
if (-not (Test-Path (Join-Path $ReleaseRoot "dist\$Version"))) { throw "dist\$Version doesn't exist: build it first" }
$mode = if ($DryRun) { 'DRY RUN' } else { 'release' }
Write-Host "ServerManagement $Version - $mode" -ForegroundColor Cyan
$off = Get-PublishOffReason
if ($off) { Write-Host "  ${off}: public steps will be skipped" -ForegroundColor Yellow }

function Step([string]$Name, [scriptblock]$Run) {
    if ($Resume -and -not $DryRun -and (Test-ReleaseStepDone $Version $Name)) { Write-Host "  $Name - already done, skipped (-Resume)"; return }
    Write-Host "  $Name ..." -ForegroundColor Cyan
    try { & $Run }
    catch { if (-not $DryRun) { Write-ReleaseStep $Version $Name 'failed' $_.Exception.Message }; Write-Host "  $Name FAILED: $($_.Exception.Message)" -ForegroundColor Red; throw }
}

Step 'check' { & (Join-Path $PSScriptRoot 'release-check.ps1') -Version $Version -Backfill:$Backfill }
Step 'public' { & (Join-Path $PSScriptRoot 'publish-public.ps1') -Version $Version -DryRun:$DryRun }
Step 'github-release' { & (Join-Path $PSScriptRoot 'publish-github-release.ps1') -Version $Version -DryRun:$DryRun -NotLatest:$NotLatest }
foreach ($s in @(@{ Name = 'website'; Cfg = $cfg.website }, @{ Name = 'discord'; Cfg = $cfg.discord })) {
    if (-not $s.Cfg.enabled) {
        if (-not $DryRun) { Write-ReleaseStep $Version $s.Name 'skipped' "off: $($s.Cfg.reason)" }
        Write-Host "  $($s.Name) - off: $($s.Cfg.reason)"
        continue
    }
    if ($off -and -not $DryRun) { Write-ReleaseStep $Version $s.Name 'skipped' $off; Write-Host "  $($s.Name) - skipped: $off"; continue }
    if ($s.Name -eq 'website') {
        # 0.6.9: the hand-off folder for the Website session (it deploys with DarkCipher0x's go; tools\verify-website.ps1 then
        # checks the live files and records "done").
        Step 'website' { & (Join-Path $PSScriptRoot 'website-handoff.ps1') -Version $Version -DryRun:$DryRun }
        continue
    }
    # Discord: the card is written by the hand-off (dist\<ver>\website\servermanagement-<ver>.json); it is posted with
    # the website's announce-discord.ps1 after the site is live and DarkCipher0x has seen its preview - never from here.
    $card = Join-Path $ReleaseRoot "dist\$Version\website\servermanagement-$Version.json"
    if (-not $DryRun) { Write-ReleaseStep $Version 'discord' 'prepared' "card $card; post it with announce-discord.ps1 -Preview first, then on DarkCipher0x's go" }
    Write-Host "  discord - card prepared: $card (posted only on DarkCipher0x's go)"
}

if (-not $DryRun) {
    $state = Read-ReleaseState $Version
    Write-Host "Release record: $(Get-ReleaseStatePath $Version)" -ForegroundColor Green
    $state.steps.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-15} {1,-8} {2}" -f $_.Name, $_.Value.status, $_.Value.evidence) }
}
