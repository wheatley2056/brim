<#
.SYNOPSIS
  Trägt ein Plugin-Release in catalog/plugins.json ein (neu oder neue Version): lädt das Zip, rechnet den
  SHA-256 nach, liest plugin.json aus dem Zip und übernimmt Kennung, Name, Version und Rechte. Beschreibung,
  Autor, Kategorien und minBrim eines bestehenden Eintrags bleiben, solange sie nicht übergeben werden.

.EXAMPLE
  ./build/catalog/Add-Plugin.ps1 -Repository wheatley2056/brim-swyxit -Tag v1.3.0
  ./build/catalog/Add-Plugin.ps1 -Repository someone/brim-weather -Tag v1.0.0 -Summary "Wetter in der Notch" -Author "Someone" -Categories Wetter
#>
param(
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Asset,
    [string] $Summary,
    [string] $Author,
    [string[]] $Categories,
    [string] $MinBrim,
    [string] $Token = $env:GITHUB_TOKEN
)
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$catalogFile = Join-Path $root "catalog/plugins.json"
$headers = @{ "User-Agent" = "brim-catalog"; "Accept" = "application/vnd.github+json" }
if ($Token) { $headers["Authorization"] = "Bearer $Token" }

# 1. Release und Zip finden
$release = Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$Repository/releases/tags/$Tag"
$zips = @($release.assets | Where-Object { $_.name -like "*.zip" -and (-not $Asset -or $_.name -eq $Asset) })
if ($zips.Count -ne 1) {
    throw "Release $Tag von $Repository hat $($zips.Count) passende Zips. Mit -Asset <Name> eines wählen."
}
$zip = $zips[0]

# 2. Laden und Prüfsumme rechnen (GitHub nennt sie auch selbst; beide müssen passen)
$temp = Join-Path ([IO.Path]::GetTempPath()) ("brim-catalog-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $temp | Out-Null
try {
    $file = Join-Path $temp $zip.name
    Invoke-WebRequest -Headers @{ "User-Agent" = "brim-catalog" } -Uri $zip.browser_download_url -OutFile $file
    $sha = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    if ($zip.digest -and $zip.digest -ne "sha256:$sha") {
        throw "Prüfsumme weicht von GitHub ab ($($zip.digest) statt sha256:$sha)"
    }

    # 3. plugin.json aus dem Zip (oben oder in genau einem Unterordner)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -match '^([^/\\]+[/\\])?plugin\.json$' } | Sort-Object { $_.FullName.Length } | Select-Object -First 1
        if (-not $entry) { throw "Im Zip fehlt plugin.json" }
        $reader = [IO.StreamReader]::new($entry.Open())
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        $reader.Dispose()
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    Remove-Item -Recurse -Force $temp
}

$version = $Tag.TrimStart("v")
if ($manifest.version -ne $version) {
    throw "plugin.json im Zip hat Version $($manifest.version), der Tag ist $Tag"
}

# 4. Eintrag zusammenstellen und in den Katalog schreiben
$catalog = Get-Content $catalogFile -Raw | ConvertFrom-Json
$old = $catalog.plugins | Where-Object { $_.id -eq $manifest.id }
function Pick($new, $existing, $fallback) { if ($new) { $new } elseif ($existing) { $existing } else { $fallback } }
$permissions = @()
if ($manifest.permissions) { $permissions = @($manifest.permissions) }
$item = [ordered]@{
    id          = $manifest.id
    name        = $manifest.name
    summary     = Pick $Summary $old.summary ""
    author      = Pick $Author $old.author ""
    repository  = "https://github.com/$Repository"
    categories  = @(Pick $Categories $old.categories @())
    permissions = $permissions
    version     = $version
    download    = $zip.browser_download_url
    sha256      = $sha
    size        = [long]$zip.size
}
$min = Pick $MinBrim $old.minBrim $null
if ($min) { $item["minBrim"] = $min }

$plugins = @($catalog.plugins | Where-Object { $_.id -ne $manifest.id }) + [pscustomobject]$item | Sort-Object id
$result = [ordered]@{ schema = 1; plugins = @($plugins) }
($result | ConvertTo-Json -Depth 5) + "`n" | Set-Content -NoNewline -Encoding utf8NoBOM $catalogFile
Write-Host "$($manifest.name) $version eingetragen ($sha)"
