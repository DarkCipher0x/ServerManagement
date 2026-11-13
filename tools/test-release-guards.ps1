#Requires -Version 7
# Proves the mandatory release guards (ADR-123) with negative tests: each test breaks one rule on a SCRATCH COPY of a built
# release clone and checks that the guard stops it with the right message. The real clone, its dist\<ver>\ and its
# release-state.json are never touched (the copy lives in %TEMP% and is deleted afterwards).
#
# Usage (after build.ps1 has built <ver> in this clone): .\tools\test-release-guards.ps1 -Version 0.6.54 [-PreflightVersion 0.6.54-preflight.<run>]
# Prints PASS / FAIL per test and a summary; exits 1 when any guard did not fire (or the untouched copy didn't pass).
param([Parameter(Mandatory)][string]$Version, [switch]$KeepScratch, [string]$PreflightVersion)
$ErrorActionPreference = 'Stop'
$src = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $src "dist\$Version\release-state.json"))) { throw "no built dist\$Version in $src - build it first" }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('dcx-guardtest-' + [guid]::NewGuid().ToString('N'))
$aux = "$scratch-aux"   # work folders live beside the copy, so the copy's own tree stays as built
New-Item -ItemType Directory $aux | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$winps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# Runs a script block in a fresh pwsh (no pipeline run id, no inherited state); returns @{ Code; Out }.
function Invoke-Pwsh([string]$Command, [hashtable]$Env = @{}) {
    $saved = @{}
    foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
    $pipelineRun = $env:DCX_PIPELINE_RUN; $env:DCX_PIPELINE_RUN = $null
    try { $out = (& pwsh -NoProfile -NonInteractive -Command $Command 2>&1 | ForEach-Object { "$_" }) -join "`n"; $code = $LASTEXITCODE }
    finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }; $env:DCX_PIPELINE_RUN = $pipelineRun }
    @{ Code = $code; Out = $out }
}
function Record([string]$Guard, [string]$Test, [bool]$Ok, [string]$Evidence) {
    $results.Add([pscustomobject]@{ Guard = $Guard; Test = $Test; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Evidence = ($Evidence -replace '\s+', ' ').Trim() })
    Write-Host ("{0}  [{1}] {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Guard, $Test) -ForegroundColor $(if ($Ok) { 'Green' } else { 'Red' })
}
function First-Line([string]$Text, [string]$Pattern) { ($Text -split "`n" | Where-Object { $_ -match $Pattern } | Select-Object -First 1) }
# Expect a command to fail and its output to match a pattern.
function Expect-Fail([string]$Guard, [string]$Test, $Run, [string]$Pattern) {
    $ok = $Run.Code -ne 0 -and $Run.Out -match $Pattern
    $ev = First-Line $Run.Out $Pattern
    if (-not $ev) { $ev = "exit $($Run.Code): " + (($Run.Out -split "`n" | Select-Object -Last 2) -join ' | ') }
    Record $Guard $Test $ok "exit $($Run.Code): $ev"
}
function Check([string]$Root) { Invoke-Pwsh "& '$Root\tools\release-check.ps1' -Version $Version" }

try {
    # The scratch copy: the clone with its .git and dist\<ver>, without build output.
    Write-Host "Copying $src to $scratch ..."
    & robocopy $src $scratch /E /XD bin obj publish .vs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $dist = Join-Path $scratch "dist\$Version"
    $state = Join-Path $dist 'release-state.json'
    $stateBackup = [IO.File]::ReadAllBytes($state)
    function Restore-State { [IO.File]::WriteAllBytes($state, $stateBackup) }

    # 0. Baseline: the untouched copy passes release-check (otherwise the negative tests prove nothing).
    $r = Check $scratch
    Record 'baseline' 'release-check passes on the untouched copy' ($r.Code -eq 0) (First-Line $r.Out 'Pre-publish checks|problem')

    # 0a. Hold guard (7a): build.ps1 and release.ps1 refuse to start while a hold is set, in each of its three forms.
    #     The message is read from the exception (one line), not the console's wrapped error display.
    function Held([string]$Script, [string]$ScriptArgs, [hashtable]$Env = @{}) { Invoke-Pwsh "try { & '$Script' $ScriptArgs | Out-Null; 'RAN' } catch { 'ERR: ' + `$_.Exception.Message; exit 1 }" $Env }
    Expect-Fail 'hold' 'build.ps1 with DCX_HOLD set' (Held "$scratch\build.ps1" '-NoPublish' @{ DCX_HOLD = 'guard test' }) 'ERR: hold guard: build\.ps1 refused .*\(DCX_HOLD=guard test\)'
    Expect-Fail 'hold' 'release.ps1 -DryRun with DCX_HOLD set' (Held "$scratch\tools\release.ps1" "-Version $Version -DryRun" @{ DCX_HOLD = '1' }) 'ERR: hold guard: release\.ps1 refused'
    Set-Content (Join-Path $scratch 'dist\HOLD') 'waiting for the go (guard test)'
    Expect-Fail 'hold' 'build.ps1 with dist\HOLD present' (Held "$scratch\build.ps1" '-NoPublish') 'ERR: hold guard: build\.ps1 refused .*HOLD exists: waiting for the go'
    Expect-Fail 'hold' 'release.ps1 -DryRun with dist\HOLD present' (Held "$scratch\tools\release.ps1" "-Version $Version -DryRun") 'ERR: hold guard: release\.ps1 refused'
    Remove-Item (Join-Path $scratch 'dist\HOLD')
    $fakeHome = Join-Path $aux 'home'; New-Item -ItemType Directory (Join-Path $fakeHome '.darkcipher') -Force | Out-Null
    Set-Content (Join-Path $fakeHome '.darkcipher\hold') 'held for every clone'
    Expect-Fail 'hold' 'build.ps1 with %USERPROFILE%\.darkcipher\hold present' (Held "$scratch\build.ps1" '-NoPublish' @{ USERPROFILE = $fakeHome }) 'ERR: hold guard: .*held for every clone'
    $r = Invoke-Pwsh ". '$scratch\tools\release-common.ps1'; Assert-NoHold 'test'; 'no hold'" @{ DCX_HOLD = '0' }
    Record 'hold' 'DCX_HOLD=0 and no hold file: not held' ($r.Code -eq 0 -and $r.Out -match 'no hold') (First-Line $r.Out 'no hold|hold guard')

    # 0b. Preflight (0.6.54): a preflight build can never be released, and a preflight never touches the version ledger.
    $base = ($Version -replace '-preflight\..*$', '')
    Expect-Fail 'preflight' 'release.ps1 -DryRun on a -preflight version' (Held "$scratch\tools\release.ps1" "-Version $base-preflight.guardtest -DryRun") 'ERR: preflight guard: release\.ps1 refused - .*-preflight\.guardtest is a preflight build'
    Expect-Fail 'preflight' 'publish-public.ps1 on a -preflight version' (Held "$scratch\tools\publish-public.ps1" "-Version $base-preflight.guardtest -DryRun") 'ERR: preflight guard: publish-public\.ps1 refused'
    Expect-Fail 'preflight' 'publish-github-release.ps1 on a -preflight version' (Held "$scratch\tools\publish-github-release.ps1" "-Version $base-preflight.guardtest -DryRun") 'ERR: preflight guard: publish-github-release\.ps1 refused'
    Expect-Fail 'preflight' 'release.ps1 -DryRun with DCX_DIST_ROOT set (output from outside dist\)' (Held "$scratch\tools\release.ps1" "-Version $Version -DryRun" @{ DCX_DIST_ROOT = (Join-Path $aux 'elsewhere') }) 'ERR: preflight guard: release\.ps1 refused - DCX_DIST_ROOT is set'
    $sp = Join-Path $dist 'release-state.json'; $spBytes = [IO.File]::ReadAllBytes($sp)
    $ss = [IO.File]::ReadAllText($sp) | ConvertFrom-Json; $ss | Add-Member -NotePropertyName preflight -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($sp, ($ss | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
    Expect-Fail 'preflight' 'release.ps1 -DryRun on a dist folder marked as a preflight build' (Held "$scratch\tools\release.ps1" "-Version $Version -DryRun") 'ERR: preflight guard: release\.ps1 refused - dist\\.* is a preflight build'
    [IO.File]::WriteAllBytes($sp, $spBytes)
    if ($PreflightVersion) {
        $pfState = Join-Path $env:LOCALAPPDATA "DarkCipher\preflight-dist\$PreflightVersion\release-state.json"
        $pf = if (Test-Path $pfState) { [IO.File]::ReadAllText($pfState) | ConvertFrom-Json } else { $null }
        $ledgerText = [IO.File]::ReadAllText((Join-Path $env:USERPROFILE '.darkcipher\servermanagement-built-versions.json'))
        Record 'preflight' "the preflight $PreflightVersion is marked preflight and complete, outside the repository" ([bool]($pf -and $pf.preflight -and $pf.pipeline.status -eq 'complete')) "release-state.json: preflight=$($pf.preflight), pipeline=$($pf.pipeline.status), path $pfState"
        Record 'preflight' "the version ledger has no entry for $PreflightVersion (a preflight never records a version)" (-not $ledgerText.Contains($PreflightVersion)) "ledger contains it: $($ledgerText.Contains($PreflightVersion))"
    }
    Restore-State
    $s = [IO.File]::ReadAllText($state) | ConvertFrom-Json
    Record 'toolchain' 'release-state.json records the toolchain' ([bool]($s.toolchain.pwsh -match '^7\.' -and $s.toolchain.dotnetSdk -and $s.toolchain.gitleaks)) ("pwsh $($s.toolchain.pwsh); .NET SDK $($s.toolchain.dotnetSdk); WiX $($s.toolchain.wix); Edge $($s.toolchain.edge); gitleaks $($s.toolchain.gitleaks); $($s.toolchain.git)")

    # 1. Toolchain: Windows PowerShell 5.1 is refused before anything runs.
    foreach ($script in 'build.ps1', 'tools\release-check.ps1', 'tools\write-checksums.ps1') {
        $out = (& $winps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch $script) -Version $Version 2>&1 | ForEach-Object { "$_" }) -join "`n"
        Expect-Fail 'toolchain' "Windows PowerShell 5.1 runs $script" @{ Code = $LASTEXITCODE; Out = $out } 'requires|ScriptRequiresUnmatchedPSVersion'
    }

    # 2. Encoding gate: mojibake planted in a generated doc, in a text file inside the source zip, and in a PDF.
    $readme = Join-Path $scratch 'README.md'; $readmeBytes = [IO.File]::ReadAllBytes($readme)
    [IO.File]::AppendAllText($readme, "`n1127 files $([char]0xC2)$([char]0xB7) 204,699 lines`n", (New-Object Text.UTF8Encoding $false))
    Expect-Fail 'encoding' 'mojibake in README.md (generated doc)' (Check $scratch) 'encoding gate: mojibake in README\.md'
    [IO.File]::WriteAllBytes($readme, $readmeBytes); Restore-State

    $zip = Join-Path $dist "DarkCipherServerManagement-$Version-source.zip"; $zipBytes = [IO.File]::ReadAllBytes($zip)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $za = [IO.Compression.ZipFile]::Open($zip, 'Update')
    try { $e = $za.CreateEntry("DarkCipherServerManagement-$Version-source/docs/planted.md"); $w = New-Object IO.StreamWriter($e.Open(), (New-Object Text.UTF8Encoding $false)); $w.Write("Totals $([char]0xE2)$([char]0x20AC)$([char]0xA6) planted"); $w.Dispose() } finally { $za.Dispose() }
    Expect-Fail 'encoding' 'mojibake in a text file inside the source zip' (Check $scratch) 'encoding gate: mojibake in DarkCipherServerManagement-.*-source\.zip!'
    [IO.File]::WriteAllBytes($zip, $zipBytes); Restore-State

    $pdf = Join-Path $dist "DarkCipherServerManagement-$Version-Project-Structure.pdf"; $pdfBytes = [IO.File]::ReadAllBytes($pdf)
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    $work = Join-Path $aux 'pdfwork'; New-Item -ItemType Directory $work | Out-Null
    $html = Join-Path $work 'planted.html'
    $body = ('<p>Project tree. Totals: 1127 files ' + [char]0xC2 + [char]0xB7 + ' 204,652 lines, of which 162,979 lines of C#.</p>') * 40
    [IO.File]::WriteAllText($html, "<!doctype html><meta charset=`"utf-8`"><body>$body</body>", (New-Object Text.UTF8Encoding $false))
    $p = Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=$work\edge", '--no-pdf-header-footer', "--print-to-pdf=$pdf", ([Uri]$html).AbsoluteUri -PassThru -WindowStyle Hidden
    $p.WaitForExit(120000) | Out-Null
    $deadline = (Get-Date).AddSeconds(60); $last = -1L
    while ((Get-Date) -lt $deadline) { $n = (Get-Item $pdf).Length; if ($n -ne $pdfBytes.Length -and $n -eq $last) { break }; $last = $n; Start-Sleep 2 }
    Expect-Fail 'encoding' 'mojibake in the text of a PDF (Project-Structure.pdf replaced by one with the 0.6.48 defect)' (Check $scratch) 'encoding gate: mojibake in DarkCipherServerManagement-.*-Project-Structure\.pdf page'
    [IO.File]::WriteAllBytes($pdf, $pdfBytes); Restore-State

    # 3. No hand-run steps: a dist-writing tool run by hand taints the version; release-check refuses it.
    $r = Invoke-Pwsh "& '$scratch\tools\write-checksums.ps1' -Version $Version | Out-Null"
    Record 'hand-run' 'write-checksums.ps1 run by hand records a taint' ($r.Code -eq 0 -and ([IO.File]::ReadAllText($state) -match 'hand-run:write-checksums')) 'release-state.json: steps."hand-run:write-checksums" = tainted'
    Expect-Fail 'hand-run' 'release-check refuses the tainted version' (Check $scratch) 'no hand-run steps: hand-run:write-checksums'
    Restore-State
    $sums = Join-Path $dist "DarkCipherServerManagement-$Version-SHA256SUMS.txt"; $sumsBytes = [IO.File]::ReadAllBytes($sums)
    [IO.File]::AppendAllText($sums, "`n")
    Expect-Fail 'hand-run' 'SHA256SUMS changed after the recorded run' (Check $scratch) 'SHA256SUMS changed after the build run'
    [IO.File]::WriteAllBytes($sums, $sumsBytes); Restore-State
    $s = [IO.File]::ReadAllText($state) | ConvertFrom-Json
    $s.pipeline.steps = @($s.pipeline.steps | Where-Object { $_.step -ne 'checksums' })
    [IO.File]::WriteAllText($state, ($s | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
    Expect-Fail 'hand-run' 'a pipeline step missing from the recorded run' (Check $scratch) 'pipeline record: the run recorded'
    Restore-State
    $s = [IO.File]::ReadAllText($state) | ConvertFrom-Json
    $s.PSObject.Properties.Remove('pipeline')
    [IO.File]::WriteAllText($state, ($s | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
    Expect-Fail 'hand-run' 'no build.ps1 run recorded at all (a hand-assembled dist)' (Check $scratch) 'has no build\.ps1 run'
    Restore-State

    # 4. Remote guard: a remote on a decommissioned server (the first in release-hosts.json), and no gateway remote.
    $git = 'git'
    $origin = (& $git -C $scratch remote get-url origin).Trim()
    $old = @(([IO.File]::ReadAllText((Join-Path $env:USERPROFILE '.darkcipher\release-hosts.json')) | ConvertFrom-Json).forbiddenHosts)[0]
    & $git -C $scratch remote add oldhost "git@${old}:git/DarkCipher-ServerManagement.git"
    Expect-Fail 'remote' 'a remote points at a decommissioned server (build.ps1)' (Invoke-Pwsh "& '$scratch\build.ps1' -NoPublish") "remote guard: .*$([regex]::Escape($old))"
    Expect-Fail 'remote' 'a remote points at a decommissioned server (release-check)' (Check $scratch) "remote guard: .*$([regex]::Escape($old))"
    & $git -C $scratch remote remove oldhost
    & $git -C $scratch remote set-url origin 'https://example.invalid/DarkCipher-ServerManagement.git'
    Expect-Fail 'remote' 'no remote points at the gateway origin' (Invoke-Pwsh "& '$scratch\build.ps1' -NoPublish") 'remote guard: no remote .* gateway origin'
    & $git -C $scratch remote set-url origin $origin
    & $git -C $scratch checkout -q -- README.md docs/PROJECT-TREE.generated.md 2>$null

    # 5. Clean-clone guard: build output present, a modified file, a commit that isn't on the gateway.
    Expect-Fail 'clean-clone' 'build.ps1 in a clone that holds build output (dist\)' (Invoke-Pwsh "& '$scratch\build.ps1' -NoPublish") 'clean-clone guard: .*build output'
    $fresh = Join-Path $aux 'fresh'
    & $git clone -q --depth 50 --branch (& $git -C $src rev-parse --abbrev-ref HEAD).Trim() $origin $fresh
    if ($LASTEXITCODE) { throw "git clone $origin failed" }
    $r = Invoke-Pwsh ". '$fresh\tools\release-common.ps1'; Assert-CleanCheckout | Out-Null; 'clean'"
    Record 'clean-clone' 'a fresh clone of the gateway origin passes' ($r.Code -eq 0 -and $r.Out -match 'clean') (First-Line $r.Out 'clean|guard')
    [IO.File]::AppendAllText((Join-Path $fresh 'README.md'), ' ')
    Expect-Fail 'clean-clone' 'a modified tracked file' (Invoke-Pwsh ". '$fresh\tools\release-common.ps1'; Assert-CleanCheckout") 'clean-clone guard: .*modified or untracked'
    & $git -C $fresh checkout -q -- README.md
    & $git -C $fresh -c user.name=guardtest -c user.email=guardtest@example.invalid commit -q --allow-empty -m 'guard test: local only'
    Expect-Fail 'clean-clone' 'HEAD is a commit the gateway origin does not have' (Invoke-Pwsh ". '$fresh\tools\release-common.ps1'; Assert-CleanCheckout") 'clean-clone guard: HEAD .* is not on the gateway remote'

    # 5a. Pre-flight (0.6.53): a stale lock file stops the build before the version is spent (the 0.6.52 NU1004).
    $lock = Join-Path $fresh 'src\DcxManager\packages.lock.json'
    $ledger = Join-Path $env:USERPROFILE '.darkcipher\servermanagement-built-versions.json'
    $ledgerBefore = if (Test-Path $ledger) { (Get-FileHash $ledger).Hash } else { '' }
    $lt = [IO.File]::ReadAllText($lock)
    $stale = [regex]::Replace($lt, '("Microsoft\.NET\.ILLink\.Tasks":\s*\{\s*"type":\s*"Direct",\s*"requested":\s*")\[[0-9.]+, \)', '${1}[10.0.1, )')
    [IO.File]::WriteAllText($lock, $stale)
    $r = Invoke-Pwsh "try { . '$fresh\tools\release-common.ps1'; Invoke-BuildPreflight | Out-Null; 'RAN' } catch { 'ERR: ' + `$_.Exception.Message; exit 1 }"
    Expect-Fail 'pre-flight' 'a stale lock file (ILLink.Tasks pinned to another version)' $r 'ERR: pre-flight: locked restore failed'
    $ledgerAfter = if (Test-Path $ledger) { (Get-FileHash $ledger).Hash } else { '' }
    Record 'pre-flight' 'the failed pre-flight left the version ledger untouched' ($ledgerBefore -eq $ledgerAfter) "ledger SHA-256 before = after: $($ledgerBefore -eq $ledgerAfter)"
    & $git -C $fresh checkout -q -- src/DcxManager/packages.lock.json

    # 6. Version guard: the built version can't be built again (ledger), nor an earlier abandoned one.
    Expect-Fail 'version' "rebuilding $Version" (Invoke-Pwsh ". '$fresh\tools\release-common.ps1'; Assert-NewVersion '$Version' (Get-ReleaseConfig)") 'version guard: .* built before'
    Expect-Fail 'version' 'rebuilding the abandoned 0.6.48' (Invoke-Pwsh ". '$src\tools\release-common.ps1'; Assert-NewVersion '0.6.48' (Get-ReleaseConfig)") 'version guard: .* built before'

    # 7. Add-on guard: missing, different from its .sha256, different from the release.json pin.
    $cfgCmd = ". '$scratch\tools\release-common.ps1'; `$c = Get-ReleaseConfig; New-Item -ItemType Directory -Force '$scratch\dist\guardtest' | Out-Null"
    $empty = Join-Path $aux 'addon-store-empty'; New-Item -ItemType Directory $empty | Out-Null
    Expect-Fail 'add-on' 'the published add-on is missing from the store' (Invoke-Pwsh "$cfgCmd; Copy-PublishedAddons `$c 'guardtest' '$empty'") 'add-on guard: .* is missing'
    $a = (Get-Content (Join-Path $scratch 'tools\release.json') -Raw | ConvertFrom-Json).publishedAddons[0]
    $name = "$($a.id)-$($a.version).dcxaddon"
    $bad = Join-Path $aux "addon-store-bad\$($a.id)"; New-Item -ItemType Directory -Force $bad | Out-Null
    Copy-Item (Join-Path $dist $name) $bad; Copy-Item (Join-Path $dist "$name.sha256") $bad
    $fs = [IO.File]::Open((Join-Path $bad $name), 'Open', 'ReadWrite'); $fs.Position = 100; $b = $fs.ReadByte(); $fs.Position = 100; $fs.WriteByte(($b -bxor 0xFF)); $fs.Dispose()
    Expect-Fail 'add-on' 'the add-on package differs from its .sha256' (Invoke-Pwsh "$cfgCmd; Copy-PublishedAddons `$c 'guardtest' '$(Split-Path $bad -Parent)'") "add-on guard: .* doesn't match its \.sha256"
    $good = Join-Path $aux "addon-store-good\$($a.id)"; New-Item -ItemType Directory -Force $good | Out-Null
    Copy-Item (Join-Path $dist $name) $good; Copy-Item (Join-Path $dist "$name.sha256") $good
    Expect-Fail 'add-on' 'the add-on differs from the SHA-256 pinned in release.json' (Invoke-Pwsh "$cfgCmd; `$c.publishedAddons[0].sha256 = ('0' * 64); Copy-PublishedAddons `$c 'guardtest' '$(Split-Path $good -Parent)'") 'add-on guard: .* pinned in release\.json'

    # 8. gitleaks: not installed, pin missing, and a planted token found.
    $plant = Join-Path $aux 'plant'; New-Item -ItemType Directory $plant | Out-Null
    $token = 'gh' + 'p_' + (-join ((1..36) | ForEach-Object { [char](97 + ($_ * 7) % 26) }))   # built at run time: no token-shaped text in this file
    [IO.File]::WriteAllText((Join-Path $plant 'config.txt'), "token = $token`n")
    Expect-Fail 'gitleaks' 'gitleaks is not installed' (Invoke-Pwsh "& '$scratch\tools\secret-scan.ps1' -Path '$plant'" @{ LOCALAPPDATA = (Join-Path $aux 'no-localappdata') }) 'gitleaks guard: .* not installed'
    Move-Item (Join-Path $scratch 'tools\gitleaks.pin') (Join-Path $scratch 'tools\gitleaks.pin.off')
    Expect-Fail 'gitleaks' 'tools\gitleaks.pin is missing' (Invoke-Pwsh "& '$scratch\tools\secret-scan.ps1' -Path '$plant'") 'gitleaks guard: tools\\gitleaks\.pin is missing'
    Move-Item (Join-Path $scratch 'tools\gitleaks.pin.off') (Join-Path $scratch 'tools\gitleaks.pin')
    Expect-Fail 'gitleaks' 'a planted GitHub token is found' (Invoke-Pwsh "& '$scratch\tools\secret-scan.ps1' -Path '$plant'") 'gitleaks (github-pat|github-token|generic-api-key)'
}
finally {
    if (-not $KeepScratch) { Remove-Item $scratch, $aux -Recurse -Force -ErrorAction SilentlyContinue }
}

$failed = @($results | Where-Object Result -eq 'FAIL')
Write-Host ''
Write-Host ("Release guard tests for {0}: {1} passed, {2} failed" -f $Version, ($results.Count - $failed.Count), $failed.Count) -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
$results | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path ([IO.Path]::GetTempPath()) "dcx-guardtest-$Version.json") -Encoding utf8
if ($failed.Count) { exit 1 }
