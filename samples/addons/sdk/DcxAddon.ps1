# DarkCipher ServerManagement add-on SDK for Windows PowerShell 5.1 (API 2: encrypted bridge, ADR-035).
# Put DcxAddon.ps1 and DcxBridge.dll next to your script and dot-source it:   . "$PSScriptRoot\DcxAddon.ps1"
# Every message to and from the app is AES-256-GCM with keys made fresh for each run (ECDH P-256); see DcxBridge.cs.
Add-Type -Path (Join-Path $PSScriptRoot 'DcxBridge.dll')
$script:DcxCallId = 0

# Runs the handshake and returns the app's "invoke" message (params.command, params.context).
function Connect-Dcx { return ([DcxBridge]::Connect() | ConvertFrom-Json) }

# Sends one message object.
function Send-Dcx($Message) { [DcxBridge]::Write(($Message | ConvertTo-Json -Compress -Depth 8)) }

# Calls an app method and returns its result; throws on an error reply.
function Invoke-Dcx([string]$Method, $Params = @{}) {
    $script:DcxCallId++
    Send-Dcx @{ jsonrpc = '2.0'; id = $script:DcxCallId; method = $Method; params = $Params }
    $line = [DcxBridge]::Read()
    if ($null -eq $line) { throw 'the app closed the bridge' }
    $reply = $line | ConvertFrom-Json
    if ($reply.error) { throw "$Method failed: $($reply.error.message)" }
    return $reply.result
}

# Everything this add-on can call (ADR-036): .methods (bridge methods) and .tools (every app capability, with its risk,
# the permission it needs and whether that is granted).
function Get-DcxCapabilities { return (Invoke-Dcx 'app.capabilities') }

# Runs any app capability by name, e.g.  Invoke-DcxTool 'host_overview' @{ host = 'web-1' }
# Reads need app.read (private ones app.private); changes need app.control and the user approves each one.
function Invoke-DcxTool([string]$Tool, $Arguments = @{}) { return (Invoke-Dcx 'app.call' @{ tool = $Tool; args = $Arguments }).result }

# Adds a line to the run log.
function Write-DcxLog([string]$Text) { Send-Dcx @{ jsonrpc = '2.0'; method = 'log'; params = @{ text = $Text } } }

# Ends the run with a result (shown to the user when -Show).
function Complete-Dcx([string]$Text, [switch]$Show) { Send-Dcx @{ jsonrpc = '2.0'; method = 'done'; params = @{ text = $Text; show = [bool]$Show } } }
