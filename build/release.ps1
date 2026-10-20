<#
.SYNOPSIS
  Baut ein Brim-Release mit Velopack: veröffentlichen, Changelog-Abschnitt ausschneiden, packen
  und (optional) als GitHub-Release hochladen. Wird von .github/workflows/release.yml genutzt,
  läuft aber genauso lokal.

.EXAMPLE
  ./build/release.ps1 -Version 0.1.1 -Output artifacts/releases
  ./build/release.ps1 -Version 0.1.1 -Upload -Token $env:GITHUB_TOKEN
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Output = "artifacts/releases",
    [string] $Runtime = "win-x64",
    [switch] $Upload,
    [string] $Token = $env:GITHUB_TOKEN,
    [string] $Repository = "https://github.com/wheatley2056/brim"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts/publish/$Version"
$notes = Join-Path $root "artifacts/notes-$Version.md"
$platform = if ($Runtime -like "*arm64") { "ARM64" } else { "x64" }

# 1. Veröffentlichen (eigenständig: Nutzer brauchen kein .NET)
dotnet publish (Join-Path $root "src/Brim/Brim.csproj") -c Release -r $Runtime --self-contained `
    -p:Platform=$platform -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw "dotnet publish fehlgeschlagen" }

# WinUI sucht seine Ressourcen mit Paketidentität unter resources.pri statt Brim.pri.
Copy-Item (Join-Path $publish "Brim.pri") (Join-Path $publish "resources.pri") -Force

# 1b. Identitätspaket für Kalender und Benachrichtigungen (selbst signiert, siehe build/identity)
# Noch nicht erprobt: Schlägt es fehl, erscheint das Release trotzdem, nur ohne Kalender und Benachrichtigungen.
try {
    & (Join-Path $PSScriptRoot "identity/build.ps1") -Destination $publish
}
catch {
    Write-Warning "Identitätspaket nicht gebaut: $_"
}

# 2. Changelog-Abschnitt dieser Version
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw
$pattern = '(?ms)^## ' + [regex]::Escape($Version) + '\s*$(.*?)(?=^## |\z)'
$match = [regex]::Match($changelog, $pattern)
if (-not $match.Success) { throw "CHANGELOG.md hat keinen Abschnitt '## $Version'" }
New-Item -ItemType Directory -Force (Split-Path $notes) | Out-Null
$match.Groups[1].Value.Trim() | Set-Content $notes -Encoding utf8

# 3. Packen; frühere Releases holen, damit Delta-Pakete entstehen (nur Änderungen laden)
New-Item -ItemType Directory -Force $Output | Out-Null
if ($Upload) {
    vpk download github --repoUrl $Repository --outputDir $Output --token $Token
}
# Paketname BrimNotch: Velopack installiert nach %LocalAppData%\BrimNotch und löscht diesen Ordner
# beim Deinstallieren – Brims Daten in %LocalAppData%\Brim bleiben so unberührt.
vpk pack --packId BrimNotch --packVersion $Version --packDir $publish --mainExe Brim.exe `
    --packTitle Brim --packAuthors "wheatley2056" --icon (Join-Path $root "src/Brim/Assets/Brim.ico") `
    --releaseNotes $notes --runtime $Runtime --outputDir $Output
if ($LASTEXITCODE) { throw "vpk pack fehlgeschlagen" }

# 4. Hochladen
if ($Upload) {
    vpk upload github --repoUrl $Repository --outputDir $Output --token $Token `
        --publish --releaseName "Brim $Version" --tag "v$Version" --merge
    if ($LASTEXITCODE) { throw "vpk upload fehlgeschlagen" }
}
