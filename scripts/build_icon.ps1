# App-Icon-Generator fuer DNS-Pilot.
#
# Motiv: goldene Kompassnadel ("Pilot") auf Kroste-Blau, umgeben von einem
# hellen Ring mit drei Netzknoten ("DNS" - Namen, die auf Adressen zeigen).
# Die Nadel ist zweifarbig (Gold vorn, Hellgrau hinten), damit die Richtung
# auch bei 16px noch zu erkennen ist.
#
# Erzeugt:
#   DnsPilot/Assets/dnspilot.png   (256x256, Master fuer Fenster und Tray)
#   DnsPilot/Assets/dnspilot.ico   (multi-res 16..256 fuer <ApplicationIcon>)
#
# Aufruf aus dem Repo-Root:  pwsh -File scripts/build_icon.ps1
#
# Reines ASCII im Quelltext (Windows-PowerShell-5.1-ANSI-Falle).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$APP_NAME = 'DnsPilot'

# Kroste-Palette (siehe App.axaml)
$BG     = [System.Drawing.Color]::FromArgb(255, 18, 62, 107)    # #123E6B
$FG     = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$FG_DIM = [System.Drawing.Color]::FromArgb(255, 214, 224, 238)
$GOLD   = [System.Drawing.Color]::FromArgb(255, 224, 177, 76)   # #E0B14C

$CORNER = 48

function New-Graphics([System.Drawing.Bitmap]$bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    return $g
}

function Add-RoundedRect($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r, $brush) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $path.Dispose()
}

function Add-Background($g, [int]$size, [double]$scale) {
    # Radius IMMER gegen die Kantenlaenge deckeln - sonst ueberlappen sich bei
    # kleinen Groessen die vier Boegen, der Pfad degeneriert und die Ecken
    # zerfallen sichtbar. 48px ist die Standardgroesse im Explorer, ein Fehler
    # dort faellt sofort auf.
    $corner = [Math]::Max(2, [int][Math]::Min($CORNER * $scale, $size * 0.22))
    $b = New-Object System.Drawing.SolidBrush $BG
    Add-RoundedRect $g 0 0 ($size - 1) ($size - 1) $corner $b
    $b.Dispose()
}

function Add-Needle($g, [double]$cx, [double]$cy, [double]$len, [double]$half) {
    # Raute entlang der Diagonale nach rechts oben: vordere Haelfte Gold,
    # hintere Haelfte hell. Die Spitze zeigt nach "Nordost".
    $k = [Math]::Sqrt(0.5)
    $tipX = $cx + $len * $k;  $tipY = $cy - $len * $k
    $tailX = $cx - $len * $k; $tailY = $cy + $len * $k
    $leftX = $cx - $half * $k; $leftY = $cy - $half * $k
    $rightX = $cx + $half * $k; $rightY = $cy + $half * $k

    $front = New-Object System.Drawing.Drawing2D.GraphicsPath
    $front.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([float]$tipX), ([float]$tipY)),
        (New-Object System.Drawing.PointF ([float]$rightX), ([float]$rightY)),
        (New-Object System.Drawing.PointF ([float]$leftX), ([float]$leftY))))
    $back = New-Object System.Drawing.Drawing2D.GraphicsPath
    $back.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([float]$tailX), ([float]$tailY)),
        (New-Object System.Drawing.PointF ([float]$leftX), ([float]$leftY)),
        (New-Object System.Drawing.PointF ([float]$rightX), ([float]$rightY))))

    $bGold = New-Object System.Drawing.SolidBrush $GOLD
    $bDim  = New-Object System.Drawing.SolidBrush $FG_DIM
    $g.FillPath($bGold, $front)
    $g.FillPath($bDim, $back)
    $bGold.Dispose(); $bDim.Dispose(); $front.Dispose(); $back.Dispose()
}

function New-IconLarge([int]$size) {
    $scale = $size / 256.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp
    Add-Background $g $size $scale

    $cx = 128 * $scale; $cy = 128 * $scale
    $ringR = 86 * $scale

    # Ring
    $penRing = New-Object System.Drawing.Pen $FG_DIM, ([float][Math]::Max(2, 7 * $scale))
    $g.DrawEllipse($penRing, [float]($cx - $ringR), [float]($cy - $ringR),
        [float](2 * $ringR), [float](2 * $ringR))

    # Drei Netzknoten auf dem Ring, mit hintergrundfarbenem Rand abgesetzt.
    $bFg = New-Object System.Drawing.SolidBrush $FG
    $bBg = New-Object System.Drawing.SolidBrush $BG
    $nodeR = 15 * $scale
    $gap = 6 * $scale
    foreach ($deg in @(200, 290, 110)) {
        $rad = $deg * [Math]::PI / 180
        $nx = $cx + $ringR * [Math]::Cos($rad)
        $ny = $cy + $ringR * [Math]::Sin($rad)
        $g.FillEllipse($bBg, [float]($nx - $nodeR - $gap), [float]($ny - $nodeR - $gap),
            [float](2 * ($nodeR + $gap)), [float](2 * ($nodeR + $gap)))
        $g.FillEllipse($bFg, [float]($nx - $nodeR), [float]($ny - $nodeR),
            [float](2 * $nodeR), [float](2 * $nodeR))
    }

    Add-Needle $g $cx $cy (74 * $scale) (24 * $scale)

    # Achse
    $hub = 9 * $scale
    $g.FillEllipse($bBg, [float]($cx - $hub), [float]($cy - $hub), [float](2 * $hub), [float](2 * $hub))

    $penRing.Dispose(); $bFg.Dispose(); $bBg.Dispose()
    $g.Dispose()
    return $bmp
}

function New-IconSmall([int]$size) {
    # Vereinfachte Variante fuer 16..48px: ohne Knoten, dickerer Ring und
    # breitere Nadel - die Knoten matschen bei 16px zu Flecken.
    $bmp = New-Object System.Drawing.Bitmap $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp
    Add-Background $g $size 1.0

    $cx = $size / 2.0; $cy = $size / 2.0
    $ringR = $size * 0.36
    $penRing = New-Object System.Drawing.Pen $FG_DIM, ([float][Math]::Max(1.2, $size * 0.06))
    $g.DrawEllipse($penRing, [float]($cx - $ringR), [float]($cy - $ringR),
        [float](2 * $ringR), [float](2 * $ringR))

    Add-Needle $g $cx $cy ($size * 0.33) ($size * 0.13)

    $penRing.Dispose()
    $g.Dispose()
    return $bmp
}

function New-Icon([int]$size) {
    if ($size -le 48) { return New-IconSmall $size } else { return New-IconLarge $size }
}

function Save-Ico([System.Drawing.Bitmap[]]$images, [string]$path) {
    # System.Drawing kann keine Multi-Res-ICOs schreiben - deshalb das
    # ICO-Format von Hand. Eingebettete PNGs sind ab Windows Vista erlaubt.
    $blobs = foreach ($img in $images) {
        $ms = New-Object System.IO.MemoryStream
        $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    try {
        $bw.Write([uint16]0)                  # reserved
        $bw.Write([uint16]1)                  # type: 1 = Icon
        $bw.Write([uint16]$images.Count)

        $offset = 6 + 16 * $images.Count
        for ($i = 0; $i -lt $images.Count; $i++) {
            $w = $images[$i].Width
            # 256 wird im ICO-Header als 0 kodiert.
            $bw.Write([byte]($(if ($w -ge 256) { 0 } else { $w })))
            $bw.Write([byte]($(if ($w -ge 256) { 0 } else { $w })))
            $bw.Write([byte]0)                # Farbanzahl (0 = truecolor)
            $bw.Write([byte]0)                # reserved
            $bw.Write([uint16]1)              # color planes
            $bw.Write([uint16]32)             # bits per pixel
            $bw.Write([uint32]$blobs[$i].Length)
            $bw.Write([uint32]$offset)
            $offset += $blobs[$i].Length
        }
        foreach ($blob in $blobs) { $bw.Write($blob) }
    }
    finally {
        $bw.Dispose(); $fs.Dispose()
    }
}

# Repo-relativ: <repo>/scripts/build_icon.ps1 -> <repo>/DnsPilot/Assets
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) "$APP_NAME/Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$pngPath = Join-Path $assets "$($APP_NAME.ToLowerInvariant()).png"
$icoPath = Join-Path $assets "$($APP_NAME.ToLowerInvariant()).ico"

$master = New-Icon 256
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote $pngPath (256x256)"

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$icons = foreach ($s in $sizes) { New-Icon $s }
Save-Ico $icons $icoPath
Write-Host "Wrote $icoPath (multi-res: $($sizes -join ', '))"

foreach ($i in $icons) { $i.Dispose() }
$master.Dispose()
