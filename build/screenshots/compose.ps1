# Schneidet die Aufnahmen aus capture.ps1 zu README-Bildern zu.
param([string]$Shots, [string]$Out)
Add-Type -AssemblyName System.Drawing
function Load($n) { [System.Drawing.Bitmap]::FromFile("$Shots\$n.png") }
function Rect($x, $y, $w, $h) { New-Object System.Drawing.Rectangle $x, $y, $w, $h }
function Crop($n, $x, $y, $w, $h, $file) {
  $src = Load $n; $dst = $src.Clone((Rect $x $y $w $h), $src.PixelFormat)
  $dst.Save("$Out\$file", [System.Drawing.Imaging.ImageFormat]::Png); $src.Dispose(); $dst.Dispose()
}
Crop 'media-expanded' 330 0 940 300 'hero.png'
Crop 'shelf'          330 0 940 300 'shelf.png'
Crop 'notes-expanded' 330 0 940 300 'update.png'

# Kompakte Zustände nebeneinander: Leerlauf, Musik, Update mit zweiter Aktivität als Perle
$tiles = 'idle-compact', 'media-compact', 'notes-compact'
$tw = 440; $th = 84; $gap = 16
$strip = New-Object System.Drawing.Bitmap ($tw * 3 + $gap * 2), $th
$g = [System.Drawing.Graphics]::FromImage($strip)
for ($i = 0; $i -lt 3; $i++) {
  $src = Load $tiles[$i]
  $g.DrawImage($src, (Rect ($i * ($tw + $gap)) 0 $tw $th), (Rect (800 - $tw / 2) 0 $tw $th), 'Pixel')
  $src.Dispose()
}
$strip.Save("$Out\states.png", [System.Drawing.Imaging.ImageFormat]::Png)

# Social-Preview 1280x640: aufgeklappte Notch auf dem Hintergrund, Name darunter
$bg = Load 'media-expanded'
$social = New-Object System.Drawing.Bitmap 1280, 640
$g = [System.Drawing.Graphics]::FromImage($social); $g.InterpolationMode = 'HighQualityBicubic'
$g.DrawImage($bg, (Rect 0 0 1280 400), (Rect 160 0 1280 400), 'Pixel')
for ($y = 400; $y -lt 640; $y++) { $g.DrawImage($bg, (Rect 0 $y 1280 1), (Rect 160 398 1280 1), 'Pixel') }   # Verlauf nach unten fortsetzen
$g.TextRenderingHint = 'AntiAliasGridFit'
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString('Brim', (New-Object System.Drawing.Font 'Segoe UI Semibold', 48), [System.Drawing.Brushes]::WhiteSmoke, (New-Object System.Drawing.RectangleF 0, 400, 1280, 90), $fmt)
$sub = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 200, 205, 215))
$g.DrawString('A notch for Windows', (New-Object System.Drawing.Font 'Segoe UI', 22), $sub, (New-Object System.Drawing.RectangleF 0, 490, 1280, 50), $fmt)
$social.Save("$Out\social-preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
