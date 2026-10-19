# Nimmt die echte App im Vorführmodus auf: neutrale Fläche dahinter, keine Mausbewegung (UI Automation / zweiter Start).
param([ValidateSet('media', 'notes', 'idle')][string]$Mode = 'media')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public static class Dpi8 { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[Dpi8]::SetProcessDPIAware() | Out-Null
$dir = $PSScriptRoot
$out = Join-Path ([IO.Path]::GetTempPath()) 'brim-shots'; New-Item -ItemType Directory $out -Force | Out-Null
$root = Resolve-Path "$PSScriptRoot\..\.."
$exe = Get-ChildItem "$root\src\Brim\bin\x64\Debug" -Recurse -Filter Brim.exe | Select-Object -First 1 -ExpandProperty FullName
$installed = "$env:LOCALAPPDATA\BrimNotch\current\Brim.exe"
$data = Join-Path ([IO.Path]::GetTempPath()) 'brim-demo'

function Shot($name) {
  $b = New-Object System.Drawing.Bitmap 1600, 520
  $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen(480, 0, 0, 0, $b.Size)
  $b.Save((Join-Path $out "$name.png")); $g.Dispose(); $b.Dispose(); "aufgenommen: $name"
}
function Demo { Start-Process $exe -Environment @{ BRIM_DEMO = $data } }
function Invoke-Name($name) {
  $p = (Get-Process Brim | Where-Object Path -eq $exe).Id
  $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p
  $n = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
  foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', $cond)) {
    $e = $w.FindFirst('Descendants', $n)
    if ($e) {
      foreach ($pat in [System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern) {
        $o = $null
        if ($e.TryGetCurrentPattern($pat, [ref]$o)) { if ($o -is [System.Windows.Automation.InvokePattern]) { $o.Invoke() } else { $o.Select() }; return }
      }
    }
  }
  Write-Warning "nicht gefunden: $name"
}

$wasRunning = [bool](Get-Process Brim -ErrorAction SilentlyContinue | Where-Object Path -eq $installed)
Get-Process Brim -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$notesFile = Join-Path $data 'update-notes.json'
if ($Mode -eq 'notes') {
  @{ Version = '0.2.0'; Markdown = "- Tabs gleiten jetzt als Pille`n- Teilen per Code mit Proxy-Unterstützung`n- Updates im Hintergrund, Neustart erst in Ruhe" } | ConvertTo-Json | Set-Content $notesFile
} elseif (Test-Path $notesFile) { Remove-Item $notesFile }

$cover = Join-Path $data 'cover.png'
if ($Mode -eq 'idle') { Move-Item $cover "$cover.off" -Force }
try {
  $bd = Start-Process pwsh -ArgumentList '-NoProfile', '-File', (Join-Path $dir 'backdrop.ps1'), '-Seconds', '40' -WindowStyle Hidden -PassThru
  Start-Sleep -Seconds 2
  Demo; Start-Sleep -Seconds 5
  Shot "$Mode-compact"
  if ($Mode -ne 'idle') {
    Demo; Start-Sleep -Milliseconds 1800   # zweiter Start klappt auf
    Invoke-Name 'Ablage'; Start-Sleep -Milliseconds 1500
    if ($Mode -eq 'media') { Shot 'shelf' }
    Invoke-Name 'Jetzt'; Start-Sleep -Milliseconds 1500
    Shot "$Mode-expanded"
  }
}finally {
  Start-Sleep -Milliseconds 500; Get-Process Brim -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process -Force
  if ($bd -and -not $bd.HasExited) { Stop-Process $bd.Id -Force }
  if (Test-Path "$cover.off") { Move-Item "$cover.off" $cover -Force }
  if ($wasRunning) { Start-Process $installed }
}




