#Requires -Version 7
# Signs ServerManagement's update manifest with the release key (ADR-079): update.json -> update.json.sig, the base64
# ECDSA P-256 / SHA-256 signature (IEEE P1363, r||s) over the exact bytes of update.json. The app pins the public key
# (Services\Updater.cs ReleaseKeyX / ReleaseKeyY) and installs an update only when this signature verifies, so a
# release can come from GitHub (or any server) without trusting that server.
#
# The private key lives in this Windows user's CNG key store ("DarkCipherServerManagement-UpdateSigning"), protected
# by Windows, never in the repository, never as a plain file. NEVER replace it: every installed copy trusts only it.
# Escrow both release keys once (-Escrow, below) and keep the file apart from its password.
#
# Usage:
#   .\tools\sign-update.ps1 -NewKey             create the key (refuses if it exists) and print the public key
#   .\tools\sign-update.ps1 -ShowPublicKey      print the public key (X / Y hex) to pin in Updater.cs
#   .\tools\sign-update.ps1 -Path <update.json> write <update.json>.sig and verify it
#   .\tools\sign-update.ps1 -Verify <update.json> [-PinnedFrom <Updater.cs>]  verify only (no private key needed)
#   .\tools\sign-update.ps1 -NewPqKey           create the post-quantum key (ML-DSA-65, once) and print it for pinning
#   .\tools\sign-update.ps1 -Escrow <file>         write a password-encrypted copy of both keys (pwsh 7; never overwrites a file)
#   .\tools\sign-update.ps1 -RestoreEscrow <file>  put both keys back on a new PC or Windows account (pwsh 7, not packaged)
#
# Escrow: PBKDF2-HMAC-SHA256 (600,000 rounds, 32-byte salt) -> AES-256-CBC, then HMAC-SHA256 over the format, rounds,
# salt, IV and ciphertext (the scheme CaseKeeper's and WhisperDesk's tools use). The password is asked for (16+
# characters, twice); keep it apart from the file. Restore checks the password and that the keys are the pinned ones
# before it writes anything, and never overwrites a key that's already there.
#
# -TestKeyName DcxEscrowTest-<x>, -TestPqKeyFile <file> and -PasswordEnv <VAR> exist only to test the escrow round trip
# with throwaway keys (tools\test-key-escrow.ps1); they can never point at the real keys.
param([switch]$NewKey, [switch]$NewPqKey, [switch]$ShowPublicKey, [string]$Path, [string]$Verify, [string]$PinnedFrom,
      [string]$Escrow, [string]$RestoreEscrow, [string]$TestKeyName, [string]$TestPqKeyFile, [string]$PasswordEnv)
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
$RealPqKeyFile = $PqKeyFile

# Test overrides (throwaway keys only), never the real key name or file.
if ($TestKeyName -or $TestPqKeyFile -or $PasswordEnv) {
    if (-not ($TestKeyName -and $TestPqKeyFile)) { throw '-TestKeyName and -TestPqKeyFile go together (and -PasswordEnv only with them).' }
    if ($TestKeyName -notmatch '^DcxEscrowTest-[A-Za-z0-9-]{1,40}$') { throw '-TestKeyName must be DcxEscrowTest-<letters, digits, ->.' }
    if ([IO.Path]::GetFullPath($TestPqKeyFile) -eq [IO.Path]::GetFullPath($RealPqKeyFile) -or (Split-Path -Leaf $TestPqKeyFile) -notlike 'DcxEscrowTest-*') { throw '-TestPqKeyFile must be a throwaway DcxEscrowTest-* file, never the real key.' }
    if (-not $PinnedFrom) { throw 'Test keys need -PinnedFrom <a test Updater.cs> (the real pins never match them).' }
    if ($NewKey -or $NewPqKey -or $Path) { throw 'The test overrides are for -Escrow and -RestoreEscrow only.' }
    $KeyName = $TestKeyName; $PqKeyFile = $TestPqKeyFile
}

# A packaged (MSIX) host such as the Claude desktop app may virtualise %LOCALAPPDATA%: a file written from inside it lands
# in %LOCALAPPDATA%\Packages\<package>\LocalCache\Local and is deleted with that app. 0.6.12's ML-DSA seed was created that
# way (found 2026-11-08). So key files are never created or restored from a process whose package redirects file writes
# (its manifest doesn't set desktop6:FileSystemWriteVirtualization to disabled), or with LOCALAPPDATA pointed into a
# package's folder. The Store's PowerShell 7 is packaged but turns write virtualisation off, so it is fine.
function VirtualisingPackage {
    if (-not ('Dcx.PackageCheck' -as [type])) {
        Add-Type -Namespace Dcx -Name PackageCheck -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder name);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetCurrentPackagePath(ref int length, System.Text.StringBuilder path);
'@
    }
    $len = 0
    if ([Dcx.PackageCheck]::GetCurrentPackageFullName([ref]$len, $null) -eq 15700) { return $null }   # APPMODEL_ERROR_NO_PACKAGE: not packaged
    $len = 0; [void][Dcx.PackageCheck]::GetCurrentPackagePath([ref]$len, $null)
    $sb = New-Object Text.StringBuilder ([Math]::Max($len, 1))
    if ([Dcx.PackageCheck]::GetCurrentPackagePath([ref]$len, $sb) -ne 0) { return "a packaged app whose folder couldn't be read" }
    $manifest = Join-Path $sb.ToString() 'AppxManifest.xml'
    try { $xml = [IO.File]::ReadAllText($manifest) } catch { return "a packaged app ($manifest couldn't be read)" }
    if ($xml -match '<desktop6:FileSystemWriteVirtualization>\s*disabled\s*</desktop6:FileSystemWriteVirtualization>') { return $null }
    $name = [regex]::Match($xml, '<Identity[^>]*\sName="([^"]+)"').Groups[1].Value
    return "the packaged app $name, which redirects writes to %LOCALAPPDATA% into its private folder"
}
function VirtualisedKeyStore {
    if ($env:LOCALAPPDATA -match '\\Packages\\[^\\]+\\LocalCache\\') { return "LOCALAPPDATA points into a packaged app's folder ($env:LOCALAPPDATA)" }
    $pkg = VirtualisingPackage
    if ($pkg) { return "this PowerShell runs inside $pkg" }
    return $null
}
function RefuseVirtualised([string]$what) {
    $v = VirtualisedKeyStore
    if ($v) { throw "Refusing to $what here: $v. A key file written now would be deleted with that app. Run it from a PowerShell 7 window opened from the Start menu (the Store's PowerShell is fine), not from a terminal inside another app." }
}

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
    RefuseVirtualised 'create the post-quantum release key'
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

# ---- escrow: both keys under one password ---------------------------------------------------------------------------
$EscrowFormat = 'servermanagement-update-keys-escrow-v1'
$EscrowIterations = 600000
function Wipe([byte[]]$b) { if ($null -ne $b) { [Array]::Clear($b, 0, $b.Length) } }
function NeedPwsh7 { if ($PSVersionTable.PSVersion.Major -lt 7) { throw "Escrow needs PowerShell 7 (pwsh): Windows PowerShell 5.1 can't export a CNG key this way." } }
function UpdaterCs { if ($PinnedFrom) { return $PinnedFrom } return (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Dcx.Core\Services\Updater.cs') }
function Hex2([byte[]]$b) { return [Convert]::ToHexString($b) }
function EscrowKeys([string]$password, [byte[]]$salt) {
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($password, $salt, $EscrowIterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $k = $kdf.GetBytes(64) } finally { $kdf.Dispose() }
    $enc = New-Object byte[] 32; $mac = New-Object byte[] 32
    [Array]::Copy($k, 0, $enc, 0, 32); [Array]::Copy($k, 32, $mac, 0, 32); Wipe $k
    return @($enc, $mac)
}
function ReadPassword([string]$prompt) {
    if ($PasswordEnv) { return [Environment]::GetEnvironmentVariable($PasswordEnv) }   # throwaway-key tests only (checked above)
    $sec = Read-Host -AsSecureString $prompt
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}
function MacOf([byte[]]$macKey, [byte[]]$salt, [byte[]]$iv, [byte[]]$ct) {
    $h = New-Object System.Security.Cryptography.HMACSHA256 (, $macKey)
    try {
        $hdr = [Text.Encoding]::ASCII.GetBytes("$EscrowFormat|$EscrowIterations|")
        $all = New-Object byte[] ($hdr.Length + $salt.Length + $iv.Length + $ct.Length)
        $hdr.CopyTo($all, 0); $salt.CopyTo($all, $hdr.Length); $iv.CopyTo($all, $hdr.Length + $salt.Length); $ct.CopyTo($all, $hdr.Length + $salt.Length + $iv.Length)
        return ,$h.ComputeHash($all)
    } finally { $h.Dispose() }
}
function SameBytes([byte[]]$a, [byte[]]$b) { return [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals($a, $b) }

# Puts the ECDSA key back as a named, persisted key of this Windows user, with the rules it was created with (Export
# Policy = AllowExport: encrypted export only, never plaintext; usage = signing). CngKey.Import makes only ephemeral keys,
# so this is NCryptImportKey with the key name and NCRYPT_DO_NOT_FINALIZE_FLAG, then the properties, then
# NCryptFinalizeKey (which persists it).
function ImportPersistedEcKey([string]$name, [byte[]]$eccPrivateBlob) {
    if (-not ('Dcx.KeyImport' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Dcx {
public static class KeyImport {
    [StructLayout(LayoutKind.Sequential)] private struct NCryptBuffer { public int cbBuffer; public int BufferType; public IntPtr pvBuffer; }
    [StructLayout(LayoutKind.Sequential)] private struct NCryptBufferDesc { public int ulVersion; public int cBuffers; public IntPtr pBuffers; }
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenStorageProvider(out IntPtr provider, string name, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptImportKey(IntPtr provider, IntPtr importKey, string blobType, ref NCryptBufferDesc parameters, out IntPtr key, byte[] data, int size, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptSetProperty(IntPtr obj, string property, byte[] value, int size, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFinalizeKey(IntPtr key, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFreeObject(IntPtr obj);
    private const int NCRYPTBUFFER_PKCS_KEY_NAME = 45, NCRYPT_DO_NOT_FINALIZE_FLAG = 0x400, NCRYPT_ALLOW_EXPORT_FLAG = 0x1, NCRYPT_ALLOW_SIGNING_FLAG = 0x2;
    // blob = BCRYPT_ECCPRIVATE_BLOB with the ECDSA P-256 magic, so the key comes back in the ECDSA algorithm group (a
    // PKCS#8 import would make it ECDH).
    public static void Persist(string name, byte[] blob) {
        IntPtr provider = IntPtr.Zero, key = IntPtr.Zero, nameBuf = IntPtr.Zero, buffers = IntPtr.Zero;
        try {
            Check(NCryptOpenStorageProvider(out provider, "Microsoft Software Key Storage Provider", 0), "open the key storage provider");
            nameBuf = Marshal.StringToHGlobalUni(name);
            var b = new NCryptBuffer { cbBuffer = (name.Length + 1) * 2, BufferType = NCRYPTBUFFER_PKCS_KEY_NAME, pvBuffer = nameBuf };
            buffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NCryptBuffer)));
            Marshal.StructureToPtr(b, buffers, false);
            var desc = new NCryptBufferDesc { ulVersion = 0, cBuffers = 1, pBuffers = buffers };
            Check(NCryptImportKey(provider, IntPtr.Zero, "ECCPRIVATEBLOB", ref desc, out key, blob, blob.Length, NCRYPT_DO_NOT_FINALIZE_FLAG), "import the key");
            Check(NCryptSetProperty(key, "Export Policy", BitConverter.GetBytes(NCRYPT_ALLOW_EXPORT_FLAG), 4, 0), "set its export policy");
            Check(NCryptSetProperty(key, "Key Usage", BitConverter.GetBytes(NCRYPT_ALLOW_SIGNING_FLAG), 4, 0), "set its usage");
            Check(NCryptFinalizeKey(key, 0), "save the key");
        } finally {
            if (key != IntPtr.Zero) NCryptFreeObject(key);
            if (provider != IntPtr.Zero) NCryptFreeObject(provider);
            if (nameBuf != IntPtr.Zero) Marshal.FreeHGlobal(nameBuf);
            if (buffers != IntPtr.Zero) Marshal.FreeHGlobal(buffers);
        }
    }
    private static void Check(int rc, string what) { if (rc != 0) throw new Win32Exception(rc, "Couldn't " + what + " (NCrypt 0x" + rc.ToString("X8") + ")."); }
}
}
'@
    }
    [Dcx.KeyImport]::Persist($name, $eccPrivateBlob)
}

if ($Escrow) {
    NeedPwsh7
    LoadBc
    if (Test-Path -LiteralPath $Escrow) { throw "$Escrow already exists; choose a new file (an escrow file is never overwritten)." }
    $escrowDir = Split-Path -Parent ([IO.Path]::GetFullPath($Escrow))
    if (-not (Test-Path -LiteralPath $escrowDir -PathType Container)) { throw "The folder $escrowDir doesn't exist." }
    if (-not [System.Security.Cryptography.CngKey]::Exists($KeyName)) { throw "This Windows account has no release key '$KeyName' to escrow." }
    $seed = PqSeed
    if ($null -eq $seed) { throw "This Windows account has no post-quantum release key ($PqKeyFile) to escrow." }
    $virt = VirtualisedKeyStore
    if ($virt) { Write-Warning "$virt. The post-quantum key escrowed is the copy this process sees ($PqKeyFile); it is checked against the pinned key." }
    $updater = UpdaterCs; $pinXY = PinnedXY $updater; $pinPq = PinnedPq $updater
    $key = [System.Security.Cryptography.CngKey]::Open($KeyName)
    $ec = New-Object System.Security.Cryptography.ECDsaCng $key
    $plain = $null; $keys = $null; $prm = $null
    try {
        # .NET exports an AllowExport (not plaintext) CNG key through an encrypted PKCS#8 export that it decrypts in memory.
        $prm = $ec.ExportParameters($true)
        $x = Hex2 $prm.Q.X; $y = Hex2 $prm.Q.Y
        if ($x -ne $pinXY[0] -or $y -ne $pinXY[1]) { throw "This account's release key isn't the one pinned in $updater; nothing was escrowed." }
        if ((PqPublicB64 $seed) -ne $pinPq) { throw "This account's post-quantum release key isn't the one pinned in $updater; nothing was escrowed." }
        $pw = ReadPassword 'Escrow password (at least 16 characters; keep it apart from the file)'
        if ($null -eq $pw -or $pw.Length -lt 16) { throw 'The escrow password must be at least 16 characters.' }
        if (-not $PasswordEnv -and (ReadPassword 'The same password again') -ne $pw) { throw "The two passwords don't match." }
        # X (32) | Y (32) | D (32) | ML-DSA seed (32)
        $plain = New-Object byte[] 128
        $prm.Q.X.CopyTo($plain, 0); $prm.Q.Y.CopyTo($plain, 32); $prm.D.CopyTo($plain, 64); $seed.CopyTo($plain, 96)
        $salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32); $iv = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
        $keys = EscrowKeys $pw $salt; $pw = $null
        $aes = [System.Security.Cryptography.Aes]::Create(); $aes.Key = $keys[0]; $aes.IV = $iv
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC; $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $ct = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length); $aes.Dispose()
        $mac = MacOf $keys[1] $salt $iv $ct
        $doc = [ordered]@{
            format = $EscrowFormat; kdf = 'PBKDF2-HMAC-SHA256'; iterations = $EscrowIterations; cipher = 'AES-256-CBC + HMAC-SHA256'
            salt = [Convert]::ToBase64String($salt); iv = [Convert]::ToBase64String($iv); ciphertext = [Convert]::ToBase64String($ct); mac = [Convert]::ToBase64String($mac)
            ecdsaPublic = "$x$y"; mldsaPublicSha256 = Hex2 ([System.Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($pinPq)))
        }
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($Escrow), (($doc | ConvertTo-Json) + "`n"), (New-Object Text.UTF8Encoding $false))
        Write-Host "Escrowed both ServerManagement release keys (ECDSA P-256 and ML-DSA-65) to $Escrow, password-encrypted. Keep the password apart from the file."
    } finally { Wipe $plain; Wipe $seed; if ($null -ne $prm) { Wipe $prm.D }; if ($null -ne $keys) { Wipe $keys[0]; Wipe $keys[1] }; $ec.Dispose(); $key.Dispose() }
    return
}

if ($RestoreEscrow) {
    NeedPwsh7
    RefuseVirtualised 'restore the release keys'
    LoadBc
    $doc = [IO.File]::ReadAllText([IO.Path]::GetFullPath($RestoreEscrow)) | ConvertFrom-Json
    if ($doc.format -ne $EscrowFormat -or [int]$doc.iterations -ne $EscrowIterations) { throw "$RestoreEscrow isn't a ServerManagement key escrow file." }
    $updater = UpdaterCs; $pinXY = PinnedXY $updater; $pinPq = PinnedPq $updater
    $haveEc = [System.Security.Cryptography.CngKey]::Exists($KeyName); $havePq = Test-Path -LiteralPath $PqKeyFile
    if ($haveEc -and $havePq) { throw 'This Windows account already has both release keys; nothing was changed.' }
    $salt = [Convert]::FromBase64String($doc.salt); $iv = [Convert]::FromBase64String($doc.iv); $ct = [Convert]::FromBase64String($doc.ciphertext)
    $keys = EscrowKeys (ReadPassword 'Escrow password') $salt
    $plain = $null; $seed = $null; $d = $null; $ecPriv = $null
    try {
        if (-not (SameBytes (MacOf $keys[1] $salt $iv $ct) ([Convert]::FromBase64String($doc.mac)))) { throw 'Wrong password, or the escrow file has been changed; nothing was restored.' }
        $aes = [System.Security.Cryptography.Aes]::Create(); $aes.Key = $keys[0]; $aes.IV = $iv
        $plain = $aes.CreateDecryptor().TransformFinalBlock($ct, 0, $ct.Length); $aes.Dispose()
        if ($plain.Length -ne 128) { throw 'The escrow file is damaged; nothing was restored.' }
        $qx = [byte[]]$plain[0..31]; $qy = [byte[]]$plain[32..63]; $d = [byte[]]$plain[64..95]; $seed = [byte[]]$plain[96..127]
        $x = Hex2 $qx; $y = Hex2 $qy
        if ($x -ne $pinXY[0] -or $y -ne $pinXY[1] -or (PqPublicB64 $seed) -ne $pinPq) { throw "The escrowed keys aren't the ones pinned in $updater; nothing was restored." }
        # A key that's already there must be the pinned one (it's kept); a different key is never overwritten.
        if ($haveEc) { $own = PublicXY ([System.Security.Cryptography.CngKey]::Open($KeyName)); if ($own[0] -ne $x -or $own[1] -ne $y) { throw "'$KeyName' exists but isn't the pinned key; nothing was changed." } }
        if ($havePq) { $s0 = PqSeed; try { if (-not (SameBytes $s0 $seed)) { throw "$PqKeyFile exists but isn't the pinned key; nothing was changed." } } finally { Wipe $s0 } }
        if (-not $haveEc) {
            # BCRYPT_ECCKEY_BLOB: magic "ECS2" (ECDSA P-256 private) | cbKey 32 | X | Y | D
            $ecPriv = New-Object byte[] (8 + 96)
            [BitConverter]::GetBytes([int]0x32534345).CopyTo($ecPriv, 0); [BitConverter]::GetBytes([int]32).CopyTo($ecPriv, 4)
            $qx.CopyTo($ecPriv, 8); $qy.CopyTo($ecPriv, 40); $d.CopyTo($ecPriv, 72)
            ImportPersistedEcKey $KeyName $ecPriv
            $own = PublicXY ([System.Security.Cryptography.CngKey]::Open($KeyName))
            if ($own[0] -ne $x -or $own[1] -ne $y) { throw "The restored '$KeyName' doesn't match the escrow; check it before signing anything." }
        }
        if (-not $havePq) {
            New-Item -ItemType Directory -Force (Split-Path $PqKeyFile) | Out-Null
            [IO.File]::WriteAllBytes($PqKeyFile, [System.Security.Cryptography.ProtectedData]::Protect($seed, $PqEntropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
        }
        Write-Host "Restored the ServerManagement release keys for this Windows user (ECDSA $(if ($haveEc) { 'kept' } else { 'restored' }), ML-DSA $(if ($havePq) { 'kept' } else { 'restored' })). They match the keys pinned in $updater."
    } finally { Wipe $plain; Wipe $seed; Wipe $d; Wipe $ecPriv; Wipe $keys[0]; Wipe $keys[1] }
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
    # ADR-123: signing a dist\<ver>\update.json outside build.ps1 taints that version (release-check refuses it).
    $verDir = Split-Path (Split-Path ([IO.Path]::GetFullPath($Path)) -Parent) -Leaf
    if ($verDir -match '^\d+\.\d+\.\d+') { . (Join-Path $PSScriptRoot 'release-common.ps1'); Assert-PipelineRun $verDir 'sign-update' }
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

Write-Host 'Nothing to do: -NewKey, -ShowPublicKey, -Path <update.json>, -Verify <update.json>, -Escrow <file> or -RestoreEscrow <file>.'
