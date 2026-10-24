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
param([switch]$NoPublish, [switch]$DryRun)
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
# Every build is a new version and earlier installers are kept: refuse to overwrite one that already exists.
$out = "dist\$version"
if ((Test-Path "$out\DarkCipherServerManagement-$version-x64.msi") -or (Test-Path "dist\DarkCipherServerManagement-$version-x64.msi")) { throw "dist already has the $version MSI - bump <Version> in $proj first (older MSIs are kept)" }
# The README's version badge and Release history entry come from the version and its release notes, never by hand.
& "$PSScriptRoot\tools\write-readme-version.ps1" -Version $version

# 1. self-test against a throwaway data folder. The solution is built (0.2.0: it grows to several projects); the
#    version and the publish still come from the exe project.
dotnet build DarkCipher.ServerManagement.sln -c Debug -nologo -v q
if ($LASTEXITCODE) { throw 'build failed' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-selftest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$env:DCX_DATA_DIR = Join-Path $tmp 'data'
New-Item -ItemType Directory $env:DCX_DATA_DIR | Out-Null
$exe = Get-ChildItem src\DcxManager\bin\Debug -Recurse -Filter DcxServerManager.exe | Select-Object -First 1
# 0.2.0: a fresh store is a new install, where AI features start off. Run 1 turns them on (the full suite); run 2 starts
# with them off and checks that no AI exists anywhere (the AI-off suite).
$env:DCX_AI_FEATURES = '1'
$p = Start-Process $exe.FullName -ArgumentList '--selftest', "$tmp\report.txt" -PassThru
$null = $p.Handle   # keep the handle, or Windows PowerShell can lose the real exit code
# The full suite takes about 5 minutes since s30 (stability tests); allow 10.
if (-not $p.WaitForExit(600000)) { $p | Stop-Process -Force; throw 'self-test timed out' }
Get-Content "$tmp\report.txt" -Encoding UTF8 | Select-Object -First 1
if ($p.ExitCode -ne 0) { Get-Content "$tmp\report.txt" -Encoding UTF8 | Select-String 'FAIL '; Remove-Item Env:\DCX_DATA_DIR; Remove-Item Env:\DCX_AI_FEATURES; throw 'self-test failed' }
$env:DCX_DATA_DIR = Join-Path $tmp 'data-ai-off'
New-Item -ItemType Directory $env:DCX_DATA_DIR | Out-Null
$env:DCX_AI_FEATURES = '0'
$p = Start-Process $exe.FullName -ArgumentList '--selftest', "$tmp\report-ai-off.txt" -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(300000)) { $p | Stop-Process -Force; throw 'AI-off self-test timed out' }
Remove-Item Env:\DCX_DATA_DIR
Remove-Item Env:\DCX_AI_FEATURES
Get-Content "$tmp\report-ai-off.txt" -Encoding UTF8 | Select-Object -First 1
if ($p.ExitCode -ne 0) { Get-Content "$tmp\report-ai-off.txt" -Encoding UTF8 | Select-String 'FAIL '; throw 'AI-off self-test failed' }
# 0.2.0 phase 7: start-up time and memory of both runs against tools\budgets.json.
& "$PSScriptRoot\tools\check-budgets.ps1" -Report "$tmp\report.txt" -ReportAiOff "$tmp\report-ai-off.txt"
# WebView2's helper processes can hold their lockfile for a moment after the exe exits: retry, and never fail the build
# over a scratch folder (s36: the 0.1.12 build stopped here after a passing self-test).
for ($i = 0; $i -lt 10 -and (Test-Path $tmp); $i++) { try { Remove-Item $tmp -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 } }
if (Test-Path $tmp) { Write-Warning "could not delete the self-test scratch folder $tmp (still in use); delete it later" }

# 2. publish (DebugType=none + PathMap keep local paths out of the binary)
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
dotnet publish $proj -c Release -o publish -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Copy-Item LICENSE publish\LICENSE.txt
Copy-Item THIRD-PARTY-NOTICES.md publish\
if (Get-ChildItem publish -Recurse -Include *.pdb) { throw '.pdb found in publish output' }
& "$PSScriptRoot\tools\check-budgets.ps1" -Exe 'publish\DcxServerManager.exe'

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
Get-Content "$tmp\report-published.txt" -Encoding UTF8 | Select-Object -First 1
if ($p.ExitCode -ne 0) { Get-Content "$tmp\report-published.txt" -Encoding UTF8 | Select-String 'FAIL '; throw 'published-exe self-test failed' }
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
dotnet tool run wix build installer\Setup\Bundle.wxs -d Version=$numeric -d AppRoot="$PSScriptRoot" -d MsiPath="$PSScriptRoot\$msi" `
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

# 4. update manifest (Settings -> Updates). Publish it with the MSI in the same folder on the website.
$notesFile = "docs\release-notes\$version.md"
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
[IO.File]::WriteAllText("$PSScriptRoot\$out\update.json", ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
# 0.6.1 (ADR-079): update.json.sig, the release key's signature the app checks before it offers anything. The key is
# in this Windows account's key store (tools\sign-update.ps1); without it the build stops rather than ship unsigned.
& "$PSScriptRoot\tools\sign-update.ps1" -Path "$PSScriptRoot\$out\update.json"
if (Test-Path $notesFile) { Copy-Item $notesFile "$out\DarkCipherServerManagement-$version-release-notes.md" }

$zip = "$out\DarkCipherServerManagement-$version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
. "$PSScriptRoot\tools\zip-tools.ps1"   # 0.6.23: forward-slash entry names (Compress-Archive writes backslashes)
New-ReleaseZip -Source publish -Zip $zip

# 4a. 0.3.0 §10: the Group Policy templates (ADMX + en-US ADML) and the policy reference, from the published exe's catalog;
#     also zipped so the checksums cover them. docs\POLICIES.md is kept equal to it (the self-test checks).
$pol = "$out\policy"
if (Test-Path $pol) { Remove-Item $pol -Recurse -Force }
$pp = Start-Process -FilePath "publish\DcxServerManager.exe" -ArgumentList '--export-policy-templates', "`"$PSScriptRoot\$pol`"" -Wait -PassThru -WindowStyle Hidden
if ($pp.ExitCode -ne 0 -or -not (Test-Path "$pol\DarkCipherServerManagement.admx")) { throw 'policy templates were not written' }
$polZip = "$out\DarkCipherServerManagement-$version-policy-templates.zip"
if (Test-Path $polZip) { Remove-Item $polZip }
New-ReleaseZip -Source $pol -Zip $polZip

# 4b. 0.2.0 phase 7: a CycloneDX SBOM of everything the release bundles (from the locked package versions).
& "$PSScriptRoot\tools\write-sbom.ps1" -Version $version -Numeric $numeric

# 5. checksums to upload with the files: "<sha256>  <name>" (sha256sum format), one per file plus SHA256SUMS for the version.
& "$PSScriptRoot\tools\write-checksums.ps1" -Version $version

# 6. The release bundle (0.4.0 rule): the documentation set as PDFs (complete guide, user, developer, business, the security
#    report with the penetration tests, the project structure with the generated tree) and the sanitised public source
#    archive (every tracked file minus the maintainers' notes, privacy-scanned). A release without all of them is no release.
& "$PSScriptRoot\tools\make-guide.ps1" -Version $version -All -NoChecksums
& "$PSScriptRoot\tools\write-public-source.ps1" -Version $version -NoChecksums
$bundle = @('Guide', 'User-Guide', 'Developer-Guide', 'Business-Guide', 'Security-Report', 'Project-Structure') | ForEach-Object { "DarkCipherServerManagement-$version-$_.pdf" }
$bundle += "DarkCipherServerManagement-$version-source.zip", "DarkCipherServerManagement-$version-x64.msi", "DarkCipherServerManagement-$version-Setup.exe", "DarkCipherServerManagement-$version-x64-portable.zip", "DarkCipherServerManagement-$version-sbom.cdx.json", "DarkCipherServerManagement-$version-policy-templates.zip", "DarkCipherServerManagement-$version-release-notes.md"
$missing = $bundle | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "release bundle incomplete: $($missing -join ', ')" }
& "$PSScriptRoot\tools\write-checksums.ps1" -Version $version   # once more: the PDFs and the source archive get theirs too
Write-Host "Release bundle complete: $($bundle.Count) files" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

# 7. The release pipeline (docs\RELEASE-PIPELINE.md): checks, public mirror and tag, GitHub Release; a record per version.
if ($NoPublish) { Write-Host "Not published (-NoPublish): .\tools\release.ps1 -Version $version (add -DryRun to rehearse)" -ForegroundColor Yellow }
else { & "$PSScriptRoot\tools\release.ps1" -Version $version -DryRun:$DryRun }
