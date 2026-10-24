# Writes SHA-256 checksums for one version's release files in dist\<ver>\ (MSI, setup program, portable zip, PDF guide, Store MSIX, the signed update.json and both of its signatures, 0.6.12):
#   <file>.sha256                                    "<sha256>  <file>" - the format the website's sync scripts check
#   DarkCipherServerManagement-<ver>-SHA256SUMS.txt  every file of that version, for one upload / sha256sum -c
# Usage: .\tools\write-checksums.ps1 -Version 0.1.3
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$dist = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist') $Version
$files = Get-ChildItem $dist -File | Where-Object { ($_.Name -like "DarkCipherServerManagement-$Version-*" -and $_.Name -notlike '*-test.msix' -and ($_.Extension -in '.msi', '.zip', '.pdf', '.msix' -or $_.Name -like '*-sbom.cdx.json' -or $_.Name -like '*-Setup.exe')) -or $_.Name -in 'update.json', 'update.json.sig', 'update.json.pq.sig' } | Sort-Object Name
if (-not $files) { throw "no release files for $Version in $dist" }
$enc = New-Object Text.UTF8Encoding $false
$lines = foreach ($f in $files) {
    $line = "{0}  {1}" -f (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower(), $f.Name
    [IO.File]::WriteAllText("$($f.FullName).sha256", "$line`n", $enc)
    $line
}
$sums = Join-Path $dist "DarkCipherServerManagement-$Version-SHA256SUMS.txt"
[IO.File]::WriteAllText($sums, (($lines -join "`n") + "`n"), $enc)
$lines | ForEach-Object { Write-Host $_ }
Write-Host "Checksums: $sums"
