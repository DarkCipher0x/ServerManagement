#Requires -Version 7
# Installs the pinned gitleaks the release pipeline requires (ADR-123; tools\gitleaks.pin: version, the SHA-256 of the
# official windows_x64 zip and of gitleaks.exe - the same pin as MaskedMail). It goes to
# %LOCALAPPDATA%\DarkCipher\tools\gitleaks-<ver>\ (never into the repository, never a system-wide install). The download
# is refused unless both hashes match. Usage: .\tools\get-gitleaks.ps1   (does nothing when the pinned copy is there)
$ErrorActionPreference = 'Stop'
$pin = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'gitleaks.pin')) | ConvertFrom-Json
$dest = Join-Path $env:LOCALAPPDATA "DarkCipher\tools\gitleaks-$($pin.version)"
$exe = Join-Path $dest 'gitleaks.exe'
if ((Test-Path $exe) -and (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $pin.exeSha256) { Write-Host "gitleaks $($pin.version) is already installed: $exe"; return }
$url = "https://github.com/gitleaks/gitleaks/releases/download/v$($pin.version)/gitleaks_$($pin.version)_windows_x64.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dcx-gitleaks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'gitleaks.zip'
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -ne $pin.zipSha256) { throw "gitleaks download has SHA-256 $got, the pin says $($pin.zipSha256): refused" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $tmp 'x'))
    $new = Join-Path $tmp 'x\gitleaks.exe'
    $exeHash = (Get-FileHash $new -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($pin.exeSha256 -and $exeHash -ne $pin.exeSha256) { throw "gitleaks.exe in the verified zip has SHA-256 $exeHash, the pin says $($pin.exeSha256): refused" }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $new $dest -Force
    Copy-Item (Join-Path $tmp 'x\LICENSE') $dest -ErrorAction SilentlyContinue
    Write-Host "gitleaks $($pin.version) installed to $dest (zip SHA-256 verified; gitleaks.exe SHA-256 $exeHash)"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
