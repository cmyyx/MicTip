# Generate MicTip tray icons in .ico format (on/off/disconnected)
# Uses System.Drawing to render multiple sizes (16/24/32/48/64) and saves as standard .ico

Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = @(16, 24, 32, 48, 64)

function New-RoundedRectPath($r, [int]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-State {
    param(
        [int]$size,
        [string]$mode,        # on / off / disconnected
        [string]$outFile
    )

    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background Color
    if ($mode -eq 'on') {
        $bg = [System.Drawing.Color]::FromArgb(34, 197, 94)
    } elseif ($mode -eq 'off') {
        $bg = [System.Drawing.Color]::FromArgb(220, 38, 38)
    } else {
        $bg = [System.Drawing.Color]::FromArgb(100, 116, 139)
    }
    $fg = [System.Drawing.Color]::White
    $fgPenWidth = [Math]::Max(1.5, $size / 16.0)

    # Scale factor (base size = 32)
    $s = $size / 32.0

    # Rounded Rect Background
    $bgBrush = New-Object System.Drawing.SolidBrush $bg
    $pad = [int]($size * 0.06)
    $bgRect = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2*$pad), ($size - 2*$pad)
    $bgPath = New-RoundedRectPath $bgRect ([int]($size * 0.25))
    $g.FillPath($bgBrush, $bgPath)
    $bgBrush.Dispose()
    $bgPath.Dispose()

    # Microphone Silhouette
    $pen = New-Object System.Drawing.Pen $fg, $fgPenWidth
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $fillBrush = New-Object System.Drawing.SolidBrush $fg

    # Microphone Capsule
    $headW = 6 * $s
    $headH = 11 * $s
    $headX = ($size - $headW) / 2
    $headY = 6 * $s
    $headRect = New-Object System.Drawing.RectangleF $headX, $headY, $headW, $headH
    $headPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Min($headW, $headH) / 2
    $headPath.AddArc($headRect.X, $headRect.Y, 2*$r, 2*$r, 180, 90)
    $headPath.AddArc($headRect.Right - 2*$r, $headRect.Y, 2*$r, 2*$r, 270, 90)
    $headPath.AddArc($headRect.Right - 2*$r, $headRect.Bottom - 2*$r, 2*$r, 2*$r, 0, 90)
    $headPath.AddArc($headRect.X, $headRect.Bottom - 2*$r, 2*$r, 2*$r, 90, 90)
    $headPath.CloseFigure()
    $g.FillPath($fillBrush, $headPath)
    $headPath.Dispose()

    # Stand Arc
    $arcRect = New-Object System.Drawing.RectangleF (9*$s), (9*$s), (14*$s), (14*$s)
    $g.DrawArc($pen, $arcRect, 0, 180)

    # Stand Pillar & Base
    $g.DrawLine($pen, (16*$s), (23*$s), (16*$s), (26*$s))
    $g.DrawLine($pen, (12*$s), (26*$s), (20*$s), (26*$s))

    # Muted slash line
    if ($mode -eq 'off') {
        $slashPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($fgPenWidth * 1.4)
        $slashPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $slashPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($slashPen, (5*$s), (27*$s), (27*$s), (5*$s))
        $slashPen.Dispose()
    }

    # Disconnected warning sign
    if ($mode -eq 'disconnected') {
        $wsize = 13 * $s
        $wx = $size - $wsize - 1*$s
        $wy = $size - $wsize - 1*$s
        $warnBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(251, 191, 36))
        $g.FillEllipse($warnBrush, $wx, $wy, $wsize, $wsize)
        $warnBrush.Dispose()
        $exFont = New-Object System.Drawing.Font "Segoe UI", ([float]($size * 0.24)), ([System.Drawing.FontStyle]::Bold)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString("!", $exFont, [System.Drawing.Brushes]::Black, (New-Object System.Drawing.RectangleF $wx, $wy, $wsize, $wsize), $sf)
        $exFont.Dispose()
    }

    $pen.Dispose()
    $fillBrush.Dispose()
    $g.Dispose()

    # Create multi-size bitmaps
    $bitmaps = @()
    foreach ($sz in $sizes) {
        if ($sz -eq $size) {
            $bitmaps += $bmp
        } else {
            $scaled = New-Object System.Drawing.Bitmap $sz, $sz
            $sg = [System.Drawing.Graphics]::FromImage($scaled)
            $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $sg.DrawImage($bmp, 0, 0, $sz, $sz)
            $sg.Dispose()
            $bitmaps += $scaled
        }
    }

    Save-Ico $bitmaps $outFile

    foreach ($b in $bitmaps) {
        if ($b -ne $bmp) {
            $b.Dispose()
        }
    }
    $bmp.Dispose()
}

function Save-Ico {
    param(
        $bitmaps,
        [string]$path
    )
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # ICONDIR
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type = icon
    $bw.Write([UInt16]$bitmaps.Count)    # count

    # Save PNG frames
    $pngStreams = @()
    foreach ($b in $bitmaps) {
        $png = New-Object System.IO.MemoryStream
        $b.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngStreams += ,$png
    }

    # ICONDIRENTRY Directory
    $dataOffset = 6 + 16 * $bitmaps.Count
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $b = $bitmaps[$i]
        $png = $pngStreams[$i]
        if ($b.Width -ge 256) { $w = 0 } else { $w = $b.Width }
        if ($b.Height -ge 256) { $h = 0 } else { $h = $b.Height }
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)              # colorCount
        $bw.Write([byte]0)              # reserved
        $bw.Write([UInt16]1)            # planes
        $bw.Write([UInt16]32)           # bitCount
        $bw.Write([UInt32]$png.Length)  # bytes
        $bw.Write([UInt32]$dataOffset)  # offset
        $dataOffset += [int]$png.Length
    }

    # Write PNG Data
    foreach ($png in $pngStreams) {
        $bw.Write($png.ToArray())
        $png.Dispose()
    }

    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

Draw-State 64 'on'           (Join-Path $outDir 'mic-on.ico')
Draw-State 64 'off'          (Join-Path $outDir 'mic-off.ico')
Draw-State 64 'disconnected' (Join-Path $outDir 'mic-disconnected.ico')

Write-Output "Generated icons in $outDir"
Get-ChildItem (Join-Path $outDir 'mic-*.ico') | Select-Object Name, Length
