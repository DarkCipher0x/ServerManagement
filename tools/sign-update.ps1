# Signs ServerManagement's update manifest with the release key (ADR-079): update.json -> update.json.sig, the base64
# ECDSA P-256 / SHA-256 signature (IEEE P1363, r||s) over the exact bytes of update.json. The app pins the public key
# (Services\Updater.cs ReleaseKeyX / ReleaseKeyY) and installs an update only when this signature verifies, so a
# release can come from GitHub (or any server) without trusting that server.
#
# The private key lives in this Windows user's CNG key store ("DarkCipherServerManagement-UpdateSigning"), protected
# by Windows, never in the repository, never as a plain file. NEVER replace it: every installed copy trusts only it.
# Escrow it (encrypted export) once: .\tools\sign-update.ps1 -ShowPublicKey prints the key's details.
#
# Usage:
#   .\tools\sign-update.ps1 -NewKey             create the key (refuses if it exists) and print the public key
#   .\tools\sign-update.ps1 -ShowPublicKey      print the public key (X / Y hex) to pin in Updater.cs
#   .\tools\sign-update.ps1 -Path <update.json> write <update.json>.sig and verify it
#   .\tools\sign-update.ps1 -Verify <update.json> [-PinnedFrom <Updater.cs>]  verify only (no private key needed)
#   .\tools\sign-update.ps1 -NewPqKey           create the post-quantum key (ML-DSA-65, once) and print it for pinning
param([switch]$NewKey, [switch]$NewPqKey, [switch]$ShowPublicKey, [string]$Path, [string]$Verify, [string]$PinnedFrom)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Core
$KeyName = 'DarkCipherServerManagement-UpdateSigning'

function PublicXY([System.Security.Cryptography.CngKey]$key) {
    # BCRYPT_ECCKEY_BLOB: magic (4) + cbKey (4) + X (32) + Y (32)
    $blob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $len = [BitConverter]::ToInt32($blob, 4)
    $x = ($blob[8..(8 + $len - 1)] | ForEach-Object { $_.ToString('X2') }) -join ''
    $y = ($blob[(8 + $len)..(8 + 2 * $len - 1)] | ForEach-Object { $_.ToString('X2') }) -join ''
    return @($x, $y)
}

function PinnedXY([string]$updaterCs) {
    $src = [IO.File]::ReadAllText($updaterCs)
    $mx = [regex]::Match($src, 'ReleaseKeyX\s*=\s*"([0-9A-Fa-f]{64})"'); $my = [regex]::Match($src, 'ReleaseKeyY\s*=\s*"([0-9A-Fa-f]{64})"')
    if (-not $mx.Success -or -not $my.Success) { throw "no pinned ReleaseKeyX / ReleaseKeyY in $updaterCs" }
    return @($mx.Groups[1].Value, $my.Groups[1].Value)
}

function Hex([string]$h) { $b = New-Object byte[] ($h.Length / 2); for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($h.Substring(2 * $i, 2), 16) }; return ,$b }

function VerifyWith([string]$file, [string]$x, [string]$y) {
    $sigFile = "$file.sig"
    if (-not (Test-Path $sigFile)) { return "no $sigFile" }
    $sig = [Convert]::FromBase64String(([IO.File]::ReadAllText($sigFile)).Trim())
    $xb = Hex $x; $yb = Hex $y
    $blob = New-Object byte[] (8 + 64)
    [BitConverter]::GetBytes([int]0x31534345).CopyTo($blob, 0)   # BCRYPT_ECDSA_PUBLIC_P256_MAGIC "ECS1"
    [BitConverter]::GetBytes([int]32).CopyTo($blob, 4)
    $xb.CopyTo($blob, 8); $yb.CopyTo($blob, 40)
    $pub = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $ec = New-Object System.Security.Cryptography.ECDsaCng $pub
    $ec.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
    if ($ec.VerifyData([IO.File]::ReadAllBytes($file), $sig)) { return $null }
    return "the signature on $file does not verify against the pinned release key"
}

# --- 0.6.12 (ADR-090): the post-quantum release key, ML-DSA-65 (FIPS 204), beside the ECDSA key ----------------------
# Windows' key store can't hold ML-DSA keys yet, so its 32-byte private seed is kept DPAPI-protected for this Windows user
# in %LOCALAPPDATA%\DarkCipher\keys, never in the repository, never as a plain file. NEVER replace it either: copies from
# 0.6.12 on trust only it. Escrow it with the ECDSA key. Its signature (base64) goes to <update.json>.pq.sig. The maths is
# BouncyCastle 2.7.0 (the package the app itself pins), loaded from the NuGet cache.
$PqKeyFile = Join-Path $env:LOCALAPPDATA 'DarkCipher\keys\DarkCipherServerManagement-UpdateSigning-MLDSA65.dpapi'
$PqEntropy = [Text.Encoding]::ASCII.GetBytes('DarkCipherServerManagement-UpdateSigning-MLDSA65')

function LoadBc {
    $dll = Join-Path $env:USERPROFILE '.nuget\packages\bouncycastle.cryptography\2.7.0\lib\net461\BouncyCastle.Cryptography.dll'
    if (-not (Test-Path $dll)) { throw "BouncyCastle 2.7.0 isn't in the NuGet cache: run a build (or dotnet restore) first" }
    Add-Type -Path $dll
    Add-Type -AssemblyName System.Security
}
function PqParams { return [Org.BouncyCastle.Crypto.Parameters.MLDsaParameters]::ml_dsa_65 }
function PqSeed {
    if (-not (Test-Path $PqKeyFile)) { return $null }
    $seed = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PqKeyFile), $PqEntropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return ,$seed
}
function PqPublicB64([byte[]]$seed) {
    $pub = [Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters]::FromSeed((PqParams), $seed).GetPublicKey().GetEncoded()
    return [Convert]::ToBase64String($pub)
}
function PinnedPq([string]$updaterCs) {
    $m = [regex]::Match([IO.File]::ReadAllText($updaterCs), 'ReleasePqKey\s*=\s*"([A-Za-z0-9+/=]*)"')
    if (-not $m.Success) { throw "no ReleasePqKey in $updaterCs" }
    return $m.Groups[1].Value
}
function PqVerifyWith([string]$file, [string]$pubB64) {
    $sigFile = "$file.pq.sig"
    if (-not (Test-Path $sigFile)) { return "no $sigFile" }
    $sig = [Convert]::FromBase64String(([IO.File]::ReadAllText($sigFile)).Trim())
    $data = [IO.File]::ReadAllBytes($file)
    $pub = [Org.BouncyCastle.Crypto.Parameters.MLDsaPublicKeyParameters]::FromEncoding((PqParams), [Convert]::FromBase64String($pubB64))
    $s = New-Object Org.BouncyCastle.Crypto.Signers.MLDsaSigner((PqParams), $false)
    $s.Init($false, $pub)
    $s.BlockUpdate($data, 0, $data.Length)
    if ($s.VerifySignature($sig)) { return $null }
    return "the post-quantum signature on $file does not verify against the pinned ML-DSA release key"
}

if ($NewPqKey) {
    LoadBc
    if (Test-Path $PqKeyFile) { throw "The post-quantum release key already exists; it is never replaced (copies from 0.6.12 on trust only it)." }
    $gen = New-Object Org.BouncyCastle.Crypto.Generators.MLDsaKeyPairGenerator
    $gen.Init((New-Object Org.BouncyCastle.Crypto.Parameters.MLDsaKeyGenerationParameters((New-Object Org.BouncyCastle.Security.SecureRandom), (PqParams))))
    $seed = $gen.GenerateKeyPair().Private.GetSeed()
    New-Item -ItemType Directory -Force (Split-Path $PqKeyFile) | Out-Null
    [IO.File]::WriteAllBytes($PqKeyFile, [System.Security.Cryptography.ProtectedData]::Protect($seed, $PqEntropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
    Write-Host "Created the post-quantum release key (ML-DSA-65), protected by Windows for this user: $PqKeyFile"
    Write-Host "Pin in Updater.cs:`n  ReleasePqKey = `"$(PqPublicB64 $seed)`""
    return
}

if ($NewKey) {
    if ([System.Security.Cryptography.CngKey]::Exists($KeyName)) { throw "The release key already exists; it is never replaced (every installed copy trusts only it)." }
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowExport   # encrypted export only (escrow), never plaintext
    $p.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
    $p.KeyCreationOptions = [System.Security.Cryptography.CngKeyCreationOptions]::None
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $KeyName, $p)
    $xy = PublicXY $key
    Write-Host "Created the release key '$KeyName' (ECDSA P-256, this Windows user's key store)."
    Write-Host "Pin in Updater.cs:`n  ReleaseKeyX = `"$($xy[0])`"`n  ReleaseKeyY = `"$($xy[1])`""
    return
}

if ($ShowPublicKey) {
    if (-not [System.Security.Cryptography.CngKey]::Exists($KeyName)) { throw "No release key on this account (tools\sign-update.ps1 -NewKey creates it once)." }
    $key = [System.Security.Cryptography.CngKey]::Open($KeyName)
    $xy = PublicXY $key
    Write-Host "Release key '$KeyName' (ECDSA P-256)`n  ReleaseKeyX = `"$($xy[0])`"`n  ReleaseKeyY = `"$($xy[1])`""
    return
}

if ($Verify) {
    $updater = if ($PinnedFrom) { $PinnedFrom } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Dcx.Core\Services\Updater.cs' }
    $xy = PinnedXY $updater
    $err = VerifyWith $Verify $xy[0] $xy[1]
    if ($err) { throw $err }
    $pq = PinnedPq $updater
    if ($pq.Length -gt 0) {
        LoadBc
        $err = PqVerifyWith $Verify $pq
        if ($err) { throw $err }
        Write-Host "Verified: $Verify.sig and $Verify.pq.sig match the pinned release keys (ECDSA P-256 and ML-DSA-65)."
        return
    }
    Write-Host "Verified: $Verify.sig matches the pinned release key."
    return
}

if ($Path) {
    if (-not [System.Security.Cryptography.CngKey]::Exists($KeyName)) { throw "No release key on this account: the update manifest can't be signed (tools\sign-update.ps1 -NewKey, once)." }
    $key = [System.Security.Cryptography.CngKey]::Open($KeyName)
    $ec = New-Object System.Security.Cryptography.ECDsaCng $key
    $ec.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
    $sig = $ec.SignData([IO.File]::ReadAllBytes($Path))
    [IO.File]::WriteAllText("$Path.sig", [Convert]::ToBase64String($sig) + "`n", (New-Object Text.UTF8Encoding $false))
    $xy = PinnedXY (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Dcx.Core\Services\Updater.cs')
    $own = PublicXY $key
    if ($own[0] -ne $xy[0] -or $own[1] -ne $xy[1]) { throw "This account's release key is not the one pinned in Updater.cs; refusing to sign." }
    $err = VerifyWith $Path $xy[0] $xy[1]
    if ($err) { throw $err }
    # The post-quantum signature, required once its key is pinned (0.6.12 on).
    $pinnedPq = PinnedPq (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Dcx.Core\Services\Updater.cs')
    if ($pinnedPq.Length -gt 0) {
        LoadBc
        $seed = PqSeed
        if ($null -eq $seed) { throw "No post-quantum release key on this account: the update manifest can't be signed (tools\sign-update.ps1 -NewPqKey, once)." }
        if ((PqPublicB64 $seed) -ne $pinnedPq) { throw "This account's post-quantum release key is not the one pinned in Updater.cs; refusing to sign." }
        $data = [IO.File]::ReadAllBytes($Path)
        $s = New-Object Org.BouncyCastle.Crypto.Signers.MLDsaSigner((PqParams), $false)
        $priv = [Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters]::FromSeed((PqParams), $seed)
        $s.Init($true, (New-Object Org.BouncyCastle.Crypto.Parameters.ParametersWithRandom($priv, (New-Object Org.BouncyCastle.Security.SecureRandom))))
        $s.BlockUpdate($data, 0, $data.Length)
        [IO.File]::WriteAllText("$Path.pq.sig", [Convert]::ToBase64String($s.GenerateSignature()) + "`n", (New-Object Text.UTF8Encoding $false))
        $err = PqVerifyWith $Path $pinnedPq
        if ($err) { throw $err }
        Write-Host "Signed: $Path.sig and $Path.pq.sig (verified against the pinned release keys, ECDSA P-256 and ML-DSA-65)."
        return
    }
    Write-Host "Signed: $Path.sig (verified against the pinned release key)."
    return
}

Write-Host 'Nothing to do: -NewKey, -ShowPublicKey, -Path <update.json> or -Verify <update.json>.'
