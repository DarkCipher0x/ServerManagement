#Requires -Version 7
# Regression test for the terminal's paste shortcuts (0.6.52; the 0.6.48 double paste, reviewer F10-F12): the terminal page
# (src\DcxManager\Assets\web\terminal.html, the file the build embeds) runs in headless Microsoft Edge - the same Chromium
# engine as WebView2 - with the host bridge replaced by a counter, and real (trusted) key events are sent through the
# DevTools protocol. Each case checks how many paste requests reach the host. No window opens; the browser profile is a
# throwaway folder and DevTools listens on 127.0.0.1 only. build.ps1 runs it after the self-test.
# Usage: .\tools\test-terminal-paste.ps1   (throws on a failure)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$web = Join-Path $root 'src\DcxManager\Assets\web'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is needed for the paste test' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('dcx-pastetest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
foreach ($f in 'xterm.js', 'xterm.css', 'addon-fit.js') { Copy-Item (Join-Path $web $f) $work }
$html = [IO.File]::ReadAllText((Join-Path $web 'terminal.html'), $utf8)
$stub = '<script>window.__msgs=[];window.chrome={webview:{postMessage:m=>window.__msgs.push(JSON.parse(JSON.stringify(m))),addEventListener:(t,f)=>{window.__host=f;}}};</script>'
$i = $html.IndexOf('<script')
[IO.File]::WriteAllText((Join-Path $work 'terminal.html'), $html.Substring(0, $i) + $stub + $html.Substring($i), $utf8)

$port = Get-Random -Minimum 20000 -Maximum 40000
$p = Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-address=127.0.0.1',
    "--remote-debugging-port=$port", "--user-data-dir=$work\profile", 'about:blank' -PassThru -WindowStyle Hidden
$ws = $null
try {
    $target = $null
    for ($k = 0; $k -lt 60 -and -not $target; $k++) {
        try { $target = (Invoke-RestMethod "http://127.0.0.1:$port/json/list") | Where-Object type -eq 'page' | Select-Object -First 1 } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $target) { throw 'headless Edge did not start its DevTools endpoint' }
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $null = $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $script:id = 0
    function Cdp([string]$Method, $Params = @{}) {
        $script:id++
        $msg = @{ id = $script:id; method = $Method; params = $Params } | ConvertTo-Json -Depth 6 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $null = $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $buf = New-Object byte[] 65536
        while ($true) {
            $ms = New-Object IO.MemoryStream
            do { $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).GetAwaiter().GetResult(); $ms.Write($buf, 0, $r.Count) } while (-not $r.EndOfMessage)
            $o = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
            if ($o.id -eq $script:id) { if ($o.error) { throw "CDP $Method failed: $($o.error.message)" }; return $o.result }
        }
    }
    function Eval([string]$Js) { (Cdp 'Runtime.evaluate' @{ expression = $Js; returnByValue = $true }).result.value }
    function Key([string]$Key, [string]$Code, [int]$Vk, [int]$Mods, [switch]$Repeat, [switch]$NoUp) {
        Cdp 'Input.dispatchKeyEvent' @{ type = 'rawKeyDown'; key = $Key; code = $Code; windowsVirtualKeyCode = $Vk; nativeVirtualKeyCode = $Vk; modifiers = $Mods; autoRepeat = [bool]$Repeat } | Out-Null
        if (-not $NoUp) { Cdp 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = $Key; code = $Code; windowsVirtualKeyCode = $Vk; nativeVirtualKeyCode = $Vk; modifiers = $Mods } | Out-Null }
    }
    function Pastes { Eval "window.__msgs.filter(m => m.t === 'paste').length" }
    function Reset { Eval "window.__msgs.length = 0; document.querySelector('.xterm-helper-textarea').focus(); true" | Out-Null }

    Cdp 'Page.navigate' @{ url = ([Uri](Join-Path $work 'terminal.html')).AbsoluteUri } | Out-Null
    for ($k = 0; $k -lt 40 -and -not (Eval "!!document.querySelector('.xterm-helper-textarea')"); $k++) { Start-Sleep -Milliseconds 250 }
    if (-not (Eval "!!document.querySelector('.xterm-helper-textarea')")) { throw 'the terminal page did not load in headless Edge' }
    Start-Sleep -Milliseconds 400

    $ctrlShift = 2 -bor 8
    $cases = [ordered]@{}
    Reset; Key 'V' 'KeyV' 86 $ctrlShift; Start-Sleep -Milliseconds 300; $cases['Ctrl+Shift+V once -> 1 paste'] = @(1, (Pastes))
    Reset; Key 'V' 'KeyV' 86 $ctrlShift; Start-Sleep -Milliseconds 600; Key 'V' 'KeyV' 86 $ctrlShift; Start-Sleep -Milliseconds 300; $cases['Ctrl+Shift+V twice, 600 ms apart -> 2 pastes'] = @(2, (Pastes))
    Reset; Key 'V' 'KeyV' 86 $ctrlShift -NoUp; foreach ($n in 1..4) { Start-Sleep -Milliseconds 120; Key 'V' 'KeyV' 86 $ctrlShift -Repeat -NoUp }
    Cdp 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = 'V'; code = 'KeyV'; windowsVirtualKeyCode = 86; nativeVirtualKeyCode = 86; modifiers = $ctrlShift } | Out-Null
    Start-Sleep -Milliseconds 300; $cases['Ctrl+Shift+V held down (4 auto-repeats over 0.5 s) -> 1 paste'] = @(1, (Pastes))
    Reset; Key 'Insert' 'Insert' 45 8; Start-Sleep -Milliseconds 300; $cases['Shift+Insert -> 1 paste'] = @(1, (Pastes))

    $failed = 0
    foreach ($c in $cases.GetEnumerator()) {
        $ok = $c.Value[0] -eq $c.Value[1]
        if (-not $ok) { $failed++ }
        Write-Host ("  {0}  {1} (got {2})" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $c.Key, $c.Value[1]) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    }
    $edgeVersion = (Cdp 'Browser.getVersion').product
    if ($failed) { throw "terminal paste test: $failed of $($cases.Count) case(s) failed ($edgeVersion)" }
    "terminal paste test: $($cases.Count) cases passed ($edgeVersion)"
}
finally {
    if ($ws) { try { $ws.Dispose() } catch { } }
    if ($p -and -not $p.HasExited) { $p | Stop-Process -Force -ErrorAction SilentlyContinue }
    # The launcher can hand over to child processes: stop every Edge process started with this run's profile.
    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -like "*$work*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
