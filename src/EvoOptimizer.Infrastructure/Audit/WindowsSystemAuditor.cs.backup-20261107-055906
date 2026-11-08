using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EvoOptimizer.Core.Audit;

namespace EvoOptimizer.Infrastructure.Audit;

/// <summary>Auditor Windows de solo lectura. Usa CIM a través de PowerShell 5.1; no requiere elevación.</summary>
public sealed class WindowsSystemAuditor
{
    private const string Payload = @"
$ErrorActionPreference = 'Continue'
function Get-One($Class, $Props) {
  try { Get-CimInstance -ClassName $Class -ErrorAction Stop | Select-Object -First 1 -Property $Props }
  catch { $null }
}
function Get-Many($Class, $Props) {
  try { @(Get-CimInstance -ClassName $Class -ErrorAction Stop | Select-Object -Property $Props) }
  catch { @() }
}
function Get-ManyFiltered($Class, $Filter, $Props) {
  try { @(Get-CimInstance -ClassName $Class -Filter $Filter -ErrorAction Stop | Select-Object -Property $Props) }
  catch { @() }
}
$result = [ordered]@{
 computer = Get-One Win32_ComputerSystem 'Manufacturer','Model','TotalPhysicalMemory','SystemType','NumberOfLogicalProcessors'
 board = Get-One Win32_BaseBoard 'Manufacturer','Product','Version'
 bios = Get-One Win32_BIOS 'Manufacturer','SMBIOSBIOSVersion','ReleaseDate'
 cpu = Get-Many Win32_Processor 'Name','Manufacturer','NumberOfCores','NumberOfLogicalProcessors','MaxClockSpeed','CurrentClockSpeed','LoadPercentage','L2CacheSize','L3CacheSize','ProcessorId','SocketDesignation'
 gpu = Get-Many Win32_VideoController 'Name','AdapterCompatibility','DriverVersion','DriverDate','AdapterRAM','VideoProcessor','PNPDeviceID','CurrentHorizontalResolution','CurrentVerticalResolution','CurrentRefreshRate','Status'
 ram = Get-Many Win32_PhysicalMemory 'BankLabel','DeviceLocator','Manufacturer','PartNumber','Capacity','Speed','ConfiguredClockSpeed','DataWidth','TotalWidth','FormFactor','SMBIOSMemoryType','SerialNumber'
 memoryArray = Get-One Win32_PhysicalMemoryArray 'MemoryDevices','MaxCapacity','MemoryErrorCorrection'
 os = Get-One Win32_OperatingSystem 'Caption','Version','BuildNumber','OSArchitecture','LastBootUpTime'
 disks = Get-Many Win32_LogicalDisk 'DeviceID','VolumeName','FileSystem','Size','FreeSpace','DriveType'
 physicalDisks = Get-Many Win32_DiskDrive 'Index','DeviceID','Model','Manufacturer','MediaType','InterfaceType','Size','Status','FirmwareRevision','SerialNumber','BytesPerSector','Partitions','PNPDeviceID'
 partitions = Get-Many Win32_DiskPartition 'DeviceID','DiskIndex','Index','Type','Size','BootPartition'
 volumes = Get-Many Win32_Volume 'DriveLetter','Label','FileSystem','Capacity','FreeSpace','Status','HealthStatus'
 smart = @(Get-CimInstance -Namespace 'root/wmi' -ClassName 'MSStorageDriver_FailurePredictStatus' -ErrorAction SilentlyContinue | Select-Object InstanceName,Active,PredictFailure,Reason)
 storage = @()
 try { $storage = @(Get-PhysicalDisk -ErrorAction Stop | Select-Object DeviceId,FriendlyName,MediaType,BusType,HealthStatus,OperationalStatus,Size,SerialNumber) } catch { $storage = @() }
 reliability = @()
 try { $reliability = @(Get-PhysicalDisk -ErrorAction Stop | Get-StorageReliabilityCounter -ErrorAction Stop | Select-Object DeviceId,Wear,Temperature,PowerOnHours,ReadErrorsTotal,WriteErrorsTotal,ReadErrorsUncorrected,WriteErrorsUncorrected,StartStopCycleCount) } catch { $reliability = @() }
 trim = ((fsutil behavior query DisableDeleteNotify 2>$null) | Out-String)
 powerPlan = ((powercfg /getactivescheme 2>$null) | Out-String)
 services = Get-ManyFiltered Win32_Service ""StartMode='Auto'"" 'Name','DisplayName','State','StartMode','ProcessId'
 startup = Get-Many Win32_StartupCommand 'Name','Command','Location','User'
 network = Get-Many Win32_NetworkAdapter 'Name','NetEnabled','Speed','MACAddress'
 cs2 = @(Get-Process -Name cs2 -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,CPU,WorkingSet64,StartTime)
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$first = @(Get-Process -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,CPU,WorkingSet64)
Start-Sleep -Milliseconds 1000
$second = @(Get-Process -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,CPU,WorkingSet64)
$sw.Stop()
$result.processMeta = [ordered]@{ elapsedSeconds = [math]::Round($sw.Elapsed.TotalSeconds,3); logicalProcessors = [Environment]::ProcessorCount }
$result.processFirst = $first
$result.processSecond = $second
$result | ConvertTo-Json -Depth 8 -Compress
";

    public async Task<SystemAuditReport> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var shell = FindPowerShell();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Payload));
        using var process = new Process { StartInfo = new ProcessStartInfo {
            FileName = shell, Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        }};
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdoutTask;
        var error = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"Falló la auditoría CIM (código {process.ExitCode}): {error}");

        using var doc = JsonDocument.Parse(output);
        var facts = new List<AuditFact>();
        var warnings = new List<string>();
        AddObject(doc.RootElement, "Equipo", "computer", facts);
        AddObject(doc.RootElement, "Placa madre", "board", facts);
        AddObject(doc.RootElement, "BIOS", "bios", facts);
        AddObject(doc.RootElement, "CPU", "cpu", facts);
        AddArray(doc.RootElement, "GPU", "gpu", facts);
        AddArray(doc.RootElement, "RAM", "ram", facts);
        AddObject(doc.RootElement, "Arreglo de memoria", "memoryArray", facts);
        AddObject(doc.RootElement, "Windows", "os", facts);
        AddArray(doc.RootElement, "Discos", "disks", facts);
        AddArray(doc.RootElement, "Disco físico", "physicalDisks", facts);
        AddArray(doc.RootElement, "Particiones", "partitions", facts);
        AddArray(doc.RootElement, "Volúmenes", "volumes", facts);
        AddArray(doc.RootElement, "SMART predictivo", "smart", facts);
        AddArray(doc.RootElement, "Almacenamiento", "storage", facts);
        AddArray(doc.RootElement, "Contadores de confiabilidad", "reliability", facts);
        AddScalar(doc.RootElement, "TRIM", "Output", "trim", facts);
        AddScalar(doc.RootElement, "Plan de energía", "Output", "powerPlan", facts);
        AddArray(doc.RootElement, "Servicios", "services", facts);
        AddArray(doc.RootElement, "Inicio", "startup", facts);
        AddArray(doc.RootElement, "Red", "network", facts);
        AddArray(doc.RootElement, "CS2", "cs2", facts);
        AddArray(doc.RootElement, "Procesos muestra 1", "processFirst", facts);
        AddArray(doc.RootElement, "Procesos muestra 2", "processSecond", facts);
        AddObject(doc.RootElement, "Muestreo de procesos", "processMeta", facts);
        if (!facts.Any(f => f.Category == "CPU")) warnings.Add("No se pudo obtener información del procesador mediante CIM.");
        if (!facts.Any(f => f.Category == "GPU")) warnings.Add("No se pudo obtener información gráfica mediante CIM.");
        if (!facts.Any(f => f.Category == "Disco físico")) warnings.Add("No se pudo obtener información del disco físico.");
        if (!facts.Any(f => f.Category == "Particiones")) warnings.Add("No se pudo obtener información de las particiones.");
        if (!facts.Any(f => f.Category == "Volúmenes")) warnings.Add("No se pudo obtener información de los volúmenes.");
        if (!facts.Any(f => f.Category == "SMART predictivo")) warnings.Add("La información SMART no está disponible mediante root/wmi.");
        if (!facts.Any(f => f.Category == "Almacenamiento")) warnings.Add("Get-PhysicalDisk no devolvió discos; NVMe y HealthStatus quedan UNKNOWN.");
        if (!facts.Any(f => f.Category == "Contadores de confiabilidad")) warnings.Add("Get-StorageReliabilityCounter no devolvió contadores; el SMART completo queda UNKNOWN (HealthStatus no es SMART).");
        if (!facts.Any(f => f.Category == "TRIM")) warnings.Add("No se pudo leer DisableDeleteNotify; el estado de TRIM queda UNKNOWN.");
        if (!facts.Any(f => f.Category == "Plan de energía")) warnings.Add("No se pudo leer el plan de energía activo.");
        if (!facts.Any(f => f.Category == "Procesos muestra 2")) warnings.Add("No hubo segunda muestra de procesos; la CPU instantánea queda UNKNOWN.");
        return new SystemAuditReport(DateTimeOffset.Now, Environment.MachineName, facts, warnings);
    }

    public static async Task<(string JsonPath, string TextPath)> ExportAsync(SystemAuditReport report, string? directory = null, CancellationToken cancellationToken = default)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvoFpsOptimizer", "Audits");
        Directory.CreateDirectory(directory);
        var stamp = report.CapturedAt.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(directory, $"EVO_Audit_{stamp}.json");
        var textPath = Path.Combine(directory, $"EVO_Audit_{stamp}.txt");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var text = new StringBuilder().AppendLine("EVO FPS OPTIMIZER — AUDITORÍA DE SOLO LECTURA")
            .AppendLine($"Equipo: {report.ComputerName}").AppendLine($"Fecha: {report.CapturedAt:O}").AppendLine(new string('-', 72));
        foreach (var group in report.Facts.GroupBy(f => f.Category)) {
            text.AppendLine().AppendLine($"[{group.Key}]");
            foreach (var fact in group) text.AppendLine($"{fact.Name}: {fact.Value ?? "DESCONOCIDO"}  | {fact.Confidence} | {fact.EvidenceSource}");
        }
        foreach (var warning in report.Warnings) text.AppendLine().AppendLine($"ADVERTENCIA: {warning}");
        await File.WriteAllTextAsync(jsonPath, json, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(textPath, text.ToString(), new UTF8Encoding(false), cancellationToken);
        return (jsonPath, textPath);
    }

    private static string FindPowerShell() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    }.FirstOrDefault(File.Exists) ?? "powershell.exe";

    private static void AddObject(JsonElement root, string category, string property, List<AuditFact> facts)
    {
        if (!root.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.Object) return;
        foreach (var p in item.EnumerateObject()) AddFact(category, p.Name, p.Value, facts);
    }
    private static void AddArray(JsonElement root, string category, string property, List<AuditFact> facts)
    {
        if (!root.TryGetProperty(property, out var item)) return;
        if (item.ValueKind == JsonValueKind.Array) {
            var index = 0;
            foreach (var row in item.EnumerateArray()) {
                if (row.ValueKind == JsonValueKind.Object)
                    foreach (var p in row.EnumerateObject()) AddFact(category, $"[{index}] {p.Name}", p.Value, facts);
                index++;
            }
        } else if (item.ValueKind == JsonValueKind.Object) {
            foreach (var p in item.EnumerateObject()) AddFact(category, p.Name, p.Value, facts);
        }
    }
    private static void AddScalar(JsonElement root, string category, string name, string property, List<AuditFact> facts)
    {
        if (!root.TryGetProperty(property, out var item)) return;
        if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;

        string? value = item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : item.GetRawText();

        if (string.IsNullOrWhiteSpace(value)) return;

        facts.Add(new AuditFact(category, name, value, "PowerShell / CIM", "SYSTEM_REPORTED"));
    }
    private static void AddFact(string category, string name, JsonElement value, List<AuditFact> facts)
    {
        var rendered = value.ValueKind switch {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => value.ToString()
        };
        facts.Add(new AuditFact(category, name, rendered, "CIM / Win32_*", rendered is null ? "UNKNOWN" : "SYSTEM_REPORTED"));
    }
}
