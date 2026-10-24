# Sample add-on for DarkCipher ServerManagement (API 2: encrypted bridge). Protocol: docs/ADDONS.md.
# Runs at Low integrity: the only writable folder is $env:DCX_ADDON_DATA.
# DcxAddon.ps1 + DcxBridge.dll (samples/addons/sdk) do the encryption: every message to and from the app is AES-256-GCM.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DcxAddon.ps1"

try {
    $invoke = Connect-Dcx
    switch ($invoke.params.command) {
        'summary' {
            $hosts = @(Invoke-Dcx 'hosts.list')
            $byFolder = $hosts | Group-Object folder | Sort-Object Count -Descending | ForEach-Object { "  $($_.Name): $($_.Count)" }
            $text = "You have $($hosts.Count) saved host(s):`n" + ($byFolder -join "`n")
            $active = Invoke-Dcx 'incidents.active'
            if ($active) {
                [void](Invoke-Dcx 'incidents.addEvent' @{ title = 'Host inventory snapshot'; text = $text })
                $text += "`n`nAdded to the active incident '$($active.title)'."
            }
            Complete-Dcx $text -Show
        }
        'note-host' {
            $active = Invoke-Dcx 'incidents.active'
            if (-not $active) { Complete-Dcx 'There is no active incident.' -Show; exit 0 }
            $name = $invoke.params.context.hostName
            [void](Invoke-Dcx 'incidents.addEvent' @{ title = "Looking at $name"; kind = 'event' })
            Complete-Dcx "Added 'Looking at $name' to '$($active.title)'." -Show
        }
        'capabilities' {
            # The full-control API (ADR-036): everything an add-on can call, and what this one has been granted.
            $caps = Get-DcxCapabilities
            $granted = @($caps.tools | Where-Object granted).Count
            $text = "Bridge: API $($caps.api), encrypted: $($caps.encrypted)`n" +
                    "$(@($caps.methods).Count) bridge methods, $(@($caps.tools).Count) app capabilities ($granted usable with this add-on's permissions).`n`n" +
                    (($caps.tools | Select-Object -First 15 | ForEach-Object { "  $($_.name) - $($_.risk), needs $($_.permission)" }) -join "`n")
            Complete-Dcx $text -Show
        }
        default { Complete-Dcx "Unknown command $($invoke.params.command)" -Show }
    }
}
catch { try { Complete-Dcx "Error: $_" -Show } catch { }; exit 1 }
