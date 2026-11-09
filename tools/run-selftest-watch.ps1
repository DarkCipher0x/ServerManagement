param([string]$Exe, [string]$Scratch, [int]$Minutes = 10, [switch]$AiOff, [switch]$ChatOn, [switch]$StreamOn, [string[]]$Addon)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class Win {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public struct R { public int L, T, Rt, B; }
  public static List<string> OnScreen(uint pid) {
    var o = new List<string>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { R r; GetWindowRect(h, out r);
        if (r.Rt > -3000 && r.L > -3000 && r.Rt - r.L > 20 && r.B - r.T > 20) { var t = new StringBuilder(200); GetWindowText(h, t, 200); var c = new StringBuilder(200); GetClassName(h, c, 200);
          o.Add(string.Format("{0} [{1}] at {2},{3} {4}x{5}", t, c, r.L, r.T, r.Rt - r.L, r.B - r.T)); } }
      return true; }, IntPtr.Zero);
    return o; } }
"@
foreach ($d in 'selftest-data','selftest-data-logs','selftest-data-addons','moved-store','commander-test','shots','addon-pkg') { if (Test-Path "$Scratch\$d") { Remove-Item -Recurse -Force "$Scratch\$d" } }
if (Test-Path "$Scratch\selftest.txt") { Remove-Item "$Scratch\selftest.txt" }
# A build may still be writing the exe (suspected cause of the s30 "exit -1, no report" run): wait until it is quiet.
for ($i = 0; $i -lt 30 -and ((Get-Date) - (Get-Item $Exe).LastWriteTime).TotalSeconds -lt 3; $i++) { Start-Sleep -Milliseconds 500 }
$env:DCX_DATA_DIR = "$Scratch\selftest-data"
# 0.2.0: a fresh store is a new install, where AI features start off; the normal run turns them on, -AiOff runs the
# AI-off suite (the module never loaded).
$env:DCX_AI_FEATURES = if ($AiOff) { '0' } else { '1' }
# ADR-117: CipherChat is off by default (and in the normal run, which checks nothing of it is loaded); -ChatOn loads it.
$env:DCX_CHAT_FEATURES = if ($ChatOn) { '1' } else { '0' }
# ADR-119: the AI-off suite checks the app with no module at all, so Windows management is off there too.
$env:DCX_WINTOOLS_FEATURES = if ($AiOff) { '0' } else { $null }
# ADR-120: module add-ons are installed into the scratch store before the modules load. -Addon <.dcxaddon> installs those;
# -StreamOn packs and signs the stream manager (samples\addons\stream, release keys: run with LOCALAPPDATA pointing at
# them) into the scratch folder and installs it. Without either, the run checks nothing of the stream manager exists.
$addons = @($Addon | Where-Object { $_ })
if ($StreamOn) {
  $root = Split-Path $PSScriptRoot -Parent
  & (Join-Path $root 'samples\addons\pack.ps1') (Join-Path $root 'samples\addons\stream') -OutDir "$Scratch\addon-build" | Out-Host
  $addons += @(Get-ChildItem "$Scratch\addon-build" -Filter 'com.darkcipher.stream-*.dcxaddon' | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object FullName)
}
$env:DCX_SELFTEST_ADDONS = if ($addons.Count -gt 0) { ($addons | ForEach-Object { (Resolve-Path $_).Path }) -join ';' } else { $null }
$started = Get-Date
$p = Start-Process $Exe -ArgumentList '--selftest', "$Scratch\selftest.txt" -PassThru
$null = $p.Handle   # keep the handle, or Windows PowerShell can lose the real exit code
$env:DCX_DATA_DIR = $null
$env:DCX_AI_FEATURES = $null
$env:DCX_CHAT_FEATURES = $null
$env:DCX_WINTOOLS_FEATURES = $null
$env:DCX_SELFTEST_ADDONS = $null
$seen = @{}
$deadline = (Get-Date).AddMinutes($Minutes)
while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
  foreach ($w in [Win]::OnScreen([uint32]$p.Id)) { if (-not $seen.ContainsKey($w)) { $seen[$w] = 1; "VISIBLE WINDOW: $w" } }
  if ($seen.Count -gt 0 -and ($seen.Keys | Where-Object { $_ -match 'Microsoft .NET|#32770' })) { "error dialog seen - stopping"; Stop-Process -Id $p.Id -Force; break }
  Start-Sleep -Milliseconds 300
}
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; "killed (timeout)" }
"exit $($p.ExitCode)"
if ($seen.Count -eq 0) { "no window appeared on screen" }
if (-not (Test-Path "$Scratch\selftest.txt")) {
  # Never just "no report": say what is known (ADR-025).
  "NO REPORT after $([int]((Get-Date) - $started).TotalSeconds) s. exe written $((Get-Item $Exe).LastWriteTime.ToString('HH:mm:ss')), started $($started.ToString('HH:mm:ss'))"
  Get-CimInstance Win32_Process -Filter "Name='DcxServerManager.exe'" | ForEach-Object { "running: pid $($_.ProcessId) $($_.CommandLine)" }
  $log = Get-ChildItem "$Scratch\selftest-data-logs" -Filter 'errors-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if ($log) { "error log: $($log.FullName) ($($log.Length) bytes, sealed entries can be read in the app)" }
}
