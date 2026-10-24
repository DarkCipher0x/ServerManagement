# Shared helpers for the release pipeline (docs\RELEASE-PIPELINE.md). Dot-source it: . "$PSScriptRoot\release-common.ps1"
# Pure ASCII (Windows PowerShell 5.1 reads scripts without a BOM as the ANSI code page).

$ReleaseRoot = Split-Path $PSScriptRoot -Parent
$ReleaseGit = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $ReleaseGit)) { $ReleaseGit = 'git' }

function Get-ReleaseConfig {
    $c = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release.json')) | ConvertFrom-Json
    $c | Add-Member -NotePropertyName CloneFull -NotePropertyValue ([IO.Path]::GetFullPath((Join-Path $ReleaseRoot $c.publicClone))) -Force
    $c
}

# --- the kill switch: either form turns every public step off, for every run, without editing anything ---------------
function Get-PublishOffReason {
    if ($env:DCX_PUBLISH_OFF -eq '1') { return 'kill switch: DCX_PUBLISH_OFF=1' }
    $f = Join-Path $env:USERPROFILE '.darkcipher\publish-off'
    if (Test-Path $f) { return "kill switch: $f exists" }
    # This app only: tools\release.json "publishing": { "enabled": false, "reason": "..." } (DarkCipher0x, 2026-10-21).
    $cfgPath = Join-Path $ReleaseRoot 'tools\release.json'
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if ($cfg.publishing -and $cfg.publishing.enabled -eq $false) { return "publishing off for this app (tools\release.json): $($cfg.publishing.reason)" }
    }
    return $null
}

# --- the record of a version's release: dist\<ver>\release-state.json ------------------------------------------------
function Get-ReleaseStatePath([string]$Version) { Join-Path $ReleaseRoot "dist\$Version\release-state.json" }

function Read-ReleaseState([string]$Version) {
    $p = Get-ReleaseStatePath $Version
    if (Test-Path $p) { return ([IO.File]::ReadAllText($p) | ConvertFrom-Json) }
    return [pscustomobject]@{ version = $Version; steps = [pscustomobject]@{} }
}

# Records one step: status is done, skipped, dry-run or failed; evidence is a short string (commit, URL, digests...).
function Write-ReleaseStep([string]$Version, [string]$Step, [string]$Status, [string]$Evidence = '') {
    $s = Read-ReleaseState $Version
    $entry = [pscustomobject]@{ status = $Status; at = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'); evidence = $Evidence }
    $s.steps | Add-Member -NotePropertyName $Step -NotePropertyValue $entry -Force
    $p = Get-ReleaseStatePath $Version
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllText($p, ($s | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
}

function Test-ReleaseStepDone([string]$Version, [string]$Step) {
    $s = Read-ReleaseState $Version
    $e = $s.steps.PSObject.Properties[$Step]
    return ($e -and $e.Value.status -eq 'done')
}

# --- remote verification: refuse to push anywhere but the expected repository and branch -----------------------------
function Assert-PublicRemote($Config) {
    $clone = $Config.CloneFull
    if (-not (Test-Path (Join-Path $clone '.git'))) { throw "no public clone at $clone - clone $($Config.publicRepo) there first" }
    $url = (& $ReleaseGit -C $clone remote get-url origin).Trim()
    if ($url -ne $Config.publicRepo) { throw "the public clone's origin is '$url', expected '$($Config.publicRepo)': refusing to push" }
    $branch = (& $ReleaseGit -C $clone rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne $Config.publicBranch) { throw "the public clone is on '$branch', expected '$($Config.publicBranch)'" }
}

# --- GitHub sign-in: the token Git Credential Manager already holds, in memory only (never stored or printed) ---------
function Get-GitHubToken {
    # The request goes in through a file redirect: Windows PowerShell's pipes start with a byte-order mark, which git
    # rejects ("missing protocol field"). ASCII, LF line ends. (CaseKeeper's working fix.)
    $req = Join-Path ([IO.Path]::GetTempPath()) ('dcx-cred-' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllBytes($req, [Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n"))
    $before = $env:GCM_INTERACTIVE; $env:GCM_INTERACTIVE = 'auto'
    try { $answer = cmd /c "`"$ReleaseGit`" credential fill < `"$req`"" }
    finally { Remove-Item $req -ErrorAction SilentlyContinue; $env:GCM_INTERACTIVE = $before }
    $line = $answer | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) { throw 'No GitHub sign-in available. Run "git -C ..\ServerManagement-public pull" once to sign in, then retry.' }
    $line.Substring(9).Trim()
}

function Get-GitHubHeaders {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    @{ Authorization = "Bearer $(Get-GitHubToken)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'ServerManagement-release' }
}

# --- allowlist globs: * = one name part, ** = any folders --------------------------------------------------------------
function Convert-GlobToRegex([string]$Glob) {
    $sb = New-Object Text.StringBuilder '^'
    $i = 0
    while ($i -lt $Glob.Length) {
        $c = $Glob[$i]
        if ($c -eq '*' -and $i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*') {
            if ($i + 2 -lt $Glob.Length -and $Glob[$i + 2] -eq '/') { [void]$sb.Append('(?:.*/)?'); $i += 3 }
            else { [void]$sb.Append('.*'); $i += 2 }
            continue
        }
        if ($c -eq '*') { [void]$sb.Append('[^/]*') }
        elseif ($c -eq '?') { [void]$sb.Append('[^/]') }
        else { [void]$sb.Append([regex]::Escape([string]$c)) }
        $i++
    }
    [void]$sb.Append('$')
    $sb.ToString()
}

# Returns @{ Allow = regex[]; Deny = regex[] } from tools\public-allowlist.txt.
function Get-PublicAllowlist {
    $allow = New-Object System.Collections.Generic.List[string]
    $deny = New-Object System.Collections.Generic.List[string]
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'public-allowlist.txt'))) {
        $t = $line.Trim()
        if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
        if ($t.StartsWith('!')) { $deny.Add((Convert-GlobToRegex $t.Substring(1))) } else { $allow.Add((Convert-GlobToRegex $t)) }
    }
    @{ Allow = $allow.ToArray(); Deny = $deny.ToArray() }
}

# 'allow', 'deny' (private) or 'unlisted' for one repository-relative path with / separators.
function Get-PublicClass([string]$Path, $List) {
    foreach ($rx in $List.Deny) { if ($Path -match $rx) { return 'deny' } }
    foreach ($rx in $List.Allow) { if ($Path -match $rx) { return 'allow' } }
    return 'unlisted'
}
