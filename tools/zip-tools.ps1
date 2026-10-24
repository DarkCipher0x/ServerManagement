# Zip archives with forward-slash entry names (0.6.23). Windows PowerShell 5.1 runs on .NET Framework, whose
# ZipFile.CreateFromDirectory and Compress-Archive write "folder\file": against the ZIP specification (APPNOTE 4.4.17),
# so Linux and macOS tools warn or extract files with backslashes in their names. The website's source check found it
# in the 0.6.22 source zip. Every release zip is written here instead; release-check.ps1 refuses any backslash entry.
# Pure ASCII (Windows PowerShell 5.1 reads scripts without a BOM as the ANSI code page).
#
# Usage: . .\tools\zip-tools.ps1 ; New-ReleaseZip -Source <folder> -Zip <file.zip> [-IncludeBaseFolder]
function New-ReleaseZip {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Zip, [switch]$IncludeBaseFolder)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $root = (Resolve-Path $Source).Path.TrimEnd('\')
    $base = if ($IncludeBaseFolder) { Split-Path $root -Parent } else { $root }
    $Zip = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Zip)   # .NET resolves relative paths against its own current folder
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    $fs = [IO.File]::Open($Zip, [IO.FileMode]::CreateNew)
    try {
        $archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($f in Get-ChildItem $root -Recurse -File -Force | Sort-Object FullName) {
                $name = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $f.LastWriteTime
                $in = [IO.File]::OpenRead($f.FullName)
                $out = $entry.Open()
                try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $fs.Dispose() }
}

# The entry names of a zip that use a backslash (none is allowed in a release zip).
function Get-BackslashEntries([Parameter(Mandatory)][string]$Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($Zip)
    try { @($z.Entries | Where-Object { $_.FullName.Contains('\') } | ForEach-Object FullName) } finally { $z.Dispose() }
}
