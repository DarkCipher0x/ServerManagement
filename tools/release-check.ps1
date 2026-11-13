#Requires -Version 7
# Release pipeline step 2: the pre-publish checks (docs\RELEASE-PIPELINE.md). Nothing is published unless all pass.
#   - the version agrees everywhere: the csproj, the MSI's ProductVersion, the exe inside the portable zip, update.json,
#     the release notes file and the README (badge and Release history, written by tools\write-readme-version.ps1);
#   - every bundle file is present (tools\release.json "releaseAssets", update.json, the release notes copy);
#   - every .sha256 and SHA256SUMS line matches its file, and every published file has one;
#   - the tests passed: build.ps1 packages nothing after a failed self-test, so a complete bundle means a green run.
# Read-only; safe to run any number of times. Usage: .\tools\release-check.ps1 -Version 0.5.3
# -Backfill: releasing an older version whose dist\<ver>\ is complete while the tree has moved on — the csproj and the
# README badge are not compared (the history entry and every built file still are).
param([Parameter(Mandatory)][string]$Version, [switch]$Backfill)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$cfg = Get-ReleaseConfig
$out = Join-Path (Get-DistRoot) $Version
# A preflight build (<x.y.z>-preflight.<run>) is checked against its base version for the csproj, notes and README.
$baseVersion = if ($Version -match '^(\d+\.\d+\.\d+)-preflight\.') { $Matches[1] } else { $Version }
$fail = New-Object System.Collections.Generic.List[string]
function Need([bool]$ok, [string]$what) { if (-not $ok) { $fail.Add($what) } }

# --- the version, everywhere --------------------------------------------------------------------------------------------
[xml]$proj = [IO.File]::ReadAllText((Join-Path $ReleaseRoot 'src\DcxManager\DcxManager.csproj'))
$numeric = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$label = $proj.Project.PropertyGroup.ReleaseLabel | Where-Object { $_ } | Select-Object -First 1
$csprojVersion = if ($label) { $label } else { $numeric }
if (-not $Backfill) { Need ($csprojVersion -eq $baseVersion) "the csproj is at $csprojVersion, not $baseVersion" }
Need (Test-Path (Join-Path $ReleaseRoot "docs\release-notes\$baseVersion.md")) "docs\release-notes\$baseVersion.md is missing"
$readme = [IO.File]::ReadAllText((Join-Path $ReleaseRoot 'README.md'))
if (-not $Backfill) { Need ($readme.Contains("badge/version-$baseVersion-")) "README's version badge isn't $baseVersion (run tools\write-readme-version.ps1)" }
Need ([regex]::IsMatch($readme, '\*\*' + [regex]::Escape($baseVersion) + ':?\*\*')) "README's Release history has no $baseVersion entry (run tools\write-readme-version.ps1)"
# 0.6.52 (reviewer F7): every 0.6.x release with notes has its history entry (0.6.43-0.6.46 had gone missing).
foreach ($n in Get-ChildItem (Join-Path $ReleaseRoot 'docs\release-notes') -Filter '0.6.*.md') {
    $v = [IO.Path]::GetFileNameWithoutExtension($n.Name)
    Need ([regex]::IsMatch($readme, '\*\*' + [regex]::Escape($v) + ':?\*\*')) "README's Release history has no entry for $v (docs\release-notes\$v.md exists)"
}

$updateJson = Join-Path $out 'update.json'
if (Test-Path $updateJson) { $uj = [IO.File]::ReadAllText($updateJson) | ConvertFrom-Json; Need ($uj.version -eq $Version -or $uj.label -eq $Version) "update.json names another version" }
else { $fail.Add('update.json is missing') }
# 0.6.1 (ADR-079): installed copies offer an update only when update.json.sig verifies against the pinned release key.
if ((Test-Path $updateJson) -and -not $Backfill) {
    try { & (Join-Path $PSScriptRoot 'sign-update.ps1') -Verify $updateJson -PinnedFrom (Join-Path $ReleaseRoot 'src\Dcx.Core\Services\Updater.cs') | Out-Null }
    catch { $fail.Add("update.json's signature: $($_.Exception.Message)") }
}

$msi = Join-Path $out "DarkCipherServerManagement-$Version-x64.msi"
if (Test-Path $msi) {
    # The Windows Installer API, read-only (COM late binding from PowerShell 5.1 fails with a type mismatch).
    if (-not ('DcxMsi' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class DcxMsi {
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr rec);
    [DllImport("msi.dll")] static extern uint MsiViewFetch(IntPtr view, out IntPtr rec);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordGetStringW(IntPtr rec, uint field, StringBuilder buf, ref uint len);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr h);
    public static string Property(string path, string name) {
        IntPtr db, view = IntPtr.Zero, rec = IntPtr.Zero;
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out db) != 0) throw new InvalidOperationException("cannot open " + path);
        try {
            if (MsiDatabaseOpenViewW(db, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'", out view) != 0) throw new InvalidOperationException("query failed");
            MsiViewExecute(view, IntPtr.Zero);
            if (MsiViewFetch(view, out rec) != 0) return null;
            var sb = new StringBuilder(256); uint len = 256;
            MsiRecordGetStringW(rec, 1, sb, ref len);
            return sb.ToString();
        } finally { if (rec != IntPtr.Zero) MsiCloseHandle(rec); if (view != IntPtr.Zero) MsiCloseHandle(view); MsiCloseHandle(db); }
    }
}
'@
    }
    $msiVersion = [DcxMsi]::Property($msi, 'ProductVersion')
    Need ($msiVersion -eq $numeric -or ($Backfill -and $msiVersion -eq $Version)) "the MSI's ProductVersion is $msiVersion, not $(if ($Backfill) { $Version } else { $numeric })"
}

$portable = Join-Path $out "DarkCipherServerManagement-$Version-x64-portable.zip"
$bundled = [ordered]@{}; $exeSha = $null
if (Test-Path $portable) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($portable)
    try {
        $e = $z.Entries | Where-Object { $_.Name -eq 'DcxServerManager.exe' } | Select-Object -First 1
        if (-not $e) { $fail.Add('the portable zip has no DcxServerManager.exe') }
        else {
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-exe-' + [guid]::NewGuid().ToString('N') + '.exe')
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $tmp)
            try {
                $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($tmp).ProductVersion
                $exeSha = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
                # Runtime guard (0.6.52): the self-contained runtime must be at least release.json "minBundledRuntime" (the
                # newest .NET security release), read from the exe's embedded dependency manifest.
                $exeText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($tmp))
                foreach ($rtName in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
                    $rtv = @([regex]::Matches($exeText, [regex]::Escape("$rtName.Runtime.win-x64/") + '(\d+\.\d+\.\d+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
                    if ($rtv.Count -ne 1) { $fail.Add("runtime guard: the exe names $($rtv.Count) versions of $rtName ($($rtv -join ', '))"); continue }
                    $bundled["$rtName"] = $rtv[0]
                    if ($cfg.minBundledRuntime) { Need ([version]$rtv[0] -ge [version]$cfg.minBundledRuntime.version) "runtime guard: the exe bundles $rtName $($rtv[0]), older than the minimum $($cfg.minBundledRuntime.version) ($($cfg.minBundledRuntime.reason)): install the newer .NET SDK and build a new version" }
                    else { $fail.Add('runtime guard: release.json has no minBundledRuntime') }
                }
            }
            finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
            Need (($exeVersion -replace '\+.*$', '') -eq $Version) "the exe in the portable zip says $exeVersion, not $Version"
        }
    }
    finally { $z.Dispose() }
}

# --- the bundle and its checksums ---------------------------------------------------------------------------------------
$addonAssets = Get-PublishedAddonAssets $cfg
$assets = @($cfg.releaseAssets | ForEach-Object { $_.Replace('{ver}', $Version) }) + $addonAssets
$expected = @($assets) + "DarkCipherServerManagement-$Version-release-notes.md"
foreach ($n in $expected) { Need (Test-Path (Join-Path $out $n)) "$n is missing from dist\$Version" }
foreach ($n in $assets) {
    $f = Join-Path $out $n; $s = "$f.sha256"
    if (-not (Test-Path $f)) { continue }
    $actual = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not (Test-Path $s)) { $fail.Add("$n has no .sha256"); continue }
    $recorded = ([IO.File]::ReadAllText($s).Trim() -split '\s+')[0].ToLowerInvariant()
    Need ($recorded -eq $actual) "$n does not match its .sha256"
}
$sums = Join-Path $out "DarkCipherServerManagement-$Version-SHA256SUMS.txt"
if (Test-Path $sums) {
    foreach ($line in [IO.File]::ReadAllLines($sums)) {
        if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { continue }
        $f = Join-Path $out $Matches[2].Trim()
        if (Test-Path $f) { Need ((Get-FileHash $f -Algorithm SHA256).Hash -eq $Matches[1].ToUpperInvariant()) "SHA256SUMS: $($Matches[2]) does not match" }
        else { $fail.Add("SHA256SUMS names $($Matches[2]), which is missing") }
    }
    $listed = [IO.File]::ReadAllLines($sums) | ForEach-Object { if ($_ -match '^[0-9a-fA-F]{64}\s+\*?(.+)$') { $Matches[1].Trim() } }
    foreach ($n in $assets) { Need ($listed -contains $n) "SHA256SUMS does not list $n" }
}
else { $fail.Add('SHA256SUMS is missing') }

# --- add-ons are never published (DarkCipher0x, 2026-10-24: "make sure that the addons are NOT published"): no packed add-on
# among the release assets, and none inside the source zip; no file named after a private term (the git-ignored
# tools\privacy-terms.local.txt, the same list the privacy scan uses; a private theme add-on above all). The add-on SDK
# and its generic samples (samples\addons) are source and stay; packed add-ons live only in dist\addons\ (git ignores it).
$terms = @()
$termsFile = Join-Path $PSScriptRoot 'privacy-terms.local.txt'
if (Test-Path $termsFile) { $terms = [IO.File]::ReadAllLines($termsFile) | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('#') } }
function Test-PrivateName([string]$name) { foreach ($t in $terms) { if ($name.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }; return $false }
foreach ($n in @($cfg.releaseAssets)) { Need ($n -notmatch '(?i)addon' -and -not (Test-PrivateName $n)) "release asset '$n' looks like an add-on or a private name: add-ons are never published" }
# DarkCipher0x's exceptions (release.json publishedAddons): only signed module packages, never a private name.
foreach ($n in $addonAssets) {
    Need (-not (Test-PrivateName $n)) "published add-on '$n' has a private name"
    $p = Join-Path $out $n
    if (-not (Test-Path $p)) { continue }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [IO.Compression.ZipFile]::OpenRead($p)
    try { $names = @($za.Entries | ForEach-Object FullName) } finally { $za.Dispose() }
    foreach ($req in 'addon.json', 'module.json', 'module.json.sig', 'module.json.pq.sig') { Need ($names -contains $req) "published add-on '$n' has no $req (only signed module packages are published)" }
    Need (-not ($names | Where-Object { (Test-PrivateName $_) -or $_ -match '(?i)\.(pdb|cs|csproj)$' })) "published add-on '$n' holds source, symbols or a private name"
}
# 0.6.23: every release zip names its entries with forward slashes (the ZIP specification; the 0.6.22 zips had
# backslashes, which Linux and macOS tools mangle, and which the dist/ rule below couldn't see).
. (Join-Path $PSScriptRoot 'zip-tools.ps1')
foreach ($zn in "DarkCipherServerManagement-$Version-source.zip", "DarkCipherServerManagement-$Version-x64-portable.zip", "DarkCipherServerManagement-$Version-policy-templates.zip") {
    $zp = Join-Path $out $zn
    if (Test-Path $zp) { $bs = @(Get-BackslashEntries $zp); Need ($bs.Count -eq 0) "$zn names $($bs.Count) entr(ies) with backslashes (e.g. '$($bs | Select-Object -First 1)'): write it with tools\zip-tools.ps1" }
}
$srcZip = Join-Path $out "DarkCipherServerManagement-$Version-source.zip"
if (Test-Path $srcZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($srcZip)
    try {
        foreach ($e in $z.Entries) {
            Need ($e.FullName -notmatch '(?i)\.dcxaddon$|(^|/)dist/' -and -not (Test-PrivateName $e.FullName)) "the source zip holds '$($e.FullName)': packed add-ons, dist\ and private names are never published"
        }
    }
    finally { $z.Dispose() }
}

# --- Mandatory release guards (ADR-123; docs\RELEASE-PIPELINE.md "Guards") ----------------------------------------------
# Remotes: this repository has the gateway origin and no remote on a decommissioned server.
try { Assert-ReleaseRemotes | Out-Null } catch { $fail.Add($_.Exception.Message) }
# gitleaks: pinned, installed and unchanged (the public step runs it; an absent gitleaks already fails here).
try { Get-PinnedGitleaks | Out-Null } catch { $fail.Add($_.Exception.Message) }

# No hand-run steps: one complete build.ps1 run recorded every step in order, nothing was run by hand for this version
# afterwards, and SHA256SUMS is still the one that run wrote.
$state = Read-ReleaseState $Version
$pl = $state.pipeline
if (-not $pl) { $fail.Add("pipeline record: release-state.json has no build.ps1 run for $Version (built by hand or before ADR-123): bump the version and build") }
else {
    Need ($pl.status -eq 'complete') "pipeline record: the build.ps1 run $($pl.run) did not complete (status '$($pl.status)')"
    $got = @($pl.steps | ForEach-Object step)
    Need (($got -join ',') -eq ($PipelineSteps -join ',')) "pipeline record: the run recorded [$($got -join ', ')], expected [$($PipelineSteps -join ', ')]"
    $b = $state.steps.PSObject.Properties['build']
    Need ($b -and $b.Value.status -eq 'done' -and "$($b.Value.evidence)".StartsWith("run $($pl.run);")) "pipeline record: the 'build' step isn't recorded as done by run $($pl.run)"
    $tc = $state.toolchain
    Need ($tc -and "$($tc.pwsh)" -match '^(\d+)\.' -and [int]$Matches[1] -ge 7) "toolchain record: missing, or not PowerShell 7 ('$($tc.pwsh)')"
    Need ($tc -and $tc.dotnetSdk -and $tc.gitleaks -and $tc.edge) "toolchain record: the .NET SDK, Edge or gitleaks version is missing"
    if (Test-Path $sums) { Need ((Get-FileHash $sums -Algorithm SHA256).Hash.ToLowerInvariant() -eq $pl.sumsSha256) "pipeline record: SHA256SUMS changed after the build run (a step was re-run by hand)" }
}
$tainted = @($state.steps.PSObject.Properties | Where-Object { $_.Value.status -eq 'tainted' } | ForEach-Object Name)
Need ($tainted.Count -eq 0) "no hand-run steps: $($tainted -join ', ') for $Version - bump the version and build again"

# Encoding gate: no mojibake in the generated docs, any text file in dist\<ver>\, any text entry of the release zips and
# published add-ons, or the text of any PDF. tools\encoding-exceptions.txt pins the few genuine hits (path, count, reason).
$exceptions = Get-EncodingExceptions
$enc = New-Object System.Collections.Generic.List[string]
$binaryExt = '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.webp', '.dll', '.exe', '.pdb', '.pdf', '.zip', '.dcxaddon', '.msi', '.msix', '.cab', '.woff', '.woff2', '.ttf', '.otf', '.sig', '.bin', '.onnx', '.gz', '.7z', '.nupkg'
function Test-IsText([byte[]]$bytes) { [Array]::IndexOf($bytes, [byte]0) -lt 0 }
foreach ($rel in 'README.md', 'docs\PROJECT-TREE.generated.md', 'docs\POLICIES.md', "docs\release-notes\$Version.md") {
    $f = Join-Path $ReleaseRoot $rel
    if (Test-Path $f) { foreach ($x in Test-TextEncoding $rel ($rel -replace '\\', '/') ([IO.File]::ReadAllText($f)) $exceptions) { $enc.Add($x) } }
}
foreach ($f in Get-ChildItem $out -Recurse -File | Where-Object { $_.Extension -notin $binaryExt -and $_.Length -lt 20MB }) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    if (-not (Test-IsText $bytes)) { continue }
    $rel = $f.FullName.Substring($out.Length + 1)
    foreach ($x in Test-TextEncoding "dist\$Version\$rel" "dist/$($rel -replace '\\', '/')" ([Text.Encoding]::UTF8.GetString($bytes)) $exceptions) { $enc.Add($x) }
}
$zips = @(@{ Kind = 'source'; Name = "DarkCipherServerManagement-$Version-source.zip" }, @{ Kind = 'portable'; Name = "DarkCipherServerManagement-$Version-x64-portable.zip" },
          @{ Kind = 'policy-templates'; Name = "DarkCipherServerManagement-$Version-policy-templates.zip" }) + @($addonAssets | ForEach-Object { @{ Kind = 'addon'; Name = $_ } })
$zipTexts = 0
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($zz in $zips) {
    $zp = Join-Path $out $zz.Name
    if (-not (Test-Path $zp)) { continue }
    $z = [IO.Compression.ZipFile]::OpenRead($zp)
    try {
        foreach ($e in $z.Entries) {
            if (-not $e.Name -or [IO.Path]::GetExtension($e.Name).ToLowerInvariant() -in $binaryExt -or $e.Length -gt 20MB) { continue }
            $st = $e.Open(); $ms = New-Object IO.MemoryStream; $st.CopyTo($ms); $st.Dispose()
            $bytes = $ms.ToArray()
            if (-not (Test-IsText $bytes)) { continue }
            $zipTexts++
            $entry = $e.FullName -replace '^DarkCipherServerManagement-[^/]+-source/', ''
            foreach ($x in Test-TextEncoding "$($zz.Name)!$($e.FullName)" "$($zz.Kind)/$entry" ([Text.Encoding]::UTF8.GetString($bytes)) $exceptions) { $enc.Add($x) }
        }
    }
    finally { $z.Dispose() }
}
Need ($zipTexts -gt 100) "encoding gate: only $zipTexts text entries found in the release zips - the scan saw too little"
$pdfs = @(Get-ChildItem $out -Filter *.pdf -File)
if ($pdfs.Count) {
    $before = $env:DCX_LOCKED_RESTORE; $env:DCX_LOCKED_RESTORE = '1'
    try { $res = @(dotnet run --project (Join-Path $PSScriptRoot 'pdftext\PdfText.csproj') -c Release -v q -- @($pdfs.FullName) 2>&1 | ForEach-Object { "$_" }); $code = $LASTEXITCODE }
    finally { $env:DCX_LOCKED_RESTORE = $before }
    foreach ($l in $res) {
        if ($l -match '^HIT (.+?) page=(\d+) (.*)$') { $enc.Add("encoding gate: mojibake in $($Matches[1]) page $($Matches[2]) ($($Matches[3]))") }
        elseif ($l -match '^OK (.+?) pages=(\d+) chars=(\d+)$') { Need ([long]$Matches[3] -ge 1000) "encoding gate: only $($Matches[3]) characters of text came out of $($Matches[1]) - it can't be checked" }
    }
    $okCount = @($res | Where-Object { $_ -match '^OK ' }).Count
    Need ($code -in 0, 3 -and $okCount -eq $pdfs.Count) "encoding gate: PDF text extraction failed (exit $code, $okCount of $($pdfs.Count) read): $(($res | Select-Object -Last 3) -join ' | ')"
}
foreach ($x in $enc) { $fail.Add($x) }

# Guard 7, real-UI verification: a record of the hand test of the changed feature in the real app, for this exact exe
# (dist\<ver>\real-ui-test.md from tools\real-ui-test-template.md). Without it the version can be checked and rehearsed
# but release.ps1 refuses to publish it.
$realUi = 'UNVERIFIED in the real UI (no dist\' + $Version + '\real-ui-test.md)'
$realUiOk = $false
$ruFile = Join-Path $out 'real-ui-test.md'
if (Test-Path $ruFile) {
    $ru = [IO.File]::ReadAllText($ruFile)
    $tester = [regex]::Match($ru, '(?m)^Tester:\s*(\S.*)$').Groups[1].Value.Trim()
    $ruExe = [regex]::Match($ru, '(?m)^Exe SHA-256:\s*([0-9a-fA-F]{64})\s*$').Groups[1].Value.ToLowerInvariant()
    $ruResult = [regex]::Match($ru, '(?m)^Result:\s*(PASS|FAIL)\s*$').Groups[1].Value
    if (-not $tester -or -not $ruResult) { $realUi = 'real-ui-test.md is incomplete (Tester and Result: PASS/FAIL are required)' }
    elseif ($exeSha -and $ruExe -ne $exeSha) { $realUi = "real-ui-test.md was for another exe ($ruExe), not this build's ($exeSha)" }
    elseif ($ruResult -ne 'PASS') { $realUi = "real-ui-test.md says FAIL ($tester)"; $fail.Add("real-UI guard: the hand test of $Version failed ($tester)") }
    else { $realUi = "PASS ($tester, exe $($exeSha.Substring(0, 12)))"; $realUiOk = $true }
}

# Guard results (guard 8): one table per run in dist\<ver>\guard-results.md, for the gate report.
function Get-GuardResult([string[]]$Prefixes, [string]$PassEvidence) {
    $hit = @($fail | Where-Object { $f = $_; @($Prefixes | Where-Object { $f.StartsWith($_) }).Count -gt 0 })
    if ($hit.Count) { @('FAIL', ($hit | Select-Object -First 2) -join ' / ') } else { @('pass', $PassEvidence) }
}
$tcText = if ($state.toolchain) { "pwsh $($state.toolchain.pwsh), .NET SDK $($state.toolchain.dotnetSdk), WiX $($state.toolchain.wix), Edge $($state.toolchain.edge), $($state.toolchain.git)" } else { 'none recorded' }
$rows = [ordered]@{
    '1 Toolchain (pwsh 7, versions recorded)'   = Get-GuardResult @('toolchain record') $tcText
    '1b Bundled runtime (minimum)'               = Get-GuardResult @('runtime guard') (($bundled.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')
    '2 Encoding gate'                            = Get-GuardResult @('encoding gate') "$zipTexts zip texts, $($pdfs.Count) PDFs, generated docs and dist text: no mojibake"
    '3 No hand-run steps (one recorded run)'     = Get-GuardResult @('pipeline record', 'no hand-run steps') "run $($pl.run), $(@($pl.steps).Count) steps in order, SHA256SUMS unchanged"
    '3a Remotes'                                 = Get-GuardResult @('remote guard') 'gateway origin present, no decommissioned server'
    '4 Clean clone, assets fetched and pinned'   = @($(if ($pl -and (@($pl.steps | ForEach-Object step) -contains 'guards')) { 'pass' } else { 'FAIL' }), "clean clone at $($pl.commit); add-ons: $((@($pl.steps) | Where-Object step -eq 'addons' | Select-Object -First 1).evidence)")
    '5 gitleaks pinned and mandatory'            = Get-GuardResult @('gitleaks guard') "$($state.toolchain.gitleaks) (the public step scans with it)"
    '7 Real-UI verification'                     = @($(if ($realUiOk) { 'pass' } else { 'UNVERIFIED' }), $realUi)
    'Version, notes, bundle and checksums'       = @($(if ($fail.Count -eq 0) { 'pass' } else { 'see failures' }), "$($assets.Count) assets")
}
$gr = New-Object Text.StringBuilder
[void]$gr.AppendLine("# Guard results for $Version ($(Get-Date -Format 'yyyy-MM-dd HH:mm zzz'), tools\release-check.ps1)")
[void]$gr.AppendLine('')
[void]$gr.AppendLine('| Guard | Result | Evidence |')
[void]$gr.AppendLine('|---|---|---|')
foreach ($r in $rows.GetEnumerator()) { [void]$gr.AppendLine("| $($r.Key) | $($r.Value[0]) | $(($r.Value[1] -replace '\|', '/')) |") }
if ($fail.Count) { [void]$gr.AppendLine(''); [void]$gr.AppendLine('Failures:'); foreach ($f in $fail) { [void]$gr.AppendLine("- $f") } }
[IO.File]::WriteAllText((Join-Path $out 'guard-results.md'), $gr.ToString(), (New-Object Text.UTF8Encoding $false))

if ($fail.Count -gt 0) {
    $fail | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-ReleaseStep $Version 'check' 'failed' ($fail -join '; ')
    throw "pre-publish checks: $($fail.Count) problem(s) for $Version (above). Nothing was published."
}
Write-ReleaseStep $Version 'check' 'done' "guards: run $($pl.run) complete, untainted, toolchain pwsh $($state.toolchain.pwsh), $zipTexts zip texts and $($pdfs.Count) PDFs free of mojibake; version $Version agrees ($(if ($Backfill) { 'back-fill: ' } else { 'csproj, ' })MSI $msiVersion, exe, update.json, notes, README); $($assets.Count) assets with matching checksums"
Write-Host "Pre-publish checks passed for $Version ($($assets.Count) assets)." -ForegroundColor Green
