# Legt den Datenordner für den Vorführmodus an (BRIM_DEMO): Cover, Ablage-Dateien, Einstellungen.
param([string]$Dir)
Add-Type -AssemblyName System.Drawing
if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
New-Item -ItemType Directory "$Dir\files" | Out-Null

function Gradient($w, $h, $c1, $c2, $angle) {
  $b = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, $w, $h), ([System.Drawing.ColorTranslator]::FromHtml($c1)), ([System.Drawing.ColorTranslator]::FromHtml($c2)), $angle
  $g.FillRectangle($br, 0, 0, $w, $h)
  return @($b, $g)
}

# Cover: Verlauf Koralle → Violett, Kreis über Horizont (wie im Design)
$b, $g = Gradient 600 600 '#FF8A66' '#7A5CFF' 50
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 240, 245)), 14
$g.DrawEllipse($pen, 170, 110, 260, 260)
$g.DrawLine($pen, 90, 470, 510, 470)
$b.Save("$Dir\cover.png", [System.Drawing.Imaging.ImageFormat]::Png)

# Ablage: Bild, Dokumente, Ordner
$b, $g = Gradient 800 600 '#2B3A67' '#5BD98A' 30
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 214, 102))), 470, 90, 180, 180)
$b.Save("$Dir\files\Moodboard.png", [System.Drawing.Imaging.ImageFormat]::Png)
function Filler($name, $bytes) { $r = New-Object byte[] $bytes; (New-Object Random 7).NextBytes($r); [IO.File]::WriteAllBytes("$Dir\files\$name", $r) }
Filler 'Pitch Deck.pdf' 2480000
Filler 'Assets.zip' 18350000
Filler 'Release Notes.md' 4200
New-Item -ItemType Directory "$Dir\files\Fotos Lissabon" | Out-Null

@'
{
  "Monitors": "Primary",
  "AutoHide": { "WhenOverlapped": false, "WhileDraggingWindows": false, "PeekFromTopEdge": false },
  "Updates": { "Enabled": false }
}
'@ | Set-Content "$Dir\settings.json"

