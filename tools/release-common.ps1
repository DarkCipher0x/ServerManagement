#Requires -Version 7
# Shared helpers for the release pipeline (docs\RELEASE-PIPELINE.md). Dot-source it: . "$PSScriptRoot\release-common.ps1"
# Pure ASCII (Windows PowerShell 5.1 reads scripts without a BOM as the ANSI code page).

$ReleaseRoot = Split-Path $PSScriptRoot -Parent
$ReleaseGit = 'C:\Program Files\Git\cmd\git.exe'
if (-not (Test-Path $ReleaseGit)) { $ReleaseGit = 'git' }

# Where versions are built: dist\ of this clone, or DCX_DIST_ROOT (build.ps1 -Preflight points it outside the repository).
function Get-DistRoot { if ($env:DCX_DIST_ROOT) { [IO.Path]::GetFullPath($env:DCX_DIST_ROOT) } else { Join-Path $ReleaseRoot 'dist' } }

# A preflight build (build.ps1 -Preflight, 0.6.54) is labelled <x.y.z>-preflight.<run> and can never be released.
function Test-PreflightVersion([string]$Version) { $Version -match '-preflight\.' }
function Assert-NotPreflight([string]$Version, [string]$What) {
    if (Test-PreflightVersion $Version) { throw "preflight guard: $What refused - $Version is a preflight build (build.ps1 -Preflight), which is never released" }
    if ($env:DCX_DIST_ROOT) { throw "preflight guard: $What refused - DCX_DIST_ROOT is set ($($env:DCX_DIST_ROOT)): releases come only from this clone's dist\" }
    $sp = Join-Path $ReleaseRoot "dist\$Version\release-state.json"
    if ((Test-Path $sp) -and (([IO.File]::ReadAllText($sp) | ConvertFrom-Json).preflight)) { throw "preflight guard: $What refused - dist\$Version is a preflight build" }
}

function Get-ReleaseConfig {
    $c = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release.json')) | ConvertFrom-Json
    $c | Add-Member -NotePropertyName CloneFull -NotePropertyValue ([IO.Path]::GetFullPath((Join-Path $ReleaseRoot $c.publicClone))) -Force
    $c
}

# --- published add-ons (release.json "publishedAddons", DarkCipher0x's exceptions to "add-ons are never published") ----
function Get-PublishedAddonAssets($cfg) {
    @($cfg.publishedAddons | Where-Object { $_ } | ForEach-Object {
        if ("$($_.id)" -notmatch '^[a-z0-9]+(\.[a-z0-9-]+)+$' -or "$($_.version)" -notmatch '^\d+\.\d+\.\d+$') { throw "release.json publishedAddons: bad id or version '$($_.id)' '$($_.version)'" }
        "$($_.id)-$($_.version).dcxaddon"
    })
}

# The signed add-on packages live outside git (dist\addons\<id>\ of the main working clone). The add-on store is, in
# order: $env:DCX_ADDON_STORE, this clone's dist\addons, release.json "addonStore" (relative to the repository root).
function Get-AddonStore($cfg) {
    $candidates = @($env:DCX_ADDON_STORE, (Join-Path $ReleaseRoot 'dist\addons'), $(if ($cfg.addonStore) { Join-Path $ReleaseRoot $cfg.addonStore })) | Where-Object { $_ }
    foreach ($c in $candidates) { if (Test-Path $c -PathType Container) { return [IO.Path]::GetFullPath($c) } }
    throw "add-on guard: no add-on store found (tried: $($candidates -join '; ')). Set DCX_ADDON_STORE or release.json addonStore"
}

# Copies each published add-on's package (and its .sha256) from the add-on store into dist\<ver>\ (build.ps1, ADR-123).
# The package must match its .sha256 AND the SHA-256 pinned in release.json; anything missing or different fails the build.
function Copy-PublishedAddons($cfg, [string]$Version, [string]$Store) {
    $out = Join-Path (Get-DistRoot) $Version
    if (-not $Store) { $Store = Get-AddonStore $cfg }
    $copied = @()
    foreach ($a in @($cfg.publishedAddons | Where-Object { $_ })) {
        $name = "$($a.id)-$($a.version).dcxaddon"
        if ("$($a.sha256)" -notmatch '^[0-9a-f]{64}$') { throw "add-on guard: release.json publishedAddons $name has no pinned sha256" }
        $src = Join-Path $Store "$($a.id)\$name"
        if (-not (Test-Path $src) -or -not (Test-Path "$src.sha256")) { throw "add-on guard: $src (and its .sha256) is missing: pack it with samples\addons\pack.ps1" }
        $want = ([IO.File]::ReadAllText("$src.sha256").Trim() -split '\s+')[0].ToLowerInvariant()
        $got = (Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $want) { throw "add-on guard: $name ($got) doesn't match its .sha256 ($want)" }
        if ($got -ne $a.sha256) { throw "add-on guard: $name ($got) doesn't match the SHA-256 pinned in release.json ($($a.sha256))" }
        Copy-Item $src (Join-Path $out $name) -Force
        Copy-Item "$src.sha256" (Join-Path $out "$name.sha256") -Force
        $copied += "$name $got"
    }
    $copied
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
function Get-ReleaseStatePath([string]$Version) { Join-Path (Get-DistRoot) "$Version\release-state.json" }

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
    Assert-ReleaseRemotes $clone 'ServerManagement-public' | Out-Null
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

# =====================================================================================================================
# Mandatory release guards (ADR-123; the shared standard's "Mandatory release guards", DarkCipher0x 2026-11-12).
# Any guard that fails stops the pipeline. tools\test-release-guards.ps1 proves each one with a negative test.
# =====================================================================================================================
$ReleaseRepoName = 'DarkCipher-ServerManagement'
# The gateway origin's host and home folder and the decommissioned servers are private (internal names, a user folder):
# they live only on the build PC, in %USERPROFILE%\.darkcipher\release-hosts.json beside the version ledger, never in git:
#   { "gatewayHost": "<host>", "gatewayHome": "/home/<user>", "forbiddenHosts": [ "<host>", ... ] }
function Get-ReleaseHosts {
    $p = Join-Path $env:USERPROFILE '.darkcipher\release-hosts.json'
    if (-not (Test-Path $p)) { throw "remote guard: $p is missing (the gateway origin and the decommissioned servers; private, never in git)" }
    $h = [IO.File]::ReadAllText($p) | ConvertFrom-Json
    if (-not $h.gatewayHost -or -not $h.gatewayHome -or -not @($h.forbiddenHosts).Count) { throw "remote guard: $p needs gatewayHost, gatewayHome and forbiddenHosts" }
    [pscustomobject]@{ GatewayHost = "$($h.gatewayHost)"; GatewayHome = "$($h.gatewayHome)".TrimEnd('/'); Forbidden = @($h.forbiddenHosts | ForEach-Object { "$_" }) }
}
# Mojibake: UTF-8 read as Windows-1252/Latin-1 (e.g. U+00C2 U+00B7, U+00E2 U+20AC U+00A6). Written with escapes: this file stays ASCII.
$MojibakeRegex = New-Object Text.RegularExpressions.Regex('\u00C2|\u00E2\u20AC|\u00C3[\u0080-\u00BF]')

# --- hold guard (guard 7a): a hold stops every build and release run before it does anything ----------------------------
# Set by whoever needs the pipeline stopped, even while an agent is mid-task: the environment variable DCX_HOLD (any
# non-empty value other than 0), the file dist\HOLD in this clone, or %USERPROFILE%\.darkcipher\hold (every clone on
# this PC, so a fresh release clone is held too). The file's first line, if any, is shown as the reason. Remove it to go on.
function Get-HoldReason {
    if ($env:DCX_HOLD -and $env:DCX_HOLD -ne '0') { return "DCX_HOLD=$($env:DCX_HOLD)" }
    foreach ($f in (Join-Path $ReleaseRoot 'dist\HOLD'), (Join-Path $env:USERPROFILE '.darkcipher\hold')) {
        if (Test-Path $f -PathType Leaf) {
            $why = (Get-Content $f -TotalCount 1 -ErrorAction SilentlyContinue | Select-Object -First 1)
            return "$f exists$(if ($why) { ": $why" })"
        }
    }
    return $null
}
function Assert-NoHold([string]$What) {
    $h = Get-HoldReason
    if ($h) { throw "hold guard: $What refused - the pipeline is on hold ($h). Nothing was built, bumped or published. Remove the hold when the go is given" }
}

# --- remote guard -------------------------------------------------------------------------------------------------------
# The gateway origin: <gatewayHome>/git/<repo>.git, locally or over ssh on <gatewayHost> (scp-style or ssh://).
function Test-GatewayRemoteUrl([string]$Url, [string]$Repo) {
    $h = Get-ReleaseHosts
    $r = [regex]::Escape($Repo); $g = [regex]::Escape($h.GatewayHost); $hm = [regex]::Escape($h.GatewayHome)
    $Url -match "^(?:$hm/git/$r\.git|[A-Za-z0-9._-]+@$g(?:\.[A-Za-z0-9.-]+)?:(?:$hm/|~/)?git/$r\.git|ssh://[A-Za-z0-9._-]+@$g(?:\.[A-Za-z0-9.-]+)?(?::\d+)?$hm/git/$r\.git)/?$"
}

# Fails unless a remote points at the gateway origin and none points at a decommissioned server. Returns the name of the
# gateway remote (origin, or vps where origin is GitHub).
function Assert-ReleaseRemotes([string]$RepoDir = $ReleaseRoot, [string]$Repo = $ReleaseRepoName) {
    $lines = @(& $ReleaseGit -C $RepoDir remote -v)
    if ($LASTEXITCODE) { throw "remote guard: git remote -v failed in $RepoDir" }
    $pairs = @($lines | ForEach-Object { $p = $_ -split '\s+'; if ($p.Count -ge 2) { [pscustomobject]@{ Name = $p[0]; Url = $p[1] } } })
    foreach ($x in $pairs) {
        foreach ($h in (Get-ReleaseHosts).Forbidden) {
            if ($x.Url -match [regex]::Escape($h)) { throw "remote guard: remote '$($x.Name)' of $RepoDir points at $h ($($x.Url)). That server is being decommissioned: remove the remote" }
        }
    }
    $gw = $pairs | Where-Object { Test-GatewayRemoteUrl $_.Url $Repo } | Select-Object -First 1
    if (-not $gw) { throw "remote guard: no remote of $RepoDir points at the gateway origin (<gatewayHost>:<gatewayHome>/git/$Repo.git, release-hosts.json). Remotes: $(($pairs | ForEach-Object { "$($_.Name)=$($_.Url)" } | Sort-Object -Unique) -join ', ')" }
    $gw.Name
}

# --- clean-clone guard --------------------------------------------------------------------------------------------------
# Builds come from a fresh clone of the gateway origin at the release commit: nothing modified or untracked, no build
# output (bin, obj, publish, dist), and HEAD present on the gateway remote. Ignored *.local.txt files (the private term
# list, fetched by build.ps1) are the only ignored files allowed. Returns the commit.
function Assert-CleanCheckout([string]$RepoDir = $ReleaseRoot) {
    $remote = Assert-ReleaseRemotes $RepoDir
    $dirty = @(& $ReleaseGit -C $RepoDir status --porcelain --untracked-files=all)
    if ($dirty.Count) { throw "clean-clone guard: $RepoDir has $($dirty.Count) modified or untracked file(s) (first: $($dirty[0].Trim())). Build from a fresh clone of the gateway origin at the release commit" }
    $ignored = @(& $ReleaseGit -C $RepoDir ls-files --others --ignored --exclude-standard --directory | Where-Object { $_ -and $_ -notmatch '^tools/[^/]+\.local\.txt$' })
    if ($ignored.Count) { throw "clean-clone guard: $RepoDir holds build output or ignored files ($(($ignored | Select-Object -First 5) -join ', ')). Build from a fresh clone" }
    & $ReleaseGit -C $RepoDir fetch -q $remote
    if ($LASTEXITCODE) { throw "clean-clone guard: cannot fetch the gateway remote '$remote'" }
    $head = (& $ReleaseGit -C $RepoDir rev-parse HEAD).Trim()
    $on = @(& $ReleaseGit -C $RepoDir branch -r --contains $head --list "$remote/*")
    if (-not $on.Count) { throw "clean-clone guard: HEAD $head is not on the gateway remote '$remote': push the release commit to the gateway origin first" }
    $head
}

# --- pre-flight (0.6.53): restore and compile BEFORE the version is spent ----------------------------------------------
# 0.6.52 was spent by a stale lock file (NU1004 after an SDK patch). So build.ps1 first does a locked restore of everything
# it builds, under the SDK global.json pins, and a compile-only Release build of the exe project (the publish settings:
# win-x64, self-contained, from the csproj; no publish). Any failure stops the build before the ledger records the version.
function Invoke-BuildPreflight {
    $saved = $env:DCX_LOCKED_RESTORE; $env:DCX_LOCKED_RESTORE = '1'
    Push-Location $ReleaseRoot
    try {
        $sdk = "$(dotnet --version 2>&1 | Select-Object -First 1)"
        if ($LASTEXITCODE) { throw "pre-flight: the pinned .NET SDK is not available ($sdk): install the SDK global.json names" }
        # Every project with a lock file (0.6.54: the stream add-on's was missed, which spent 0.6.53), plus the solution.
        $projects = @('DarkCipher.ServerManagement.sln') + @(& $ReleaseGit -C $ReleaseRoot ls-files -- '*packages.lock.json' | Where-Object { $_ -notmatch '^src/Vendor/' } |
            ForEach-Object { Get-ChildItem (Join-Path $ReleaseRoot (Split-Path $_ -Parent)) -Filter *.csproj | Select-Object -First 1 | ForEach-Object { $_.FullName.Substring($ReleaseRoot.Length + 1) } })
        foreach ($p in $projects) {
            # --force: re-evaluate even when obj\ says up to date, so a changed lock file is always checked.
            $o = @(dotnet restore $p --locked-mode --force -nologo -v q 2>&1 | ForEach-Object { "$_" })
            if ($LASTEXITCODE) {
                $err = ($o | Where-Object { $_ -match 'error' } | Select-Object -First 2) -join ' / '
                throw "pre-flight: locked restore failed for $p under SDK ${sdk}: $err. Regenerate the lock files under this SDK (dotnet restore --force-evaluate), commit, bump"
            }
        }
        $o = @(dotnet build 'src\DcxManager\DcxManager.csproj' -c Release --no-restore -nologo -v q 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE) { throw "pre-flight: the compile-only Release build failed: $(($o | Where-Object { $_ -match 'error' } | Select-Object -First 2) -join ' / ')" }
        "SDK $sdk; locked restore ok ($($projects.Count) projects); Release compile ok"
    }
    finally { Pop-Location; $env:DCX_LOCKED_RESTORE = $saved }
}

# --- version guard: a version is never rebuilt, even in a fresh clone ----------------------------------------------------
function Get-VersionLedgerPath { Join-Path $env:USERPROFILE '.darkcipher\servermanagement-built-versions.json' }

function Assert-NewVersion([string]$Version, $Config, [switch]$NoRecord) {
    $p = Get-VersionLedgerPath
    $known = New-Object 'System.Collections.Generic.HashSet[string]'
    if (Test-Path $p) { foreach ($v in @(([IO.File]::ReadAllText($p) | ConvertFrom-Json).versions)) { [void]$known.Add("$($v.version)") } }
    # Every dist\<ver> folder of this clone and of sibling clones of this repository counts as built.
    foreach ($d in Get-ChildItem (Split-Path $ReleaseRoot -Parent) -Directory -ErrorAction SilentlyContinue) {
        if (-not (Test-Path (Join-Path $d.FullName 'src\DcxManager\DcxManager.csproj'))) { continue }
        foreach ($v in Get-ChildItem (Join-Path $d.FullName 'dist') -Directory -ErrorAction SilentlyContinue) { if ($v.Name -match '^\d+\.\d+\.\d+') { [void]$known.Add($v.Name) } }
    }
    # Versions found beside this clone but not yet in the ledger are recorded, so a clone elsewhere still knows them.
    $ledgerVersions = if (Test-Path $p) { @(([IO.File]::ReadAllText($p) | ConvertFrom-Json).versions | ForEach-Object { "$($_.version)" }) } else { @() }
    if (-not $NoRecord) { foreach ($v in @($known | Where-Object { $ledgerVersions -notcontains $_ } | Sort-Object)) { Add-BuiltVersion $v '' 'seen: dist folder beside the release clone' } }
    if ($known.Contains($Version)) { throw "version guard: $Version has been built before (ledger $p or a dist\$Version folder). A version is never rebuilt: bump <Version>" }
    if ($Config -and (Test-Path (Join-Path $Config.CloneFull '.git'))) {
        if (& $ReleaseGit -C $Config.CloneFull tag -l "v$Version") { throw "version guard: v$Version is already tagged in the public clone" }
    }
}

function Add-BuiltVersion([string]$Version, [string]$Commit, [string]$RunId) {
    $p = Get-VersionLedgerPath
    $l = if (Test-Path $p) { [IO.File]::ReadAllText($p) | ConvertFrom-Json } else { [pscustomobject]@{ versions = @() } }
    $l.versions = @($l.versions) + [pscustomobject]@{ version = $Version; commit = $Commit; run = $RunId; clone = $ReleaseRoot; at = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz') }
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllText($p, ($l | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
}

# --- gitleaks: pinned (tools\gitleaks.pin), mandatory; absent or different means stop ------------------------------------
function Get-PinnedGitleaks {
    $pinFile = Join-Path $PSScriptRoot 'gitleaks.pin'
    if (-not (Test-Path $pinFile)) { throw "gitleaks guard: tools\gitleaks.pin is missing" }
    $pin = [IO.File]::ReadAllText($pinFile) | ConvertFrom-Json
    $exe = Join-Path $env:LOCALAPPDATA "DarkCipher\tools\gitleaks-$($pin.version)\gitleaks.exe"
    if (-not (Test-Path $exe)) { throw "gitleaks guard: gitleaks $($pin.version) is not installed at $exe - run tools\get-gitleaks.ps1" }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $pin.exeSha256) { throw "gitleaks guard: $exe has SHA-256 $hash, not the pinned $($pin.exeSha256): refusing to run it" }
    [pscustomobject]@{ Exe = $exe; Version = $pin.version; Sha256 = $hash }
}

# --- toolchain guard: what built this version, recorded in release-state.json --------------------------------------------
function Get-ReleaseToolchain {
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw "toolchain guard: PowerShell $($PSVersionTable.PSVersion) - the pipeline needs PowerShell 7 (pwsh)" }
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    Push-Location $ReleaseRoot
    try {
        dotnet tool restore | Out-Null
        $wix = (dotnet tool run wix --version 2>$null | Select-Object -First 1)
        $sdk = (dotnet --version | Select-Object -First 1)
    }
    finally { Pop-Location }
    $gl = Get-PinnedGitleaks
    [pscustomobject][ordered]@{
        pwsh = $PSVersionTable.PSVersion.ToString(); pwshEdition = $PSVersionTable.PSEdition
        dotnetSdk = "$sdk"; wix = "$wix"; git = "$((& $ReleaseGit --version) | Select-Object -First 1)"
        edge = if ($edge) { (Get-Item $edge).VersionInfo.ProductVersion } else { $null }
        gitleaks = "$($gl.Version) (sha256 $($gl.Sha256))"; os = [Environment]::OSVersion.VersionString
    }
}

function Set-ReleaseStateProperty([string]$Version, [string]$Name, $Value) {
    $s = Read-ReleaseState $Version
    $s | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    $p = Get-ReleaseStatePath $Version
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllText($p, ($s | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
}

# --- no hand-run steps ---------------------------------------------------------------------------------------------------
# build.ps1 sets DCX_PIPELINE_RUN for its run. A tool that writes into dist\<ver>\ outside that run marks the version
# tainted; release-check.ps1 then refuses it (bump and build again).
function Assert-PipelineRun([string]$Version, [string]$Tool) {
    if ($env:DCX_PIPELINE_RUN) { return }
    if (-not (Test-Path (Join-Path (Get-DistRoot) $Version))) { return }
    Write-ReleaseStep $Version "hand-run:$Tool" 'tainted' 'run by hand outside build.ps1: this version can no longer be released (bump it and build again)'
    Write-Warning "$Tool was run by hand for ${Version}: release-check.ps1 will refuse $Version from now on (bump the version and build again)"
}

# The steps one build.ps1 run must record, in order (release-check.ps1 compares the list exactly).
$PipelineSteps = @('guards', 'readme', 'warm-up', 'selftest', 'paste-test', 'publish', 'selftest-published', 'msi', 'setup', 'update-manifest', 'portable', 'policy', 'sbom', 'addons', 'checksums', 'guides', 'public-source', 'bundle')

function Add-PipelineStep([string]$Version, [string]$Step, [string]$Evidence = '') {
    $s = Read-ReleaseState $Version
    $pl = $s.pipeline
    if (-not $pl -or $pl.run -ne $env:DCX_PIPELINE_RUN) { throw "pipeline record: step $Step outside the recorded run" }
    $pl.steps = @($pl.steps) + [pscustomobject]@{ step = $Step; at = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'); evidence = $Evidence }
    Set-ReleaseStateProperty $Version 'pipeline' $pl
}

# --- encoding gate -------------------------------------------------------------------------------------------------------
# tools\encoding-exceptions.txt: "<path><TAB><exact hit count><TAB><reason>" for text where the pattern is genuine (not
# mojibake). Any other hit, or a different count, fails.
function Get-EncodingExceptions {
    $h = @{}
    $f = Join-Path $PSScriptRoot 'encoding-exceptions.txt'
    if (Test-Path $f) {
        foreach ($line in [IO.File]::ReadAllLines($f)) {
            if ($line.Trim().Length -eq 0 -or $line.StartsWith('#')) { continue }
            $p = $line.Split("`t")
            if ($p.Count -lt 3 -or $p[1] -notmatch '^\d+$' -or $p[2].Trim().Length -lt 10) { throw "encoding-exceptions.txt: '$line' needs <path> TAB <count> TAB <reason>" }
            $h[$p[0]] = [int]$p[1]
        }
    }
    $h
}

# Returns the problems for one text: hits not covered by an exception (or an exception whose count no longer matches).
function Test-TextEncoding([string]$Label, [string]$Key, [string]$Text, $Exceptions) {
    $ms = $MojibakeRegex.Matches($Text)
    if ($Exceptions.ContainsKey($Key)) {
        if ($ms.Count -ne $Exceptions[$Key]) { return @("encoding gate: $Label has $($ms.Count) mojibake-pattern hit(s), tools\encoding-exceptions.txt allows exactly $($Exceptions[$Key])") }
        return @()
    }
    @($ms | Select-Object -First 3 | ForEach-Object {
        $line = ($Text.Substring(0, $_.Index) -split "`n").Count
        $s = [Math]::Max(0, $_.Index - 25); $ctx = $Text.Substring($s, [Math]::Min(60, $Text.Length - $s))
        $ctx = -join ($ctx.ToCharArray() | ForEach-Object { if ([int]$_ -lt 32) { ' ' } elseif ([int]$_ -gt 126) { '\u{0:X4}' -f [int]$_ } else { $_ } })
        "encoding gate: mojibake in ${Label}:$line ($ctx)"
    })
}
