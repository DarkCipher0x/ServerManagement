# Secret scan over a folder that is about to be published (release pipeline step 4; docs\RELEASE-PIPELINE.md).
# Runs this project's rules (tools\secret-patterns.txt) over every file, binaries included (read as bytes, so a token
# compiled into a DLL is found too), and then gitleaks when its pinned copy is installed (tools\gitleaks.pin: version
# and SHA-256; the binary lives in %LOCALAPPDATA%\DarkCipher\tools, never in the repository, never installed system-wide).
# Any finding stops the publish. Findings are printed with the file and line and the matched text masked.
#
# Usage: .\tools\secret-scan.ps1 -Path <folder>   (exit code 0 = clean; throws on a finding)
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path -PathType Container)) { throw "no folder $Path" }
$Path = (Resolve-Path $Path).Path

$rules = foreach ($line in [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'secret-patterns.txt'))) {
    if ($line.Trim().Length -eq 0 -or $line.StartsWith('#')) { continue }
    $parts = $line.Split("`t", 2)
    if ($parts.Count -ne 2) { throw "secret-patterns.txt: no TAB in '$line'" }
    [pscustomobject]@{ Name = $parts[0]; Rx = New-Object Text.RegularExpressions.Regex($parts[1], 'Compiled') }
}
$marker = 'secret-scan: example'
$latin1 = [Text.Encoding]::GetEncoding(28591)
$found = New-Object System.Collections.Generic.List[string]
$files = Get-ChildItem $Path -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' }
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Path.Length).TrimStart('\', '/')
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $isText = -not ($bytes -contains [byte]0)
    $text = if ($isText) { [Text.Encoding]::UTF8.GetString($bytes) } else { $latin1.GetString($bytes) -replace "`0", '' }
    $lines = $text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains($marker)) { continue }
        foreach ($r in $rules) {
            $m = $r.Rx.Match($lines[$i])
            if ($m.Success) {
                $v = $m.Value; $shown = if ($v.Length -gt 12) { $v.Substring(0, 8) + '...' } else { '...' }
                $found.Add(("{0}({1}): {2}: {3}" -f $rel, ($i + 1), $r.Name, $shown))
            }
        }
    }
}

# gitleaks, when its pinned copy is installed and matches the pin
$gitleaks = 'not installed (tools\gitleaks.pin missing)'
$pinFile = Join-Path $PSScriptRoot 'gitleaks.pin'
if (Test-Path $pinFile) {
    $pin = [IO.File]::ReadAllText($pinFile) | ConvertFrom-Json
    $exe = Join-Path $env:LOCALAPPDATA "DarkCipher\tools\gitleaks-$($pin.version)\gitleaks.exe"
    if (-not (Test-Path $exe)) { throw "gitleaks $($pin.version) is pinned but not installed at $exe - run tools\get-gitleaks.ps1" }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $pin.exeSha256) { throw "gitleaks at $exe does not match its pin (SHA-256 $hash): refusing to run it" }
    $report = Join-Path ([IO.Path]::GetTempPath()) ('dcx-gitleaks-' + [guid]::NewGuid().ToString('N') + '.json')
    & $exe detect --no-git --source $Path --redact --no-banner --report-format json --report-path $report --exit-code 3 2>&1 | Out-Null
    $code = $LASTEXITCODE
    try {
        if ($code -eq 3) {
            $items = [IO.File]::ReadAllText($report) | ConvertFrom-Json
            foreach ($it in $items) { $found.Add(("{0}({1}): gitleaks {2}" -f $it.File.Replace($Path, '').TrimStart('\', '/'), $it.StartLine, $it.RuleID)) }
        }
        elseif ($code -ne 0) { throw "gitleaks failed (exit $code)" }
    }
    finally { Remove-Item $report -ErrorAction SilentlyContinue }
    $gitleaks = "gitleaks $($pin.version)"
}

if ($found.Count -gt 0) {
    $found | Select-Object -First 50 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "secret scan: $($found.Count) finding(s) in $Path (above). Nothing was published."
}
Write-Host ("Secret scan clean: {0} files, {1} project rules, {2}" -f $files.Count, @($rules).Count, $gitleaks) -ForegroundColor Green
$gitleaks
