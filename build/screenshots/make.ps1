# Erzeugt die README-Bilder in docs/assets neu aus der echten App (Vorführmodus BRIM_DEMO).
# Legt kurz eine neutrale Fläche oben mittig auf den Hauptmonitor; eine laufende installierte
# Fassung wird beendet und danach wieder gestartet. Die Maus wird nicht bewegt.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
dotnet build "$root\src\Brim" -c Debug -p:Platform=x64 --nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$PSScriptRoot\setup-demo.ps1" -Dir (Join-Path ([IO.Path]::GetTempPath()) 'brim-demo')
foreach ($mode in 'media', 'notes', 'idle') { & "$PSScriptRoot\capture.ps1" -Mode $mode }
& "$PSScriptRoot\compose.ps1" -Shots (Join-Path ([IO.Path]::GetTempPath()) 'brim-shots') -Out "$root\docs\assets"
