#Requires -Version 7
# Builds DarkCipher ServerManagement into .\dist\<ver>\ (one folder per release; earlier releases stay in theirs):
#   DarkCipherServerManagement-<ver>-x64.msi            installer (Start menu + optional desktop shortcut, upgrades older versions)
#   DarkCipherServerManagement-<ver>-x64-portable.zip   unzip-and-run (self-contained, no .NET install needed)
# Runs the self-test first (scratch data folder, off-screen) and stops if anything fails.
#   update.json                                         update manifest for Settings -> Updates (publish next to the MSI)
#   DarkCipherServerManagement-<ver>-sbom.cdx.json      CycloneDX SBOM (tools\write-sbom.ps1); budgets: tools\budgets.json
#   DarkCipherServerManagement-<ver>-release-notes.md   a copy of docs\release-notes\<ver>.md
#   policy\ + DarkCipherServerManagement-<ver>-policy-templates.zip   Group Policy ADMX / ADML + POLICIES.md (0.3.0 §10)
#   *.sha256, DarkCipherServerManagement-<ver>-SHA256SUMS.txt   checksums (tools\write-checksums.ps1)
# The release bundle (0.4.0 rule, step 6): six PDFs — the complete Guide, User-Guide, Developer-Guide, Business-Guide (Active
#   Directory / LDAPS / Entra, Group Policy, syslog, log sinks, team), Security-Report (the penetration tests done) and
#   Project-Structure (the tree and how it is built) — plus the sanitised public source archive (…-source.zip, privacy-scanned).
#   The build fails when any of them is missing.
# Code signing (optional): set DCX_SIGN_THUMBPRINT to a code-signing certificate in CurrentUser\My (and DCX_SIGN_TIMESTAMP
# to override the RFC 3161 timestamp server). Unsigned builds are refused by the in-app updater, by design.
# Microsoft Store (free signing, ADR-029): after this script, tools\make-msix.ps1 packages .\publish as an MSIX.
# Source control: the tree is a local git repository since 0.2.0 phase 0 (docs\PLAN-0.2.0.md); dist\ and publish\ are ignored.
# Release pipeline (step 7, since 0.5.4; docs\RELEASE-PIPELINE.md): tools\release.ps1 runs the pre-publish checks, pushes
#   the allowlisted, secret-scanned source to https://github.com/DarkCipher0x/ServerManagement with an annotated tag v<ver>,
#   and publishes the GitHub Release (draft, uploads verified, then published). Website and Discord stay off while
#   ServerManagement is "coming soon" (tools\release.json). -NoPublish skips step 7; -DryRun rehearses it without
#   publishing. The kill switch (DCX_PUBLISH_OFF=1 or %USERPROFILE%\.darkcipher\publish-off) turns every public step off.
param([switch]$NoPublish, [switch]$DryRun, [switch]$PreflightOnly, [switch]$Preflight)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# Build hygiene (Directory.Build.props): restore only exactly what packages.lock.json pins (versions + hashes), fail on
# high/critical package advisories, and build deterministically.
$env:DCX_LOCKED_RESTORE = '1'
$proj = 'src\DcxManager\DcxManager.csproj'
[xml]$x = Get-Content $proj
# $numeric: the Windows Installer / update version (x.y.z, always increasing). $version: the release's name (file names,
# dist folder) — the csproj's ReleaseLabel when set (e.g. 0.2.0-preview.1 built as 0.1.21), else the numeric version.
$numeric = $x.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$label = $x.Project.PropertyGroup.ReleaseLabel | Where-Object { $_ } | Select-Object -First 1
$version = if ($label) { $label } else { $numeric }
Write-Host "Building DarkCipher ServerManagement $version (installer version $numeric)" -ForegroundColor Cyan
$out = "dist\$version"

# 0. Mandatory release guards (ADR-123, docs\RELEASE-PIPELINE.md "Guards"). Each one stops the build before anything is
#    written to dist\; tools\test-release-guards.ps1 proves them. PowerShell 7 itself is enforced by #Requires above.
. "$PSScriptRoot\tools\release-common.ps1"
Assert-NoHold 'build.ps1'                   # guard 7a, before anything else: DCX_HOLD, dist\HOLD or %USERPROFILE%\.darkcipher\hold
# -Preflight (0.6.54): the whole build as a rehearsal - every step in the same order, then release-check - under the label
# <x.y.z>-preflight.<run> (never releasable: release.ps1 refuses it), into a dist folder outside the repository, and
# without touching the version ledger. A failure costs no version.
$baseVersion = $version
if ($Preflight) {
    $version = "$numeric-preflight.$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $env:DCX_DIST_ROOT = Join-Path $env:LOCALAPPDATA 'DarkCipher\preflight-dist'
    Write-Host "PREFLIGHT $version (for $baseVersion): output in $(Get-DistRoot), nothing recorded in the version ledger" -ForegroundColor Yellow
}
elseif ($env:DCX_DIST_ROOT) { throw 'DCX_DIST_ROOT is set: a release build writes only into this clone''s dist\ (unset it, or use -Preflight)' }
$out = Join-Path (Get-DistRoot) $version
$labelArgs = @(if ($Preflight) { "-p:ReleaseLabel=$version" })
$cfg = Get-ReleaseConfig
Assert-ReleaseRemotes | Out-Null             # first: no remote on a decommissioned server, the gateway origin present
# Generated files are committed, never edited by hand: regenerate them and refuse if the commit doesn't already hold them.
& "$PSScriptRoot\tools\write-readme-version.ps1" -Version $baseVersion
& "$PSScriptRoot\tools\write-tree.ps1" | Out-Null
$stale = @(& $ReleaseGit -C $PSScriptRoot status --porcelain -- README.md docs/PROJECT-TREE.generated.md)
if ($stale.Count) { throw "generated files are out of date ($(($stale | ForEach-Object { $_.Trim() }) -join ', ')): commit them, push to the gateway origin and build from a fresh clone" }
# The privacy scan's private term list is never in git: fetched from the main working clone. Without it, no build.
$termsFile = Join-Path $PSScriptRoot 'tools\privacy-terms.local.txt'
if (-not (Test-Path $termsFile)) {
    $termsSrc = Join-Path (Split-Path $PSScriptRoot -Parent) 'DarkCipher-ServerManagement\tools\privacy-terms.local.txt'
    if (-not (Test-Path $termsSrc)) { throw "privacy guard: tools\privacy-terms.local.txt is missing here and at $termsSrc" }
    Copy-Item $termsSrc $termsFile
}
$commit = Assert-CleanCheckout                 # remotes (gateway origin, no decommissioned server), fresh clone, HEAD on the gateway
Assert-NewVersion $baseVersion $cfg -NoRecord:$Preflight   # never rebuilt: the ledger, every dist\<ver> of the sibling clones, the public tags
$toolchain = Get-ReleaseToolchain              # pwsh 7, .NET SDK, WiX, git, Edge, the pinned gitleaks (installed and verified)
$addonStore = Get-AddonStore $cfg              # the signed add-on packages must be reachable before the version is spent
$preflightResult = Invoke-BuildPreflight             # locked restore + compile-only Release build under the pinned SDK
if ($PreflightOnly) { Write-Host "Pre-flight passed for ${version}: $preflightResult. Nothing recorded: $version is not spent." -ForegroundColor Green; return }
$runId = [guid]::NewGuid().ToString('N')
$env:DCX_PIPELINE_RUN = $runId
if (-not $Preflight) { Add-BuiltVersion $version $commit $runId }   # from here on the version is spent, whatever happens next
Set-ReleaseStateProperty $version 'toolchain' $toolchain
if ($Preflight) { Set-ReleaseStateProperty $version 'preflight' $true }
Set-ReleaseStateProperty $version 'pipeline' ([pscustomobject][ordered]@{
    run = $runId; status = 'running'; commit = $commit; tree = (& $ReleaseGit rev-parse 'HEAD^{tree}').Trim()
    started = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'); finished = $null; sumsSha256 = $null; steps = @()
})
Add-PipelineStep $version 'guards' "remotes ok; clean clone at $commit; $version is new; pre-flight: $preflightResult; toolchain pwsh $($toolchain.pwsh), .NET SDK $($toolchain.dotnetSdk), gitleaks $($toolchain.gitleaks)"
Add-PipelineStep $version 'readme' 'README badge and history, project tree: generated and identical to the commit'
# ADR-117: the vendored CipherChat must be exactly the pinned copy (fixes go to CipherChat, then sync-cipherchat.ps1).
& "$PSScriptRoot\tools\sync-cipherchat.ps1" -Check

# 1. self-test against a throwaway data folder. The solution is built (0.2.0: it grows to several projects); the
#    version and the publish still come from the exe project.
dotnet build DarkCipher.ServerManagement.sln -c Debug -nologo -v q @labelArgs
if ($LASTEXITCODE) { throw 'build failed' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-selftest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$env:DCX_DATA_DIR = Join-Path $tmp 'data'
New-Item -ItemType Directory $env:DCX_DATA_DIR | Out-Null
$exe = Get-ChildItem src\DcxManager\bin\Debug -Recurse -Filter DcxServerManager.exe | Select-Object -First 1
# 0.2.0: a fresh store is a new install, where AI features start off. Run 1 turns them on (the full suite); run 2 starts
# with them off and checks that no AI exists anywhere (the AI-off suite). 0.6.37: CipherChat (ADR-117) and the stream
# manager (ADR-118a) follow the same pattern: on in run 1, off (never loaded) in run 2 and in the published-exe run.
$env:DCX_AI_FEATURES = '1'
$env:DCX_CHAT_FEATURES = '1'
# ADR-120: the stream manager is a module add-on now: packed and signed (release keys) into the scratch folder and
# installed into run 1's store; runs 2 and 3 have none and check nothing of it exists.
& .\samples\addons\pack.ps1 .\samples\addons\stream -OutDir (Join-Path $tmp 'addons') | Out-Host
$env:DCX_SELFTEST_ADDONS = (Get-ChildItem (Join-Path $tmp 'addons') -Filter *.dcxaddon | Select-Object -First 1).FullName
# 0.6.52 (ADR-123, DarkCipher0x's choice): one windowless warm-up launch of the freshly built exe before the measured
# start-up. A fresh release clone means brand-new binaries on a new path: the first launch pays for Windows Defender's
# scan and a cold disk cache (0.6.50 and 0.6.51 measured 4.1-4.2 s against the 4.0 s budget; a long-lived clone, 3.5 s).
# It exports the policy templates (no window, no network) into a scratch folder with its own empty data folder, so the
# self-test's store is still brand new and its start-up is still the app's first start in that store.
$warm = Join-Path $tmp 'warm-up'
New-Item -ItemType Directory (Join-Path $warm 'data') -Force | Out-Null
$dataDir = $env:DCX_DATA_DIR; $env:DCX_DATA_DIR = Join-Path $warm 'data'
$sw = [Diagnostics.Stopwatch]::StartNew()
$wp = Start-Process $exe.FullName -ArgumentList '--export-policy-templates', "`"$warm\policy`"" -PassThru -WindowStyle Hidden
$null = $wp.Handle
if (-not $wp.WaitForExit(180000)) { $wp | Stop-Process -Force; throw 'the warm-up launch timed out' }
$env:DCX_DATA_DIR = $dataDir
if ($wp.ExitCode -ne 0 -or -not (Test-Path "$warm\policy\DarkCipherServerManagement.admx")) { throw "the warm-up launch failed (exit $($wp.ExitCode))" }
Add-PipelineStep $version 'warm-up' ("windowless launch (--export-policy-templates, scratch data folder) in {0:N0} ms" -f $sw.ElapsedMilliseconds)
$p = Start-Process $exe.FullName -ArgumentList '--selftest', "$tmp\report.txt" -PassThru
$null = $p.Handle   # keep the handle, or Windows PowerShell can lose the real exit code
# The full suite takes about 5 minutes since s30 (stability tests); allow 10.
if (-not $p.WaitForExit(600000)) { $p | Stop-Process -Force; throw 'self-test timed out' }
Get-Content "$tmp\report.txt" -Encoding UTF8 | Select-Object -First 1
if ($p.ExitCode -ne 0) { Get-Content "$tmp\report.txt" -Encoding UTF8 | Select-String 'FAIL '; Remove-Item Env:\DCX_DATA_DIR; Remove-Item Env:\DCX_AI_FEATURES; throw 'self-test failed' }
$env:DCX_DATA_DIR = Join-Path $tmp 'data-ai-off'
New-Item -ItemType Directory $env:DCX_DATA_DIR | Out-Null
$env:DCX_AI_FEATURES = '0'
$env:DCX_CHAT_FEATURES = '0'
# ADR-119: Windows management is a built-in module too; the AI-off run checks the app with no module at all.
$env:DCX_WINTOOLS_FEATURES = '0'
$env:DCX_SELFTEST_ADDONS = $null
$p = Start-Process $exe.FullName -ArgumentList '--selftest', "$tmp\report-ai-off.txt" -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(300000)) { $p | Stop-Process -Force; throw 'AI-off self-test timed out' }
Remove-Item Env:\DCX_DATA_DIR
Remove-Item Env:\DCX_AI_FEATURES
Get-Content "$tmp\report-ai-off.txt" -Encoding UTF8 | Select-Object -First 1
if ($p.ExitCode -ne 0) { Get-Content "$tmp\report-ai-off.txt" -Encoding UTF8 | Select-String 'FAIL '; throw 'AI-off self-test failed' }
# 0.2.0 phase 7: start-up time and memory of both runs against tools\budgets.json.
& "$PSScriptRoot\tools\check-budgets.ps1" -Report "$tmp\report.txt" -ReportAiOff "$tmp\report-ai-off.txt"
Add-PipelineStep $version 'selftest' ("{0} | {1}" -f (Get-Content "$tmp\report.txt" -Encoding UTF8 -TotalCount 1), (Get-Content "$tmp\report-ai-off.txt" -Encoding UTF8 -TotalCount 1))
# 0.6.52: the terminal's paste shortcuts in headless Edge (the 0.6.48 double paste must never come back).
Add-PipelineStep $version 'paste-test' (& "$PSScriptRoot\tools\test-terminal-paste.ps1" | Select-Object -Last 1)
# WebView2's helper processes can hold their lockfile for a moment after the exe exits: retry, and never fail the build
# over a scratch folder (s36: the 0.1.12 build stopped here after a passing self-test).
for ($i = 0; $i -lt 10 -and (Test-Path $tmp); $i++) { try { Remove-Item $tmp -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 } }
if (Test-Path $tmp) { Write-Warning "could not delete the self-test scratch folder $tmp (still in use); delete it later" }

# 2. publish (DebugType=none + PathMap keep local paths out of the binary)
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
dotnet publish $proj -c Release -o publish -nologo -v q @labelArgs
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Copy-Item LICENSE publish\LICENSE.txt
Copy-Item THIRD-PARTY-NOTICES.md publish\
if (Get-ChildItem publish -Recurse -Include *.pdb) { throw '.pdb found in publish output' }
& "$PSScriptRoot\tools\check-budgets.ps1" -Exe 'publish\DcxServerManager.exe'
Add-PipelineStep $version 'publish' "DcxServerManager.exe sha256 $((Get-FileHash 'publish\DcxServerManager.exe' -Algorithm SHA256).Hash.ToLower())"

# Optional Authenticode signing (the in-app updater only installs packages signed by a pinned publisher)
$thumb = $env:DCX_SIGN_THUMBPRINT
$signtool = if ($thumb) { Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1 }
function Sign([string]$file) {
    if (-not $thumb) { return }
    if (-not $signtool) { throw 'DCX_SIGN_THUMBPRINT is set but signtool.exe (Windows SDK) was not found' }
    $ts = if ($env:DCX_SIGN_TIMESTAMP) { $env:DCX_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
    & $signtool.FullName sign /sha1 $thumb /fd SHA256 /tr $ts /td SHA256 /q $file
    if ($LASTEXITCODE) { throw "signing failed: $file" }
    if ((Get-AuthenticodeSignature $file).Status -ne 'Valid') { throw "signature did not verify: $file" }
}
Sign 'publish\DcxServerManager.exe'

# 2b. 0.2.0 phase 6g: the published single-file exe itself (what the MSI and zip ship) starts, loads its projects from
#     the bundle and keeps the AI module out — the AI-off suite, about a minute. (The full suite was run against a
#     published exe for 0.2.0-preview.4: 328 ok; the three checks it skips need the source tree.)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-selftest-pub-' + [guid]::NewGuid().ToString('N'))
$env:DCX_DATA_DIR = Join-Path $tmp 'data'
New-Item -ItemType Directory $env:DCX_DATA_DIR -Force | Out-Null
$env:DCX_AI_FEATURES = '0'
$p = Start-Process (Resolve-Path 'publish\DcxServerManager.exe').Path -ArgumentList '--selftest', "$tmp\report-published.txt" -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(300000)) { $p | Stop-Process -Force; Remove-Item Env:\DCX_DATA_DIR; Remove-Item Env:\DCX_AI_FEATURES; throw 'published-exe self-test timed out' }
Remove-Item Env:\DCX_DATA_DIR
Remove-Item Env:\DCX_AI_FEATURES
Remove-Item Env:\DCX_CHAT_FEATURES, Env:\DCX_WINTOOLS_FEATURES, Env:\DCX_SELFTEST_ADDONS -ErrorAction SilentlyContinue
Get-Content "$tmp\report-published.txt" -Encoding UTF8 | Select-Object -First 1
if ($p.ExitCode -ne 0) { Get-Content "$tmp\report-published.txt" -Encoding UTF8 | Select-String 'FAIL '; throw 'published-exe self-test failed' }
Add-PipelineStep $version 'selftest-published' (Get-Content "$tmp\report-published.txt" -Encoding UTF8 -TotalCount 1)
for ($i = 0; $i -lt 10 -and (Test-Path $tmp); $i++) { try { Remove-Item $tmp -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 } }

# 3. MSI installer (WiX 5; tool pinned in .config\dotnet-tools.json)
dotnet tool restore | Out-Null
dotnet tool run wix extension add -g WixToolset.UI.wixext/5.0.2 | Out-Null
$msi = "$out\DarkCipherServerManagement-$version-x64.msi"
dotnet tool run wix build installer\DarkCipherServerManagement.wxs -arch x64 -ext WixToolset.UI.wixext `
    -d Version=$numeric -d PublishDir="$PSScriptRoot\publish" -wx -o $msi
if ($LASTEXITCODE) { throw 'wix build failed' }
Remove-Item "$out\*.wixpdb" -ErrorAction SilentlyContinue
Sign $msi
& "$PSScriptRoot\tools\check-budgets.ps1" -Msi $msi
Add-PipelineStep $version 'msi' "sha256 $((Get-FileHash $msi -Algorithm SHA256).Hash.ToLower())"
if (-not $thumb) { Write-Host 'No Authenticode signature (no DCX_SIGN_THUMBPRINT): the in-app updater trusts this build through the release key''s signature on update.json (ADR-079); Windows SmartScreen may still warn on a downloaded copy.' -ForegroundColor Yellow }

# 3b. The themed setup program around the MSI (installer\Setup, the DarkCipher Setup template; README there). The MSI stays
#     the deployable package; DarkCipherServerManagement-<ver>-Setup.exe is the double-click download. Its setup app is
#     built (locked restore), its config is checked (setup-app.json parses, every option and detection is valid), then
#     the bundle is built around this MSI. Never named plain "Setup.exe": WiX refuses it (Windows' compatibility shims).
dotnet build installer\Setup\DarkCipher.Setup.csproj -c Release -nologo -v q | Out-Null
if ($LASTEXITCODE) { throw 'setup program build failed' }
$setupBin = "$PSScriptRoot\installer\Setup\bin\Release\net48\win-x64"
$check = & "$setupBin\DarkCipher.Setup.exe" --check "$PSScriptRoot\installer"
if ($LASTEXITCODE) { throw "setup-app.json check failed: $check" }
Write-Host ($check | Select-Object -Last 1)
$setupExe = "$out\DarkCipherServerManagement-$version-Setup.exe"
dotnet tool run wix build installer\Setup\Bundle.wxs -d Version=$numeric -d AppRoot="$PSScriptRoot" -d MsiPath="$msi" `
    -d SetupBin="$setupBin" -d AppInclude="$PSScriptRoot\installer\setup-app.wxi" -wx -o $setupExe
if ($LASTEXITCODE) { throw 'setup bundle build failed' }
Remove-Item "$out\*.wixpdb" -ErrorAction SilentlyContinue
if ($thumb) {
    # A bundle is signed in two places: its engine (detached, signed, reattached), then the whole file.
    $engine = "$out\setup-engine.exe"
    dotnet tool run wix burn detach $setupExe -engine $engine; if ($LASTEXITCODE) { throw 'wix burn detach failed' }
    Sign $engine
    dotnet tool run wix burn reattach $setupExe -engine $engine -o $setupExe; if ($LASTEXITCODE) { throw 'wix burn reattach failed' }
    Remove-Item $engine
}
Sign $setupExe
Add-PipelineStep $version 'setup' "sha256 $((Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower())"

# 4. update manifest (Settings -> Updates). Publish it with the MSI in the same folder on the website.
$notesFile = "docs\release-notes\$baseVersion.md"
$manifest = [ordered]@{
    product      = 'DarkCipher ServerManagement'
    version      = $numeric
    label        = $version
    released     = (Get-Date).ToString('yyyy-MM-dd')
    notes        = if (Test-Path $notesFile) { (Get-Content $notesFile -Raw -Encoding UTF8).Trim() } else { '' }
    schemaChange = [bool]$env:DCX_SCHEMA_CHANGE
    signed       = [bool]$thumb
    packages     = @([ordered]@{
        kind = 'msi'; arch = 'x64'; file = (Split-Path $msi -Leaf); url = (Split-Path $msi -Leaf)
        bytes = (Get-Item $msi).Length; sha256 = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()
    }, [ordered]@{
        # 0.6.1: PCs installed with the setup program update with the setup program (signed manifests only).
        kind = 'setup'; arch = 'x64'; file = (Split-Path $setupExe -Leaf); url = (Split-Path $setupExe -Leaf)
        bytes = (Get-Item $setupExe).Length; sha256 = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()
    })
}
[IO.File]::WriteAllText("$out\update.json", ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
# 0.6.1 (ADR-079): update.json.sig, the release key's signature the app checks before it offers anything. The key is
# in this Windows account's key store (tools\sign-update.ps1); without it the build stops rather than ship unsigned.
& "$PSScriptRoot\tools\sign-update.ps1" -Path "$out\update.json"
if (Test-Path $notesFile) { Copy-Item $notesFile "$out\DarkCipherServerManagement-$version-release-notes.md" }
Add-PipelineStep $version 'update-manifest' 'update.json signed (ECDSA P-256 and ML-DSA-65)'

$zip = "$out\DarkCipherServerManagement-$version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
. "$PSScriptRoot\tools\zip-tools.ps1"   # 0.6.23: forward-slash entry names (Compress-Archive writes backslashes)
New-ReleaseZip -Source publish -Zip $zip
Add-PipelineStep $version 'portable' "sha256 $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"

# 4a. 0.3.0 §10: the Group Policy templates (ADMX + en-US ADML) and the policy reference, from the published exe's catalog;
#     also zipped so the checksums cover them. docs\POLICIES.md is kept equal to it (the self-test checks).
$pol = "$out\policy"
if (Test-Path $pol) { Remove-Item $pol -Recurse -Force }
$pp = Start-Process -FilePath "publish\DcxServerManager.exe" -ArgumentList '--export-policy-templates', "`"$pol`"" -Wait -PassThru -WindowStyle Hidden
if ($pp.ExitCode -ne 0 -or -not (Test-Path "$pol\DarkCipherServerManagement.admx")) { throw 'policy templates were not written' }
$polZip = "$out\DarkCipherServerManagement-$version-policy-templates.zip"
if (Test-Path $polZip) { Remove-Item $polZip }
New-ReleaseZip -Source $pol -Zip $polZip
Add-PipelineStep $version 'policy' "sha256 $((Get-FileHash $polZip -Algorithm SHA256).Hash.ToLower())"

# 4b. 0.2.0 phase 7: a CycloneDX SBOM of everything the release bundles (from the locked package versions).
& "$PSScriptRoot\tools\write-sbom.ps1" -Version $version -Numeric $numeric
Add-PipelineStep $version 'sbom'

# 4c. The signed add-on packages released with the app (release.json publishedAddons): fetched from the add-on store,
#     checked against their .sha256 and the SHA-256 pinned in release.json. Missing or different fails the build.
$addons = Copy-PublishedAddons $cfg $version $addonStore
Add-PipelineStep $version 'addons' (($addons -join '; ') + " from $addonStore")

# 5. checksums to upload with the files: "<sha256>  <name>" (sha256sum format), one per file plus SHA256SUMS for the version.
& "$PSScriptRoot\tools\write-checksums.ps1" -Version $version
Add-PipelineStep $version 'checksums'

# 6. The release bundle (0.4.0 rule): the documentation set as PDFs (complete guide, user, developer, business, the security
#    report with the penetration tests, the project structure with the generated tree) and the sanitised public source
#    archive (every tracked file minus the maintainers' notes, privacy-scanned). A release without all of them is no release.
& "$PSScriptRoot\tools\make-guide.ps1" -Version $version -All -NoChecksums
Add-PipelineStep $version 'guides'
& "$PSScriptRoot\tools\write-public-source.ps1" -Version $version -NoChecksums
Add-PipelineStep $version 'public-source' "sha256 $((Get-FileHash "$out\DarkCipherServerManagement-$version-source.zip" -Algorithm SHA256).Hash.ToLower())"
$bundle = @('Guide', 'User-Guide', 'Developer-Guide', 'Business-Guide', 'Security-Report', 'Project-Structure') | ForEach-Object { "DarkCipherServerManagement-$version-$_.pdf" }
$bundle += "DarkCipherServerManagement-$version-source.zip", "DarkCipherServerManagement-$version-x64.msi", "DarkCipherServerManagement-$version-Setup.exe", "DarkCipherServerManagement-$version-x64-portable.zip", "DarkCipherServerManagement-$version-sbom.cdx.json", "DarkCipherServerManagement-$version-policy-templates.zip", "DarkCipherServerManagement-$version-release-notes.md"
$missing = $bundle | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "release bundle incomplete: $($missing -join ', ')" }
& "$PSScriptRoot\tools\write-checksums.ps1" -Version $version   # once more: the PDFs and the source archive get theirs too
# The tree must still be exactly the release commit (the generated files above are regenerated identically).
$after = @(& $ReleaseGit -C $PSScriptRoot status --porcelain --untracked-files=all)
if ($after.Count) { throw "the build changed tracked files ($(($after | Select-Object -First 3 | ForEach-Object { $_.Trim() }) -join ', ')): the source archive no longer matches commit $commit. Fix, commit, bump" }
$sumsPath = "$out\DarkCipherServerManagement-$version-SHA256SUMS.txt"
Add-PipelineStep $version 'bundle' "$($bundle.Count) files; SHA256SUMS sha256 $((Get-FileHash $sumsPath -Algorithm SHA256).Hash.ToLower())"
$pl = (Read-ReleaseState $version).pipeline
$pl.status = 'complete'; $pl.finished = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'); $pl.sumsSha256 = (Get-FileHash $sumsPath -Algorithm SHA256).Hash.ToLower()
Set-ReleaseStateProperty $version 'pipeline' $pl
Write-ReleaseStep $version 'build' 'done' "run $runId; commit $commit; SHA256SUMS $($pl.sumsSha256)"
Write-Host "Release bundle complete: $($bundle.Count) files" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

# 7. The release pipeline (docs\RELEASE-PIPELINE.md): checks, public mirror and tag, GitHub Release; a record per version.
if ($Preflight) {
    # The rehearsal ends with the pre-publish checks on its own output; it is never published.
    [IO.File]::WriteAllText((Join-Path $out 'PREFLIGHT-NOT-FOR-RELEASE.txt'), "Preflight build $version of $baseVersion (run $runId, commit $commit): a rehearsal, never released.`n", (New-Object Text.UTF8Encoding $false))
    & "$PSScriptRoot\tools\release-check.ps1" -Version $version
    Write-Host "PREFLIGHT PASSED: $version (for $baseVersion) built and checked end to end in $out. $baseVersion is not spent." -ForegroundColor Green
    return
}
if ($NoPublish) { Write-Host "Not published (-NoPublish): .\tools\release.ps1 -Version $version (add -DryRun to rehearse)" -ForegroundColor Yellow }
else { & "$PSScriptRoot\tools\release.ps1" -Version $version -DryRun:$DryRun }
