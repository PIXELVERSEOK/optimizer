$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$infra = Join-Path $root 'src\EvoOptimizer.Infrastructure\bin\Debug\net10.0\EvoOptimizer.Infrastructure.dll'
$core = Join-Path $root 'src\EvoOptimizer.Core\bin\Debug\net10.0\EvoOptimizer.Core.dll'
if (-not (Test-Path $infra) -or -not (Test-Path $core)) { throw 'Primero compilá EVO-FPS-OPTIMIZER.slnx.' }
Write-Host 'EVO SYSTEM AUDITOR — lectura solamente' -ForegroundColor Cyan
Write-Host 'No modifica registro, servicios, energía ni archivos de configuración.' -ForegroundColor Yellow
# El modo portable usa el mismo conjunto CIM para exportar el informe sin cargar ensamblados .NET.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $env:LOCALAPPDATA 'EvoFpsOptimizer\Audits'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$report = [ordered]@{
 CapturedAt = (Get-Date).ToString('o'); ComputerName = $env:COMPUTERNAME
 Computer = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,SystemType,NumberOfLogicalProcessors,TotalPhysicalMemory
 Motherboard = Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product,Version
 BIOS = Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion,ReleaseDate
 CPU = Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed
 GPU = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,DriverDate,AdapterRAM,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
 RAM = @(Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel,Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed,DeviceLocator)
 OS = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime
 Disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID,FileSystem,Size,FreeSpace)
 Network = @(Get-CimInstance Win32_NetworkAdapter | Where-Object NetEnabled | Select-Object Name,Speed,MACAddress)
 CS2 = @(Get-Process cs2 -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,CPU,WorkingSet64)
}
$jsonPath = Join-Path $outDir "EVO_Audit_$stamp.json"
$txtPath = Join-Path $outDir "EVO_Audit_$stamp.txt"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('EVO FPS OPTIMIZER — AUDITORÍA DE SOLO LECTURA')
$lines.Add("Equipo: $env:COMPUTERNAME | Fecha: $($report.CapturedAt)")
foreach ($section in $report.Keys) { if ($section -notin @('CapturedAt','ComputerName')) { $lines.Add("`n[$section]"); $lines.Add(($report[$section] | Format-List | Out-String).Trim()) } }
$lines | Set-Content -LiteralPath $txtPath -Encoding UTF8
Write-Host "Auditoría JSON: $jsonPath" -ForegroundColor Green
Write-Host "Auditoría TXT:  $txtPath" -ForegroundColor Green
Start-Process notepad.exe -ArgumentList "`"$txtPath`""
