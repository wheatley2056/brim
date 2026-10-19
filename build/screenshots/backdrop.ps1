# Neutrale Fläche hinter der Notch (oben mittig), ohne Fokus zu nehmen; schließt sich selbst.
param([int]$Seconds = 90, [int]$X = 480, [int]$W = 1600, [int]$H = 560)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Windows.Forms.Primitives, System.Drawing, System.Drawing.Primitives, System.Drawing.Common, System.ComponentModel.Primitives, System.Private.Windows.Core @"
using System.Drawing; using System.Drawing.Drawing2D; using System.Windows.Forms;
public class Backdrop : Form {
  public Backdrop() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; DoubleBuffered = true; }
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }
  protected override void OnPaint(PaintEventArgs e) {
    var r = ClientRectangle;
    using (var b = new LinearGradientBrush(r, ColorTranslator.FromHtml("#161A22"), ColorTranslator.FromHtml("#3A2A48"), 20f)) e.Graphics.FillRectangle(b, r);
  }
}
"@
Add-Type 'using System.Runtime.InteropServices; public static class Dpi9 { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[Dpi9]::SetProcessDPIAware() | Out-Null
$f = New-Object Backdrop
$f.Bounds = New-Object System.Drawing.Rectangle $X, 0, $W, $H
$t = New-Object System.Windows.Forms.Timer; $t.Interval = $Seconds * 1000; $t.add_Tick({ $f.Close() }); $t.Start()
[System.Windows.Forms.Application]::Run($f)

