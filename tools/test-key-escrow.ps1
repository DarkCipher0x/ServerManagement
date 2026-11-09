# Tests sign-update.ps1 -Escrow / -RestoreEscrow end to end with THROWAWAY keys only (pwsh 7):
#   a throwaway ECDSA P-256 CNG key "DcxEscrowTest-<random>" (AllowExport, signing, like the real one), a throwaway
#   ML-DSA-65 seed file, and a fake Updater.cs pinning them. It escrows them, deletes them, restores them, and checks
#   the restored keys are the same (public keys, a signature, the export policy) and that the refusals hold: an
#   existing escrow file, a wrong password, a changed file, keys already there. Everything it made is removed at the
#   end. The real release keys are never opened (sign-update.ps1 refuses test overrides that name them).
#
# Usage:  pwsh -NoProfile -File tools\test-key-escrow.ps1
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with pwsh 7.' }
$sign = Join-Path $PSScriptRoot 'sign-update.ps1'
$id = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$name = "DcxEscrowTest-$id"
$dir = Join-Path ([IO.Path]::GetTempPath()) "DcxEscrowTest-$id"
$pq = Join-Path $dir "DcxEscrowTest-$id.dpapi"
$updater = Join-Path $dir 'Updater.cs'
$escrow = Join-Path $dir 'escrow.json'
$entropy = [Text.Encoding]::ASCII.GetBytes('DarkCipherServerManagement-UpdateSigning-MLDSA65')
$env:DCX_ESCROW_TEST_PW = 'throwaway-test-password-' + $id
$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what"; $failures.Add($what) } }
function Refused([scriptblock]$run, [string]$like) {
    try { & $run *> $null; return $false } catch { return $_.Exception.Message -like "*$like*" }
}
function Run([hashtable]$extra) { & $sign -TestKeyName $name -TestPqKeyFile $pq -PinnedFrom $updater -PasswordEnv DCX_ESCROW_TEST_PW @extra }

$dll = Join-Path $env:USERPROFILE '.nuget\packages\bouncycastle.cryptography\2.7.0\lib\net461\BouncyCastle.Cryptography.dll'
Add-Type -Path $dll
Add-Type -AssemblyName System.Security
New-Item -ItemType Directory -Force $dir | Out-Null
try {
    # --- throwaway keys and their pins
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowExport
    $p.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
    $k = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $name, $p)
    $blob = $k.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob); $k.Dispose()
    $x = [Convert]::ToHexString($blob[8..39]); $y = [Convert]::ToHexString($blob[40..71])
    $mp = [Org.BouncyCastle.Crypto.Parameters.MLDsaParameters]::ml_dsa_65
    $gen = New-Object Org.BouncyCastle.Crypto.Generators.MLDsaKeyPairGenerator
    $gen.Init((New-Object Org.BouncyCastle.Crypto.Parameters.MLDsaKeyGenerationParameters((New-Object Org.BouncyCastle.Security.SecureRandom), $mp)))
    $seed = $gen.GenerateKeyPair().Private.GetSeed()
    $pub = [Convert]::ToBase64String([Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters]::FromSeed($mp, $seed).GetPublicKey().GetEncoded())
    [IO.File]::WriteAllBytes($pq, [System.Security.Cryptography.ProtectedData]::Protect($seed, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
    [IO.File]::WriteAllText($updater, "ReleaseKeyX = `"$x`"; ReleaseKeyY = `"$y`"; ReleasePqKey = `"$pub`";")
    Write-Host "Throwaway keys: $name, $pq"

    # --- the overrides can't name the real keys
    Check (Refused { & $sign -Escrow (Join-Path $dir 'x.json') -TestKeyName 'DarkCipherServerManagement-UpdateSigning' -TestPqKeyFile $pq -PinnedFrom $updater } 'DcxEscrowTest') 'test overrides refuse the real key name'
    Check (Refused { & $sign -NewKey -TestKeyName $name -TestPqKeyFile $pq -PinnedFrom $updater } 'only') 'test overrides refuse -NewKey'

    # --- escrow
    Run @{ Escrow = $escrow } | Out-Null
    $doc = Get-Content -Raw $escrow | ConvertFrom-Json
    Check ($doc.format -eq 'servermanagement-update-keys-escrow-v1' -and $doc.iterations -eq 600000 -and $doc.ecdsaPublic -eq "$x$y") 'escrow file written (format, rounds, public key)'
    Check (-not ((Get-Content -Raw $escrow) -match [Convert]::ToBase64String($seed))) 'escrow file has no plain seed'
    Check (Refused { Run @{ Escrow = $escrow } } 'already exists') 'an existing escrow file is never overwritten'
    Check (Refused { Run @{ RestoreEscrow = $escrow } } 'both release keys') 'restore refuses when both keys are there'

    # --- tampering and the wrong password
    $bad = Join-Path $dir 'tampered.json'
    $t = Get-Content -Raw $escrow | ConvertFrom-Json
    $c = [Convert]::FromBase64String($t.ciphertext); $c[5] = $c[5] -bxor 1; $t.ciphertext = [Convert]::ToBase64String($c)
    [IO.File]::WriteAllText($bad, ($t | ConvertTo-Json))

    # --- delete the throwaway keys, then restore
    [System.Security.Cryptography.CngKey]::Open($name).Delete()
    Remove-Item -LiteralPath $pq
    Check (Refused { Run @{ RestoreEscrow = $bad } } 'Wrong password') 'a changed escrow file is refused'
    $env:DCX_ESCROW_TEST_PW_SAVED = $env:DCX_ESCROW_TEST_PW; $env:DCX_ESCROW_TEST_PW = 'not-the-password-at-all-xx'
    Check (Refused { Run @{ RestoreEscrow = $escrow } } 'Wrong password') 'the wrong password is refused'
    $env:DCX_ESCROW_TEST_PW = $env:DCX_ESCROW_TEST_PW_SAVED
    Check (-not [System.Security.Cryptography.CngKey]::Exists($name) -and -not (Test-Path $pq)) 'nothing was written by the refused restores'

    Run @{ RestoreEscrow = $escrow } | Out-Null
    Check ([System.Security.Cryptography.CngKey]::Exists($name)) 'ECDSA key restored as a named persisted key'
    $rk = [System.Security.Cryptography.CngKey]::Open($name)
    $b2 = $rk.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    Check ([Convert]::ToHexString($b2[8..39]) -eq $x -and [Convert]::ToHexString($b2[40..71]) -eq $y) 'restored ECDSA public key matches'
    Check ($rk.ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::AllowExport) 'restored export policy is AllowExport (no plaintext export)'
    Check ($rk.KeyUsage -eq [System.Security.Cryptography.CngKeyUsages]::Signing) 'restored key usage is signing'
    Check ($rk.AlgorithmGroup -eq [System.Security.Cryptography.CngAlgorithmGroup]::ECDsa) 'restored key is an ECDSA key'
    $ec = New-Object System.Security.Cryptography.ECDsaCng $rk
    $data = [Text.Encoding]::UTF8.GetBytes('escrow round trip')
    $sig = $ec.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $vk = [System.Security.Cryptography.ECDsa]::Create(); $prm = New-Object System.Security.Cryptography.ECParameters
    $prm.Curve = [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'); $pt = New-Object System.Security.Cryptography.ECPoint
    $pt.X = [Convert]::FromHexString($x); $pt.Y = [Convert]::FromHexString($y); $prm.Q = $pt; $vk.ImportParameters($prm)
    Check ($vk.VerifyData($data, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) 'restored ECDSA key signs (verified with the original public key)'
    $ec.Dispose(); $rk.Dispose(); $vk.Dispose()
    $s2 = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($pq), $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    Check ([System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals($s2, $seed)) 'ML-DSA seed restored (DPAPI, same seed)'
} finally {
    if ([System.Security.Cryptography.CngKey]::Exists($name)) { [System.Security.Cryptography.CngKey]::Open($name).Delete() }
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    Remove-Item Env:DCX_ESCROW_TEST_PW, Env:DCX_ESCROW_TEST_PW_SAVED -ErrorAction SilentlyContinue
}
if ($failures.Count) { throw "$($failures.Count) escrow check(s) failed: $($failures -join '; ')" }
Write-Host 'Key escrow round trip: all checks passed (throwaway keys removed).'
