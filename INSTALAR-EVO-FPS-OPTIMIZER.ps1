$ErrorActionPreference = 'Stop'
$archive = Join-Path $PSScriptRoot 'EVO-FPS-OPTIMIZER-RECONSTRUIDO.zip'
$destination = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Evo_optimizer'

if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    throw "No encuentro el ZIP junto al script: $archive"
}

if (Test-Path -LiteralPath $destination) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = "$destination.pre-evo-rebuild-$stamp"
    Move-Item -LiteralPath $destination -Destination $backup
    Write-Host "Carpeta anterior preservada en: $backup" -ForegroundColor Yellow
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force

$solution = Join-Path $destination 'EVO-FPS-OPTIMIZER.slnx'
if (-not (Test-Path -LiteralPath $solution)) {
    throw "La extracción terminó pero no aparece la solución: $solution"
}

Write-Host "Proyecto creado en: $destination" -ForegroundColor Green
Write-Host 'Verificación estática de archivos completada. Falta compilar en Windows.' -ForegroundColor Cyan
Write-Host "Para compilar: Set-Location `"$destination`"; dotnet build .\EVO-FPS-OPTIMIZER.slnx" -ForegroundColor Cyan
