<#
.SYNOPSIS
  Erzeugt die winget-Manifeste (Schema 1.6) für ein Release aus dem Velopack-Setup.

.DESCRIPTION
  Ergebnis in <Output>\winget\manifests\w\wheatley2056\Brim\<Version>\. Diesen Ordner als Pull Request bei
  microsoft/winget-pkgs einreichen (beim ersten Mal von Hand, danach mit wingetcreate, siehe build/winget/README.md).
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output,
    [string] $Repository = "https://github.com/wheatley2056/brim"
)
$ErrorActionPreference = "Stop"

$id = "wheatley2056.Brim"
$setupName = "BrimNotch-win-Setup.exe"
$setup = Join-Path $Output $setupName
if (-not (Test-Path $setup)) { throw "$setupName fehlt in $Output" }
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash
$url = "$Repository/releases/download/v$Version/$setupName"
$dir = Join-Path $Output "winget/manifests/w/wheatley2056/Brim/$Version"
New-Item -ItemType Directory -Force $dir | Out-Null

$schema = "https://aka.ms/winget-manifest"
@"
# yaml-language-server: `$schema=$schema.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: de-DE
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $dir "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=$schema.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
  - interactive
  - silent
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
UpgradeBehavior: install
MinimumOSVersion: 10.0.19041.0
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $dir "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=$schema.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: de-DE
Publisher: wheatley2056
PublisherUrl: https://github.com/wheatley2056
PackageName: Brim
PackageUrl: $Repository
License: GPL-3.0
LicenseUrl: $Repository/blob/main/LICENSE
ShortDescription: Eine Notch für Windows – Musik, Ablage, Teilen und System am oberen Bildschirmrand.
Description: Brim setzt eine lebendige Notch an den oberen Bildschirmrand, wie die Dynamic Island. Sie zeigt Musik, Lautstärke, Timer, Akku und Systemwerte, nimmt Dateien auf, teilt sie mit anderen Brims und macht Platz, sobald ein Fenster ihn braucht. Erweiterbar über Plugins.
Moniker: brim
Tags:
  - notch
  - dynamic-island
  - media
  - shelf
  - widgets
ReleaseNotesUrl: $Repository/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $dir "$id.locale.de-DE.yaml") -Encoding utf8

Write-Host "winget-Manifeste: $dir"
