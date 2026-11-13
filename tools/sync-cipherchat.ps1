#Requires -Version 7
# CipherChat's engine inside ServerManagement (ADR-117): a pinned, vendored copy of CipherChat's four UI-free libraries
# (Crypto, Transport, Api, Engine) in src\Vendor\CipherChat, taken from a git commit of the CipherChat repository (never
# its working tree), with VENDOR.json recording the version, the commit and the SHA-256 of every file.
# 0.6.38: also the call screen's page (CallHost\): the files CipherChat's Windows app ships as its CallHost folder (its
# web\callhost page, the portal's call engine and screen from web\app, and their vendored libraries), unchanged.
#
#   .\tools\sync-cipherchat.ps1 -From ..\CipherChat [-Ref <commit or tag>]   bring in a CipherChat version (review the diff!)
#   .\tools\sync-cipherchat.ps1 -Check                                        verify the copy against VENDOR.json (build.ps1)
#
# ServerManagement never edits these files: a protocol or security change is made in CipherChat, tested there, and
# brought in here with this script. -Check fails on a changed, missing or extra file.
param([string]$From = (Join-Path $PSScriptRoot '..\..\CipherChat'), [string]$Ref = 'HEAD', [switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dest = Join-Path $root 'src\Vendor\CipherChat'
$manifestPath = Join-Path $dest 'VENDOR.json'
$libraries = 'CipherChat.Crypto', 'CipherChat.Transport', 'CipherChat.Api', 'CipherChat.Engine'
# The call page, as CipherChat's windows\src\CipherChat.App\CipherChat.App.csproj lays out its CallHost folder.
$callPage = 'web/callhost'
$callApp = 'crypto.js', 'kat.js', 'callcrypto.js', 'call.js', 'callworker.js', 'callui.js', 'glitch.js', 'term.js', 'chat.css'
$callVendor = 'web/app/vendor'
# What is copied from each library: its source, its project and its lock file (package versions + content hashes).
$keep = '\.(cs|csproj)$|^packages\.lock\.json$'
# Written by this script around the copy (not from CipherChat), so not in the manifest's file list.
$own = 'VENDOR.json', 'Directory.Build.props', 'Directory.Build.targets', 'README.md'

function Hash([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

function Files {
    Get-ChildItem -LiteralPath $dest -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object { $_.FullName.Substring($dest.Length + 1).Replace('\', '/') } |
        Where-Object { $own -notcontains $_ } | Sort-Object
}

if ($Check) {
    if (-not (Test-Path $manifestPath)) { throw "src\Vendor\CipherChat\VENDOR.json is missing: run tools\sync-cipherchat.ps1 -From <CipherChat repo>" }
    $m = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $want = @{}; foreach ($p in $m.files.PSObject.Properties) { $want[$p.Name] = $p.Value }
    $have = @(Files)
    $bad = @()
    foreach ($f in $have) {
        if (-not $want.ContainsKey($f)) { $bad += "extra: $f" }
        elseif ((Hash (Join-Path $dest $f)) -ne $want[$f]) { $bad += "changed: $f" }
    }
    foreach ($f in $want.Keys) { if ($have -notcontains $f) { $bad += "missing: $f" } }
    if ($bad.Count) { throw "the vendored CipherChat doesn't match VENDOR.json (edit CipherChat, then sync):`n  " + ($bad -join "`n  ") }
    Write-Host "Vendored CipherChat $($m.version) (commit $($m.commit.Substring(0, 12))): $($have.Count) files match VENDOR.json" -ForegroundColor Green
    return
}

$From = Resolve-Path $From
$commit = (git -C $From rev-parse --verify "$Ref^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw "not a commit in ${From}: $Ref" }
$props = (git -C $From show "${commit}:windows/Directory.Build.props") -join "`n"
if ($props -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') { throw 'no <Version> in CipherChat''s windows\Directory.Build.props' }
$version = $Matches[1]

# The exact committed files, through git archive (no working-tree edits, no ignored files).
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("cc-vendor-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $tar = Join-Path $tmp 'src.tar'
    git -C $From archive --format=tar -o $tar $commit ($libraries | ForEach-Object { "windows/src/$_" }) LICENSE $callPage $callVendor ($callApp | ForEach-Object { "web/app/$_" })
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    tar -xf $tar -C $tmp
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }

    if (Test-Path $dest) { Get-ChildItem -LiteralPath $dest -Force | Where-Object { $own -notcontains $_.Name } | Remove-Item -Recurse -Force }
    else { New-Item -ItemType Directory $dest | Out-Null }
    foreach ($lib in $libraries) {
        $src = Join-Path $tmp "windows\src\$lib"
        if (-not (Test-Path $src)) { throw "CipherChat $commit has no windows\src\$lib" }
        Get-ChildItem -LiteralPath $src -Recurse -File | Where-Object { $_.Name -match $keep } | ForEach-Object {
            $rel = $_.FullName.Substring($src.Length + 1)
            $to = Join-Path $dest "$lib\$rel"
            New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
            Copy-Item -LiteralPath $_.FullName $to
        }
    }
    Copy-Item -LiteralPath (Join-Path $tmp 'LICENSE') (Join-Path $dest 'LICENSE')
    # The call page: CallHost\ = web\callhost\*, CallHost\app\ = the listed web\app files, CallHost\app\vendor\ = their libraries.
    $hostDir = Join-Path $dest 'CallHost'
    foreach ($pair in @(@{ From = Join-Path $tmp ($callPage -replace '/', '\'); To = $hostDir }, @{ From = Join-Path $tmp ($callVendor -replace '/', '\'); To = Join-Path $hostDir 'app\vendor' })) {
        if (-not (Test-Path $pair.From)) { throw "CipherChat $commit has no $($pair.From.Substring($tmp.Length + 1))" }
        Get-ChildItem -LiteralPath $pair.From -Recurse -File | ForEach-Object {
            $to = Join-Path $pair.To $_.FullName.Substring($pair.From.Length + 1)
            New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
            Copy-Item -LiteralPath $_.FullName $to
        }
    }
    foreach ($f in $callApp) {
        $src = Join-Path $tmp "web\app\$f"
        if (-not (Test-Path $src)) { throw "CipherChat $commit has no web\app\$f" }
        New-Item -ItemType Directory -Force (Join-Path $hostDir 'app') | Out-Null
        Copy-Item -LiteralPath $src (Join-Path $hostDir "app\$f")
    }
    # CipherChat's own build settings (verbatim) for its libraries, so ServerManagement's analyzers and rules don't
    # rewrite or reject CipherChat's code; ServerManagement's locked restore still applies.
    $ccProps = git -C $From show "${commit}:windows/Directory.Build.props"
    [IO.File]::WriteAllText((Join-Path $dest 'CipherChat.Build.props'), (($ccProps -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
} finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

$files = [ordered]@{}
foreach ($f in Files) { $files[$f] = Hash (Join-Path $dest $f) }
$manifest = [ordered]@{
    source = 'CipherChat (https://github.com/DarkCipher0x/CipherChat), MIT'
    version = $version
    commit = $commit
    libraries = $libraries
    synced = (Get-Date).ToString('yyyy-MM-dd')
    files = $files
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Vendored CipherChat $version (commit $($commit.Substring(0, 12))): $($files.Count) files. Review: git diff -- src/Vendor/CipherChat" -ForegroundColor Green
