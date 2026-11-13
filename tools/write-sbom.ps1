#Requires -Version 7
# Writes a CycloneDX 1.5 SBOM for one release (0.2.0 phase 7) to dist\<ver>\DarkCipherServerManagement-<ver>-sbom.cdx.json:
#   every NuGet package the projects resolve (from the packages.lock.json files: exact versions and their SHA-512 content
#   hashes, the same pins the locked restore enforces), the .NET runtime the self-contained exe bundles, and the
#   JavaScript bundled as embedded resources (xterm.js). No local paths, user names or machine details.
# Usage: .\tools\write-sbom.ps1 -Version 0.2.0 [-Numeric 0.2.0]
param([Parameter(Mandatory)][string]$Version, [string]$Numeric = $Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'release-common.ps1')
$dist = Join-Path (Get-DistRoot) $Version
if (-not (Test-Path $dist)) { throw "no dist folder for $Version" }
. (Join-Path $PSScriptRoot 'release-common.ps1')
Assert-PipelineRun $Version 'write-sbom'   # ADR-123: outside build.ps1 this taints the version

# Licences of the direct dependencies (THIRD-PARTY-NOTICES.md); Microsoft's System.* / Microsoft.* packages are MIT
# unless listed.
$licences = @{
    'SSH.NET' = 'MIT'; 'PdfPig' = 'Apache-2.0'; 'QRCoder' = 'MIT'; 'Microsoft.Web.WebView2' = 'LicenseRef-Microsoft-WebView2-SDK'
}
function LicenceOf([string]$name) {
    if ($licences.ContainsKey($name)) { return $licences[$name] }
    if ($name -like 'System.*' -or $name -like 'Microsoft.*' -or $name -like 'runtime.*') { return 'MIT' }
    return $null
}

$seen = @{}
$components = New-Object System.Collections.Generic.List[object]
foreach ($lock in Get-ChildItem (Join-Path $root 'src') -Recurse -Filter packages.lock.json | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $j = Get-Content $lock.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($target in $j.dependencies.PSObject.Properties) {
        foreach ($p in $target.Value.PSObject.Properties) {
            $v = $p.Value
            if ($v.type -eq 'Project' -or -not $v.resolved) { continue }
            $key = "$($p.Name)@$($v.resolved)"
            if ($seen.ContainsKey($key)) { continue }
            $c = [ordered]@{
                type = 'library'; 'bom-ref' = "pkg:nuget/$($p.Name)@$($v.resolved)"; name = $p.Name; version = $v.resolved
                purl = "pkg:nuget/$($p.Name)@$($v.resolved)"; scope = 'required'
            }
            # Resolved but not shipped in the win-x64 build: build-time tasks and other platforms' native packages.
            if ($p.Name -like 'Microsoft.NET.ILLink.Tasks' -or $p.Name -match '^runtime\.(linux|osx|freebsd|illumos|solaris|android|ios|tvos|maccatalyst)') { $c.scope = 'excluded' }
            if ($v.contentHash) {
                $hex = -join ([Convert]::FromBase64String($v.contentHash) | ForEach-Object { $_.ToString('x2') })
                $c.hashes = @([ordered]@{ alg = 'SHA-512'; content = $hex })
            }
            if (LicenceOf $p.Name) { $c.licenses = @(@{ license = @{ id = (LicenceOf $p.Name) } }) }
            if ($c.licenses -and $c.licenses[0].license.id -like 'LicenseRef-*') { $c.licenses = @(@{ license = @{ name = 'Microsoft WebView2 SDK licence' } }) }
            $seen[$key] = $c
            $components.Add($c)
        }
    }
}
if ($components.Count -eq 0) { throw 'no packages found in the lock files' }

# The runtime bundled by the self-contained publish, read from the published exe itself (its embedded dependency
# manifest names the runtime packs, e.g. Microsoft.NETCore.App.Runtime.win-x64/10.0.15). 0.6.52: until then this took
# the newest 8.0 runtime on the build PC, so every SBOM since the .NET 10 move (0.6.13, ADR-092) named the wrong runtime.
$exePath = Join-Path $root 'publish\DcxServerManager.exe'
if (-not (Test-Path $exePath)) { throw "no published exe at ${exePath}: write-sbom.ps1 runs after the publish step" }
$exeText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($exePath))
$rt = foreach ($name in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
    $hits = @([regex]::Matches($exeText, [regex]::Escape("$name.Runtime.win-x64/") + '(\d+\.\d+\.\d+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($hits.Count -ne 1) { throw "the published exe names $($hits.Count) versions of $name.Runtime.win-x64 ($($hits -join ', ')), expected exactly one" }
    [pscustomobject]@{ Name = $name; Version = [version]$hits[0] }
}
foreach ($r in $rt) {
    $pack = "$($r.Name).Runtime.win-x64" -replace '^Microsoft\.WindowsDesktop\.App\.Runtime', 'Microsoft.WindowsDesktop.App.Runtime'
    $components.Add([ordered]@{
        type = 'framework'; 'bom-ref' = "pkg:nuget/$pack@$($r.Version)"; name = $pack; version = "$($r.Version)"
        purl = "pkg:nuget/$pack@$($r.Version)"; scope = 'required'; licenses = @(@{ license = @{ id = 'MIT' } })
    })
}

# Bundled as embedded resources (src\DcxManager\Assets\web).
foreach ($js in @(@('@xterm/xterm', '5.5.0'), @('@xterm/addon-fit', '0.10.0'))) {
    $components.Add([ordered]@{
        type = 'library'; 'bom-ref' = "pkg:npm/$($js[0])@$($js[1])"; name = $js[0]; version = $js[1]
        purl = "pkg:npm/$($js[0] -replace '@', '%40')@$($js[1])"; scope = 'required'; licenses = @(@{ license = @{ id = 'MIT' } })
    })
}

$bom = [ordered]@{
    bomFormat = 'CycloneDX'; specVersion = '1.5'; serialNumber = 'urn:uuid:' + [guid]::NewGuid().ToString(); version = 1
    metadata = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        tools = @([ordered]@{ vendor = 'DarkCipher Systems'; name = 'write-sbom.ps1' })
        component = [ordered]@{
            type = 'application'; 'bom-ref' = 'darkcipher-servermanagement'; name = 'DarkCipher ServerManagement'; version = $Version
            supplier = @{ name = 'DarkCipher Systems'; url = @('https://darkciphersystems.com') }
            properties = @(@{ name = 'installer-version'; value = $Numeric })
        }
    }
    components = $components
    dependencies = @([ordered]@{ ref = 'darkcipher-servermanagement'; dependsOn = @($components | ForEach-Object { $_.'bom-ref' }) })
}
$out = Join-Path $dist "DarkCipherServerManagement-$Version-sbom.cdx.json"
[IO.File]::WriteAllText($out, ($bom | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
Write-Host "SBOM: $out ($($components.Count) components)"
