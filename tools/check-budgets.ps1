# Checks a build against tools\budgets.json (0.2.0 phase 7); build.ps1 calls it after the self-tests and after packaging.
#   -Report / -ReportAiOff   self-test reports: their "start-up: N ms ... M MB private" line against aiOn / aiOff
#   -Exe / -Msi              sizes against exeMaxMb / msiMaxMb
# Prints every measurement; throws when one is over its budget. Raise a budget only with a reason in budgets.json.
param([string]$Report, [string]$ReportAiOff, [string]$Exe, [string]$Msi)
$ErrorActionPreference = 'Stop'
$b = Get-Content (Join-Path $PSScriptRoot 'budgets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$over = @()
function StartUp([string]$file, [string]$what, $limit) {
    $line = Get-Content $file -Encoding UTF8 | Where-Object { $_ -match 'start-up: ([\d,]+) ms .*?(\d+) MB private' } | Select-Object -First 1
    if (-not $line) { $script:over += "$what start-up line missing in $file"; return }
    $null = $line -match 'start-up: ([\d,]+) ms .*?(\d+) MB private'
    $ms = [int]($Matches[1] -replace ',', ''); $mb = [int]$Matches[2]
    Write-Host ("budget  {0,-14} {1,6} ms (max {2})  {3,4} MB (max {4})" -f $what, $ms, $limit.maxMs, $mb, $limit.maxMb)
    if ($ms -gt $limit.maxMs) { $script:over += "$what start-up $ms ms > $($limit.maxMs) ms" }
    if ($mb -gt $limit.maxMb) { $script:over += "$what memory $mb MB > $($limit.maxMb) MB" }
}
function Size([string]$file, [string]$what, [double]$max) {
    $mb = [math]::Round((Get-Item $file).Length / 1MB, 1)
    Write-Host ("budget  {0,-14} {1,6} MB (max {2})" -f $what, $mb, $max)
    if ($mb -gt $max) { $script:over += "$what $mb MB > $max MB" }
}
if ($Report) { StartUp $Report 'AI on' $b.aiOn }
if ($ReportAiOff) { StartUp $ReportAiOff 'AI off' $b.aiOff }
if ($Exe) { Size $Exe 'exe' $b.exeMaxMb }
if ($Msi) { Size $Msi 'msi' $b.msiMaxMb }
if ($over.Count -gt 0) { throw "over budget (tools\budgets.json):`n  " + ($over -join "`n  ") }
