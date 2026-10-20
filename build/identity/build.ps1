<#
.SYNOPSIS
  Baut das Identitätspaket (Sparse Package) und signiert es mit einem selbst ausgestellten Zertifikat.

.DESCRIPTION
  Es gibt bewusst keine gekaufte Signatur (Entscheidung 20.10.2026). Jedes Release bekommt ein eigenes
  Zertifikat „CN=Brim“; Brim vertraut ihm auf Wunsch einmalig (Einstellungen → Kalender und
  Benachrichtigungen → Einrichten) und registriert dann das Paket. Ergebnis landet in <Ziel>\identity:
  Brim.Identity.msix und Brim.Identity.cer (nur der öffentliche Schlüssel).
#>
param([Parameter(Mandatory)] [string] $Destination)
$ErrorActionPreference = "Stop"

$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $sdk) { throw "makeappx.exe nicht gefunden (Windows SDK)" }
$makeappx = $sdk.FullName
$signtool = Join-Path $sdk.Directory "signtool.exe"

$work = Join-Path ([IO.Path]::GetTempPath()) ("brim-identity-" + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
Copy-Item (Join-Path $PSScriptRoot "AppxManifest.xml") $work
$out = Join-Path $Destination "identity"
New-Item -ItemType Directory -Force $out | Out-Null
$msix = Join-Path $out "Brim.Identity.msix"

# /nv: ohne Prüfung der Dateien – die liegen am externen Ort, nicht im Paket.
& $makeappx pack /d $work /p $msix /nv /o
if ($LASTEXITCODE) { throw "makeappx fehlgeschlagen" }

$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Brim" -FriendlyName "Brim Identität" `
    -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(10)
try {
    $password = [guid]::NewGuid().ToString()
    $pfx = Join-Path $work "identity.pfx"
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $out "Brim.Identity.cer") | Out-Null
    & $signtool sign /fd SHA256 /f $pfx /p $password $msix
    if ($LASTEXITCODE) { throw "signtool fehlgeschlagen" }
}
finally {
    # Der private Schlüssel verlässt den Build nie.
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work
}
Write-Host "Identitätspaket: $msix"
