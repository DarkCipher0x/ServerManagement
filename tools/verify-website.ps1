# Release pipeline step 7b: after the Website session has deployed a version, check the live site serves exactly the
# hand-off's files (dist\<ver>\website\release-manifest.json): every site_path downloaded and its SHA-256 compared.
# Read-only. Records "website" as done only when every file matches.
# Pure ASCII (Windows PowerShell 5.1 reads scripts without a BOM as the ANSI code page).
#
# Usage: .\tools\verify-website.ps1 -Version 0.6.9 [-Site https://darkciphersystems.com]
param([Parameter(Mandatory)][string]$Version, [string]$Site = 'https://darkciphersystems.com')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$manifestPath = Join-Path $ReleaseRoot "dist\$Version\website\release-manifest.json"
if (-not (Test-Path $manifestPath)) { throw "no hand-off for $Version - run tools\website-handoff.ps1 first" }
$m = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($m.github_release -eq 'pending') { throw "the hand-off still has pending GitHub fields: publish, then run tools\website-handoff.ps1 again" }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$bad = New-Object System.Collections.Generic.List[string]
try {
    foreach ($f in $m.files) {
        $dst = Join-Path $tmp $f.file
        try { Invoke-WebRequest "$($Site.TrimEnd('/'))/$($f.site_path)" -OutFile $dst -UseBasicParsing -TimeoutSec 600 }
        catch { $bad.Add("$($f.site_path): $($_.Exception.Message)"); continue }
        $h = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($h -ne $f.sha256) { $bad.Add("$($f.site_path): SHA-256 $h, expected $($f.sha256)") } else { Write-Host "  $($f.site_path) OK" }
    }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
if ($bad.Count -gt 0) { Write-ReleaseStep $Version 'website' 'failed' "live site differs: $($bad -join '; ')"; throw "the live site differs: $($bad -join '; ')" }
Write-ReleaseStep $Version 'website' 'done' "$Site serves all $(@($m.files).Count) files byte-identical to the hand-off"
Write-Host "Website verified: $Site serves all $(@($m.files).Count) files byte-identical" -ForegroundColor Green
