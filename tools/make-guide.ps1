# Builds a PDF from a chapter manifest (docs\guide\*.txt): tools\guide turns the listed Markdown files into one HTML page and
# Microsoft Edge (headless) prints it to dist\<ver>\. One manifest per PDF of the release bundle (0.4.0 rule):
#   guide.txt                 → DarkCipherServerManagement-<ver>-Guide.pdf              (everything)
#   guide-user.txt            → …-User-Guide.pdf
#   guide-developer.txt       → …-Developer-Guide.pdf
#   guide-business.txt        → …-Business-Guide.pdf   (Active Directory / LDAPS / Entra, Group Policy, syslog, log sinks, team, data)
#   guide-security-report.txt → …-Security-Report.pdf  (the security model, the penetration tests done, the results)
#   guide-project.txt         → …-Project-Structure.pdf (the tree of the project and how it is built out)
# Usage: .\tools\make-guide.ps1                 (the complete guide, version from the csproj)
#        .\tools\make-guide.ps1 -Spec docs\guide\guide-business.txt -Suffix Business-Guide
#        .\tools\make-guide.ps1 -All             (every PDF above; build.ps1 does this)
param([string]$Version, [string]$Spec = 'docs\guide\guide.txt', [string]$Suffix = 'Guide', [switch]$All, [switch]$NoChecksums)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $proj = [IO.File]::ReadAllText((Join-Path $root 'src\DcxManager\DcxManager.csproj'))
    if ($proj -notmatch '<Version>([^<]+)</Version>') { throw 'no <Version> in the csproj' }
    $Version = $Matches[1]
    # A release with a label (0.2.0-preview.1) is named and filed by the label, as build.ps1 does.
    if ($proj -match '<ReleaseLabel>([^<]+)</ReleaseLabel>') { $Version = $Matches[1] }
}
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is needed to print the PDF' }

# A list of (manifest, suffix) pairs. Built item by item: PowerShell unrolls a one-element array of arrays on output.
$specs = New-Object System.Collections.Generic.List[object]
if ($All) {
    foreach ($p in @(@('docs\guide\guide.txt', 'Guide'), @('docs\guide\guide-user.txt', 'User-Guide'), @('docs\guide\guide-developer.txt', 'Developer-Guide'),
                     @('docs\guide\guide-business.txt', 'Business-Guide'), @('docs\guide\guide-security-report.txt', 'Security-Report'), @('docs\guide\guide-project.txt', 'Project-Structure'))) { $specs.Add($p) }
} else { $specs.Add(@($Spec, $Suffix)) }

# The generated project table is part of the project PDF: refresh it first so the PDF never lags the tree.
if ($All -or $Spec -like '*guide-project.txt') { & (Join-Path $PSScriptRoot 'write-tree.ps1') }

foreach ($pair in $specs) {
    $manifest = Join-Path $root $pair[0]
    if (-not (Test-Path $manifest)) { throw "manifest not found: $manifest" }
    $work = Join-Path ([IO.Path]::GetTempPath()) ('dcx-guide-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $work | Out-Null
    try {
        $html = Join-Path $work 'guide.html'
        dotnet run --project (Join-Path $PSScriptRoot 'guide\Guide.csproj') -c Release -- $root $manifest $Version $html
        if ($LASTEXITCODE) { throw "guide HTML failed for $($pair[0])" }
        New-Item -ItemType Directory -Force (Join-Path $root "dist\$Version") | Out-Null
        $pdf = Join-Path $root "dist\$Version\DarkCipherServerManagement-$Version-$($pair[1]).pdf"
        # A throwaway browser profile: nothing touches the user's Edge profile.
        $uri = ([Uri]$html).AbsoluteUri
        $p = Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=$work\edge",
            '--no-pdf-header-footer', '--generate-pdf-document-outline', "--print-to-pdf=$pdf", $uri -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(180000)) { $p | Stop-Process -Force; throw 'Edge timed out' }
        if (-not (Test-Path $pdf)) { throw "no PDF was written for $($pair[0])" }
        Write-Host ("PDF: {0} ({1:N0} KB)" -f $pdf, ((Get-Item $pdf).Length / 1KB))
    }
    finally { Start-Sleep 1; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
if (-not $NoChecksums) { & (Join-Path $PSScriptRoot 'write-checksums.ps1') -Version $Version }   # the PDFs are uploaded with their checksums too
