using System.Security.Cryptography;
using System.Text;

namespace EvoOptimizer.Core.Hardware;

public sealed record HardwareFingerprintInput(
    string CpuModel,
    string GpuModel,
    string MotherboardModel,
    long TotalMemoryBytes,
    string MemoryType,
    int MemorySpeedMhz,
    string StorageConfiguration);

public sealed record EnvironmentFingerprintInput(
    string GpuDriverVersion,
    string OperatingSystem,
    string OperatingSystemBuild,
    string DisplayConfiguration,
    string PowerPlan);

public sealed record HardwareFingerprint(
    string HardwareId,
    string EnvironmentId,
    string HardwareDescription,
    string EnvironmentDescription,
    DateTimeOffset CreatedAt);

public sealed class HardwareFingerprintBuilder
{
    public HardwareFingerprint Build(
        HardwareFingerprintInput hardware,
        EnvironmentFingerprintInput environment)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(environment);

        ValidateHardware(hardware);

        var hardwareParts = new[]
        {
            Normalize(hardware.CpuModel),
            Normalize(hardware.GpuModel),
            Normalize(hardware.MotherboardModel),
            hardware.TotalMemoryBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Normalize(hardware.MemoryType),
            hardware.MemorySpeedMhz.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Normalize(hardware.StorageConfiguration)
        };

        var environmentParts = new[]
        {
            Normalize(environment.GpuDriverVersion),
            Normalize(environment.OperatingSystem),
            Normalize(environment.OperatingSystemBuild),
            Normalize(environment.DisplayConfiguration),
            Normalize(environment.PowerPlan)
        };

        var hardwareId = CreateHash(hardwareParts);
        var environmentId = CreateHash(
            hardwareParts.Concat(environmentParts));

        return new HardwareFingerprint(
            hardwareId,
            environmentId,
            string.Join(" | ", hardwareParts),
            string.Join(" | ", environmentParts),
            DateTimeOffset.UtcNow);
    }

    public bool IsSameHardware(
        HardwareFingerprint first,
        HardwareFingerprint second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return string.Equals(
            first.HardwareId,
            second.HardwareId,
            StringComparison.Ordinal);
    }

    public bool IsSameEnvironment(
        HardwareFingerprint first,
        HardwareFingerprint second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return IsSameHardware(first, second) &&
               string.Equals(
                   first.EnvironmentId,
                   second.EnvironmentId,
                   StringComparison.Ordinal);
    }

    private static void ValidateHardware(
        HardwareFingerprintInput input)
    {
        if (string.IsNullOrWhiteSpace(input.CpuModel))
            throw new ArgumentException("Falta el modelo de CPU.");

        if (string.IsNullOrWhiteSpace(input.GpuModel))
            throw new ArgumentException("Falta el modelo de GPU.");

        if (input.TotalMemoryBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(input.TotalMemoryBytes));

        if (input.MemorySpeedMhz < 0)
            throw new ArgumentOutOfRangeException(
                nameof(input.MemorySpeedMhz));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNKNOWN";

        return string.Join(
            " ",
            value.Trim()
                .ToUpperInvariant()
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
    }

    private static string CreateHash(IEnumerable<string> parts)
    {
        var canonical = string.Join("\n", parts);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);

        return Convert.ToHexString(hash);
    }
}
