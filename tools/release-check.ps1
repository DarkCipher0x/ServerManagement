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
$out = Join-Path $ReleaseRoot "dist\$Version"
$fail = New-Object System.Collections.Generic.List[string]
function Need([bool]$ok, [string]$what) { if (-not $ok) { $fail.Add($what) } }

# --- the version, everywhere --------------------------------------------------------------------------------------------
[xml]$proj = [IO.File]::ReadAllText((Join-Path $ReleaseRoot 'src\DcxManager\DcxManager.csproj'))
$numeric = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$label = $proj.Project.PropertyGroup.ReleaseLabel | Where-Object { $_ } | Select-Object -First 1
$csprojVersion = if ($label) { $label } else { $numeric }
if (-not $Backfill) { Need ($csprojVersion -eq $Version) "the csproj is at $csprojVersion, not $Version" }
Need (Test-Path (Join-Path $ReleaseRoot "docs\release-notes\$Version.md")) "docs\release-notes\$Version.md is missing"
$readme = [IO.File]::ReadAllText((Join-Path $ReleaseRoot 'README.md'))
if (-not $Backfill) { Need ($readme.Contains("badge/version-$Version-")) "README's version badge isn't $Version (run tools\write-readme-version.ps1)" }
Need ([regex]::IsMatch($readme, '\*\*' + [regex]::Escape($Version) + ':?\*\*')) "README's Release history has no $Version entry (run tools\write-readme-version.ps1)"

$updateJson = Join-Path $out 'update.json'
if (Test-Path $updateJson) { Need ((([IO.File]::ReadAllText($updateJson) | ConvertFrom-Json).version) -eq $Version) "update.json names another version" }
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
if (Test-Path $portable) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($portable)
    try {
        $e = $z.Entries | Where-Object { $_.Name -eq 'DcxServerManager.exe' } | Select-Object -First 1
        if (-not $e) { $fail.Add('the portable zip has no DcxServerManager.exe') }
        else {
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-exe-' + [guid]::NewGuid().ToString('N') + '.exe')
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $tmp)
            try { $exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($tmp).ProductVersion } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
            Need (($exeVersion -replace '\+.*$', '') -eq $Version) "the exe in the portable zip says $exeVersion, not $Version"
        }
    }
    finally { $z.Dispose() }
}

# --- the bundle and its checksums ---------------------------------------------------------------------------------------
$assets = $cfg.releaseAssets | ForEach-Object { $_.Replace('{ver}', $Version) }
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

if ($fail.Count -gt 0) {
    $fail | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-ReleaseStep $Version 'check' 'failed' ($fail -join '; ')
    throw "pre-publish checks: $($fail.Count) problem(s) for $Version (above). Nothing was published."
}
Write-ReleaseStep $Version 'check' 'done' "version $Version agrees ($(if ($Backfill) { 'back-fill: ' } else { 'csproj, ' })MSI $msiVersion, exe, update.json, notes, README); $($assets.Count) assets with matching checksums"
Write-Host "Pre-publish checks passed for $Version ($($assets.Count) assets)." -ForegroundColor Green
