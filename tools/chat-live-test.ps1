# The CipherChat live run (ADR-117): ServerManagement's chat against a REAL CipherChat server.
#
# Takes CipherChat's own server from a CipherChat checkout at the commit VENDOR.json pins (git archive: the checkout is
# only read), starts it in development mode on 127.0.0.1 with a throwaway state folder, makes its owner's one-time
# enrolment link, and runs the app's live suite (SelfTestChatLive.cs) through run-selftest-watch.ps1 with CipherChat on.
# The suite enrols the owner, signs this PC in, pairs, chats, sends pictures, administers, rings and makes a video call
# with CipherChat's drawn test camera — all off the screen — and deletes its test accounts. The server, its database
# and keys are thrown away afterwards. Needs Python 3 (the server uses only its standard library) and git.
#
#   .\tools\chat-live-test.ps1 -Exe <DcxServerManager.exe> -Scratch <empty folder> [-CipherChat <checkout>]
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Scratch, [string]$CipherChat = (Join-Path $PSScriptRoot '..\..\CipherChat'), [int]$Minutes = 15)
$ErrorActionPreference = 'Stop'
$CipherChat = (Resolve-Path $CipherChat).Path
$vendor = Get-Content (Join-Path $PSScriptRoot '..\src\Vendor\CipherChat\VENDOR.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $Scratch | Out-Null
$src = Join-Path $Scratch 'cipherchat-server'
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
New-Item -ItemType Directory -Force $src | Out-Null
$tar = Join-Path $Scratch 'cipherchat-server.tar'
git -C $CipherChat archive --format=tar -o $tar $vendor.commit server
if ($LASTEXITCODE) { throw "git archive of CipherChat $($vendor.commit) failed" }
tar -xf $tar -C $src
Remove-Item $tar
$server = Join-Path $src 'server\cipherchat\server.py'

# A free loopback port.
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$origin = "http://127.0.0.1:$port"
$state = Join-Path $Scratch 'cipherchat-state'
if (Test-Path $state) { Remove-Item -Recurse -Force $state }
$envs = @{
  CIPHERCHAT_DEV = '1'; CIPHERCHAT_ORIGIN = $origin; CIPHERCHAT_OWNER = 'owner'; CIPHERCHAT_NAME = 'Live run'
  CIPHERCHAT_STATE_DIR = (Join-Path $state 'db'); CIPHERCHAT_KEY_FILE = (Join-Path $state 'keys\server.key')
  CIPHERCHAT_LISTEN = "127.0.0.1:$port"; CIPHERCHAT_TRUST_PROXY = '0'
}
foreach ($k in $envs.Keys) { Set-Item "env:$k" $envs[$k] }
try {
  $link = (& python -I $server owner-invite 2>&1 | Select-Object -Last 1).ToString()
  if ($LASTEXITCODE -or $link -notmatch '#enrol=(.+)$') { throw "owner-invite failed: $link" }
  $enrol = $Matches[1]
  $log = Join-Path $Scratch 'cipherchat-server.log'
  $proc = Start-Process python -ArgumentList '-I', "`"$server`"", 'serve' -PassThru -WindowStyle Hidden -RedirectStandardError $log
  $up = $false
  for ($i = 0; $i -lt 100 -and -not $up; $i++) {
    try { $up = (Invoke-WebRequest "$origin/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { Start-Sleep -Milliseconds 200 }
  }
  if (-not $up) { throw "the CipherChat server didn't start (see $log)" }
  "CipherChat $($vendor.version) ($($vendor.commit.Substring(0, 12))) serving on $origin"
} finally { foreach ($k in $envs.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue } }

try {
  $env:DCX_CHAT_LIVE_ORIGIN = $origin; $env:DCX_CHAT_LIVE_OWNER = 'owner'; $env:DCX_CHAT_LIVE_ENROL = $enrol
  & (Join-Path $PSScriptRoot 'run-selftest-watch.ps1') -Exe $Exe -Scratch $Scratch -Minutes $Minutes -ChatOn
} finally {
  Remove-Item env:DCX_CHAT_LIVE_ORIGIN, env:DCX_CHAT_LIVE_OWNER, env:DCX_CHAT_LIVE_ENROL -ErrorAction SilentlyContinue
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Start-Sleep -Milliseconds 500
  Remove-Item -Recurse -Force $state, $src -ErrorAction SilentlyContinue
}
if (Test-Path "$Scratch\selftest.txt") {
  Get-Content "$Scratch\selftest.txt" -TotalCount 1
  Select-String -Path "$Scratch\selftest.txt" -Pattern '^(ok|FAIL)' | ForEach-Object { $_.Line }
}
if (Test-Path $log) { $u = Select-String -Path $log -Pattern 'UNHANDLED' ; if ($u) { "server reported unhandled errors:"; $u | Select-Object -First 5 | ForEach-Object { $_.Line } } }
