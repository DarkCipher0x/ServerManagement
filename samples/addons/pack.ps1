# Packs an add-on folder into a .dcxaddon file (a zip with addon.json at its root).
# Output: dist\addons\<add-on id>\<id>-<version>.dcxaddon plus <file>.sha256 ("<sha256>  <file>", sha256sum format).
# Each add-on has its own folder; every version stays in it (an existing version is never overwritten: bump "version").
# Usage: .\pack.ps1 .\hello-hosts
#
# Module add-ons (ADR-120: addon.json has "module": {"assembly", "type", "api"}): the project in <folder>\src is built
# (Release), and the package is staged from the folder's files (not src\) plus the built assembly. module.json lists every
# staged file with its SHA-256 and is signed with the release keys through tools\sign-update.ps1 -Path (module.json.sig
# and module.json.pq.sig). The app loads a module only when that signature verifies. Signing needs this Windows user's
# release keys, so run it from the account that holds them (with LOCALAPPDATA pointing at the folder that has them).
# -OutDir <dir> writes only the .dcxaddon there (a throwaway build for the self-test: no dist folder, no zip copy, and an
# existing file there is replaced).
param([Parameter(Mandatory = $true)][string]$Folder, [string]$OutDir)
$ErrorActionPreference = 'Stop'
$src = (Resolve-Path $Folder).Path
$manifest = [IO.File]::ReadAllText((Join-Path $src 'addon.json')) | ConvertFrom-Json
if ($manifest.id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or "$($manifest.version)" -notmatch '^[0-9A-Za-z.+-]+$') { throw 'addon.json needs a plain "id" and "version"' }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$name = "{0}-{1}.dcxaddon" -f $manifest.id, $manifest.version
if ($OutDir) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $dir = (Resolve-Path $OutDir).Path
    $out = Join-Path $dir $name
    if (Test-Path $out) { Remove-Item $out }
} else {
    $dir = Join-Path $repo "dist\addons\$($manifest.id)"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir $name
    if (Test-Path $out) { throw "$out already exists - bump ""version"" in addon.json (earlier versions are kept)" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packFrom = $src
$stage = $null
if ($manifest.module) {
    $mod = $manifest.module
    if ("$($mod.assembly)" -notmatch '^[A-Za-z0-9._-]+\.dll$') { throw 'module.assembly must be a .dll file name at the package root' }
    $proj = Get-ChildItem (Join-Path $src 'src') -Filter *.csproj | Select-Object -First 1
    if (-not $proj) { throw "a module add-on needs its project in $src\src" }
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("dcxaddon-" + [guid]::NewGuid().ToString('N'))
    $bin = Join-Path $stage '.bin'
    New-Item -ItemType Directory -Force $stage, $bin | Out-Null
    try {
        & dotnet build $proj.FullName -c Release -nologo -v q -o $bin | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'the module project did not build' }
        $pkg = Join-Path $stage 'pkg'
        New-Item -ItemType Directory $pkg | Out-Null
        # The package: the folder's own files (never src\, bin\ or obj\) and the built assembly (Core and Ui come from the app).
        Get-ChildItem $src -File | Where-Object { $_.Name -notin @('module.json', 'module.json.sig', 'module.json.pq.sig') } | ForEach-Object { Copy-Item $_.FullName $pkg }
        $dll = Join-Path $bin $mod.assembly
        if (-not (Test-Path $dll)) { throw "the build did not produce $($mod.assembly)" }
        Copy-Item $dll $pkg
        # module.json: what the release keys vouch for (the app checks every field and every file at every start).
        $files = Get-ChildItem $pkg -File | Sort-Object { $_.Name } -Culture ([Globalization.CultureInfo]::InvariantCulture)
        $lines = @()
        foreach ($f in ($files | Sort-Object Name -CaseSensitive)) { $lines += '    "' + $f.Name + '": "' + (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower() + '"' }
        $json = "{`n  ""purpose"": ""dcx-addon-module"",`n  ""id"": ""$($manifest.id)"",`n  ""version"": ""$($manifest.version)"",`n  ""api"": $([int]$mod.api),`n  ""assembly"": ""$($mod.assembly)"",`n  ""type"": ""$($mod.type)"",`n  ""files"": {`n" + ($lines -join ",`n") + "`n  }`n}`n"
        $mj = Join-Path $pkg 'module.json'
        [IO.File]::WriteAllText($mj, $json, (New-Object Text.UTF8Encoding $false))
        & (Join-Path $repo 'tools\sign-update.ps1') -Path $mj
        if (-not (Test-Path "$mj.sig") -or -not (Test-Path "$mj.pq.sig")) { throw 'module.json was not signed' }
        $packFrom = $pkg
        Write-Host "Module $($mod.type) in $($mod.assembly): $($files.Count) file(s) signed in module.json"
    } catch { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue; throw }
}
try { [IO.Compression.ZipFile]::CreateFromDirectory($packFrom, $out) }
finally { if ($stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue } }
$sha = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
Write-Host "Packed $out"
Write-Host "SHA-256 $sha"
if ($OutDir) { return }
[IO.File]::WriteAllText("$out.sha256", "$sha  $name`n", (New-Object Text.UTF8Encoding $false))
# DarkCipher0x: every packaged add-on also gets a zipped copy: dist\addons\<id>.zip holds the <id> folder (all versions + .sha256).
$zip = Join-Path $repo "dist\addons\$($manifest.id).zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "Zipped $zip"
