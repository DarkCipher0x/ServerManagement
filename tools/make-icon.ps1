# Generates src\DcxManager\Assets\app.ico (16-256 px) and logo.png (512 px):
# a teal server rack with status LEDs and a ">_" prompt, inside the darkcipher0x ring, on black.
# Run once: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\DcxManager\Assets'
$teal = [System.Drawing.Color]::FromArgb(255, 0x36, 0xD2, 0xCB)
$dim  = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x8C, 0x87)
$bg   = [System.Drawing.Color]::FromArgb(255, 0x05, 0x09, 0x09)
$panel = [System.Drawing.Color]::FromArgb(255, 0x0B, 0x13, 0x14)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure()
    return $p
}

function Draw([int]$s, [bool]$ring) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $tealBrush = New-Object System.Drawing.SolidBrush $teal
    if ($ring) {
        # logo: glow ring like the other darkcipher0x apps
        for ($i = 6; $i -ge 1; $i--) {
            $a = [int](18 * (7 - $i) / 6)
            $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($a, 0x36, 0xD2, 0xCB)), ($s * 0.02 + $i * $s * 0.008)
            $g.DrawEllipse($glow, $s * 0.06, $s * 0.06, $s * 0.88, $s * 0.88)
        }
        $g.DrawEllipse((New-Object System.Drawing.Pen $teal, ($s * 0.022)), $s * 0.06, $s * 0.06, $s * 0.88, $s * 0.88)
        $inset = $s * 0.24
    } else {
        $w = $s - 1; $r = [Math]::Max(2, $s * 0.18)
        $tile = RoundRect 0 0 $w $w $r
        $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $tile)
        $g.DrawPath((New-Object System.Drawing.Pen $teal, ([Math]::Max(1, $s / 32))), $tile)
        $inset = $s * 0.17
    }
    # three rack units
    $units = if ($s -lt 24) { 2 } else { 3 }
    $x = $inset; $wU = $s - 2 * $inset
    $gap = [Math]::Max(1, $s * 0.035)
    $hU = ($s - 2 * $inset - ($units - 1) * $gap) / $units
    $stroke = [Math]::Max(1, $s / $(if ($ring) { 70 } else { 28 }))
    for ($u = 0; $u -lt $units; $u++) {
        $y = $inset + $u * ($hU + $gap)
        $rr = RoundRect $x $y $wU $hU ([Math]::Max(1, $s * 0.03))
        $g.FillPath((New-Object System.Drawing.SolidBrush $panel), $rr)
        $g.DrawPath((New-Object System.Drawing.Pen $teal, $stroke), $rr)
        $led = [Math]::Max(1.5, $hU * 0.26)
        $cy = $y + $hU / 2 - $led / 2
        $g.FillEllipse($tealBrush, $x + $wU - $led * 2.2, $cy, $led, $led)
        if ($s -ge 32) { $g.FillEllipse((New-Object System.Drawing.SolidBrush $dim), $x + $wU - $led * 4.0, $cy, $led, $led) }
        if ($s -ge 24 -and $u -ne 1) {
            # drive slots
            $sp = New-Object System.Drawing.Pen $dim, ([Math]::Max(1, $s / 48))
            $g.DrawLine($sp, $x + $wU * 0.12, $y + $hU / 2, $x + $wU * 0.40, $y + $hU / 2)
        }
    }
    # ">_" prompt on the middle unit
    if ($s -ge 24) {
        $u = 1; $y = $inset + $u * ($hU + $gap)
        $pp = New-Object System.Drawing.Pen $teal, ([Math]::Max(1.5, $s / $(if ($ring) { 48 } else { 22 }))); $pp.StartCap = 'Round'; $pp.EndCap = 'Round'; $pp.LineJoin = 'Round'
        $k = $hU * $(if ($ring) { 0.2 } else { 0.24 }); $ox = $x + $wU * 0.14; $oy = $y + $hU / 2
        $g.DrawLines($pp, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF $ox, ($oy - $k)),
            (New-Object System.Drawing.PointF ($ox + $k), $oy),
            (New-Object System.Drawing.PointF $ox, ($oy + $k))))
        $g.DrawLine($pp, ($ox + $k * 1.6), ($oy + $k), ($ox + $k * 3.0), ($oy + $k))
    }
    $g.Dispose()
    return $bmp
}

$frames = @()
foreach ($s in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = Draw $s $false
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $frames += , @($s, $ms.ToArray())
}
$out = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset); $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
[IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())

$logo = Draw 512 $true
$logo.Save((Join-Path $assets 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $logo.Dispose()
"wrote app.ico and logo.png"
