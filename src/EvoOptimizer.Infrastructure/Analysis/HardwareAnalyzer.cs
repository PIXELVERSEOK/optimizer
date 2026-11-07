using System.Management;
using System.Runtime.Versioning;

namespace EvoOptimizer.Infrastructure.Analysis;

public sealed record HardwareComponent(
    string Category,
    string Name,
    string Manufacturer,
    string Model,
    string DriverVersion,
    string DriverDate,
    string Status,
    IReadOnlyDictionary<string, string?> Properties);

public sealed record HardwareAnalysisReport(
    DateTimeOffset CapturedAt,
    IReadOnlyList<HardwareComponent> Components,
    IReadOnlyList<string> Warnings);

[SupportedOSPlatform("windows")]
public sealed class HardwareAnalyzer
{
    public Task<HardwareAnalysisReport> AnalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var components = new List<HardwareComponent>();
        var warnings = new List<string>();

        ReadClass(
            "Win32_Processor",
            "CPU",
            new[] { "Name", "Manufacturer", "NumberOfCores",
                "NumberOfLogicalProcessors", "MaxClockSpeed",
                "CurrentClockSpeed", "LoadPercentage", "L2CacheSize",
                "L3CacheSize" },
            components, warnings, cancellationToken);

        ReadClass(
            "Win32_PhysicalMemory",
            "RAM",
            new[] { "Manufacturer", "PartNumber", "Capacity",
                "Speed", "ConfiguredClockSpeed", "DeviceLocator",
                "FormFactor", "SMBIOSMemoryType" },
            components, warnings, cancellationToken);

        ReadClass(
            "Win32_VideoController",
            "GPU",
            new[] { "Name", "AdapterCompatibility", "DriverVersion",
                "DriverDate", "AdapterRAM", "VideoModeDescription",
                "CurrentRefreshRate", "Status" },
            components, warnings, cancellationToken);

        ReadClass(
            "Win32_DiskDrive",
            "Disco",
            new[] { "Model", "Manufacturer", "MediaType",
                "InterfaceType", "Size", "Status", "FirmwareRevision",
                "BytesPerSector", "Partitions" },
            components, warnings, cancellationToken);

        ReadClass(
            "Win32_PnPSignedDriver",
            "Controlador",
            new[] { "DeviceName", "Manufacturer", "DriverVersion",
                "DriverDate", "InfName", "IsSigned", "DeviceClass",
                "DeviceID" },
            components, warnings, cancellationToken);

        return Task.FromResult(new HardwareAnalysisReport(
            DateTimeOffset.UtcNow,
            components,
            warnings));
    }

    private static void ReadClass(
        string className,
        string category,
        string[] properties,
        ICollection<HardwareComponent> output,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT {string.Join(",", properties)} FROM {className}");

            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using (item)
                {
                    var values = new Dictionary<string, string?>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var property in properties)
                    {
                        try
                        {
                            var raw = item[property];

                            values[property] = raw switch
                            {
                                null => null,
                                Array array => string.Join(", ", array.Cast<object>()),
                                DateTime date => date.ToString("O"),
                                _ => Convert.ToString(
                                    raw,
                                    System.Globalization.CultureInfo.InvariantCulture)
                            };
                        }
                        catch
                        {
                            values[property] = null;
                        }
                    }

                    string Get(string key) =>
                        values.TryGetValue(key, out var value)
                            ? value ?? string.Empty
                            : string.Empty;

                    var name = Get("Name");
                    if (string.IsNullOrWhiteSpace(name))
                        name = Get("Model");

                    if (string.IsNullOrWhiteSpace(name))
                        name = Get("DeviceName");

                    output.Add(new HardwareComponent(
                        category,
                        name,
                        Get("Manufacturer"),
                        Get("Model"),
                        Get("DriverVersion"),
                        Get("DriverDate"),
                        Get("Status"),
                        values));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"No se pudo consultar {className}: {ex.Message}");
        }
    }
}
