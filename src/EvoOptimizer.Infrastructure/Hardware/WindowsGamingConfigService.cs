using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using EvoOptimizer.Core.Optimization;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Hardware;

public sealed record WindowsGamingConfig(
    bool GameDvrEnabled,
    bool GameModeEnabled,
    bool HagsEnabled,
    string PowerPlanName,
    IReadOnlyList<string> DiagnosticNotes,
    bool? IsOnAcPower = null,
    bool HasHighPerformancePlan = false,
    string? ActivePowerPlanGuid = null,
    uint? ProcessorEppAc = null,
    uint? ProcessorEppDc = null,
    IReadOnlyList<string>? PowerDiagnostics = null);

[SupportedOSPlatform("windows")]
public sealed class WindowsGamingConfigService
{
    private const string GameDvrPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    public WindowsGamingConfig ReadConfiguration()
    {
        bool gameDvr = ReadDword(Registry.CurrentUser, GameDvrPath, "AppCaptureEnabled") == 1;
        bool gameMode = ReadDword(Registry.CurrentUser, GameBarPath, "AllowAutoGameMode") != 0; // Default en Win11 suele ser 1
        bool hags = ReadDword(Registry.LocalMachine, GraphicsDriversPath, "HwSchMode") == 2;
        string powerPlan = ReadActivePowerPlan();
        WindowsPowerTelemetry powerTelemetry = ReadPowerTelemetrySafe();
        bool hasHighPerformancePlan = FindPowerPlanGuid(
            "Alto rendimiento",
            "High performance") is not null;

        var notes = new List<string>();

        if (gameDvr)
            notes.Add("Captura de Windows (GameDVR) activa: puede consumir hilos de CPU y memoria.");

        if (!gameMode)
            notes.Add("Modo Juego desactivado en Windows.");

        if (!hags)
            notes.Add("HAGS (Programación de GPU acelerada por hardware) desactivada.");

        if (powerPlan.Contains("Equilibrado", StringComparison.OrdinalIgnoreCase) ||
            powerPlan.Contains("Balanced", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Plan de energía Equilibrado detectado. El nombre del plan por sí solo no demuestra una limitación de frecuencia o FPS.");
        }

        notes.AddRange(powerTelemetry.Warnings.Select(warning => $"Energía: {warning}"));

        return new WindowsGamingConfig(
            GameDvrEnabled: gameDvr,
            GameModeEnabled: gameMode,
            HagsEnabled: hags,
            PowerPlanName: powerPlan,
            DiagnosticNotes: notes,
            IsOnAcPower: powerTelemetry.IsOnAcPower,
            HasHighPerformancePlan: hasHighPerformancePlan,
            ActivePowerPlanGuid: powerTelemetry.ActiveSchemeGuid,
            ProcessorEppAc: powerTelemetry.ProcessorEppAc,
            ProcessorEppDc: powerTelemetry.ProcessorEppDc,
            PowerDiagnostics: powerTelemetry.Warnings);
    }

    /// <summary>
    /// La telemetría de energía es opcional y de solo lectura. Si el sistema no
    /// expone algún dato (o un P/Invoke falla), se degrada a UNKNOWN con una
    /// advertencia en vez de propagar la excepción al constructor de la app.
    /// </summary>
    private static WindowsPowerTelemetry ReadPowerTelemetrySafe()
    {
        try
        {
            return new WindowsPowerTelemetryService().ReadCurrent();
        }
        catch (Exception ex)
        {
            return new WindowsPowerTelemetry(
                IsOnAcPower: null,
                ActiveSchemeGuid: null,
                ProcessorEppAc: null,
                ProcessorEppDc: null,
                Warnings: new[] { $"Telemetría de energía no disponible: {ex.Message}" });
        }
    }

    public IReadOnlyList<OptimizationSignal> GenerateSignals(WindowsGamingConfig config)
    {
        var signals = new List<OptimizationSignal>();

        if (config.GameDvrEnabled)
        {
            signals.Add(new OptimizationSignal(
                "GAMEDVR_CAPTURE_ENABLED",
                true,
                0.95,
                "GameDVR (AppCaptureEnabled=1) detectado en el registro de Windows."));
        }

        if (!config.GameModeEnabled)
        {
            signals.Add(new OptimizationSignal(
                "GAME_MODE_DISABLED",
                true,
                0.90,
                "El Modo Juego de Windows se encuentra deshabilitado (AllowAutoGameMode=0)."));
        }

        return signals;
    }

    /// <summary>
    /// GUID del plan de energía activo. Se usa para guardar y restaurar el estado
    /// original antes de cambiar de plan.
    /// </summary>
    public string? ReadActivePowerPlanGuid()
    {
        string? output = RunPowerCfg("/getactivescheme");

        return output is null ? null : ExtractGuid(output);
    }

    /// <summary>
    /// Busca un plan de energía instalado por coincidencia de nombre. Devuelve
    /// null si Windows no lo expone (no se inventan GUIDs).
    /// </summary>
    public string? FindPowerPlanGuid(params string[] nameFragments)
    {
        if (nameFragments.Length == 0) return null;

        string? output = RunPowerCfg("/list");

        if (output is null) return null;

        foreach (string line in output.Split('\n'))
        {
            string? guid = ExtractGuid(line);

            if (guid is null) continue;

            if (nameFragments.Any(fragment =>
                    line.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                return guid;
            }
        }

        return null;
    }

    /// <summary>
    /// Activa un plan de energía por GUID verificado (obtenido de powercfg /list).
    /// Lanza si powercfg no confirma el cambio; el llamador guarda el GUID
    /// original antes de invocarlo para poder restaurarlo.
    /// </summary>
    public void SetActivePowerPlan(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid) || ExtractGuid(guid) is null)
            throw new ArgumentException("El GUID del plan de energía no es válido.", nameof(guid));

        string? output = RunPowerCfg($"/setactive {guid}");

        if (output is null)
            throw new InvalidOperationException("No se pudo ejecutar powercfg para cambiar el plan.");

        string? active = ReadActivePowerPlanGuid();

        if (!string.Equals(active, guid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"powercfg no confirmó el plan solicitado ({guid}); sigue activo {active ?? "desconocido"}.");
    }

    /// <summary>
    /// Un plan Equilibrado/Balanced es el único caso donde el cambio a Alto
    /// rendimiento aporta un efecto técnico conocido.
    /// </summary>
    public static bool IsBalancedPowerPlanName(string? name) =>
        name is not null &&
        (name.Contains("Equilibrado", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Balanced", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? ExtractGuid(string text)
    {
        // El texto de powercfg está localizado ("GUID del esquema de energía",
        // "Power Scheme GUID"), así que se busca el patrón GUID directamente.
        var match = GuidPattern.Match(text);

        return match.Success ? match.Value : null;
    }

    private static string? RunPowerCfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process is null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);

            return output;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadDword(RegistryKey root, string subKeyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            if (key?.GetValue(valueName) is int value)
                return value;
        }
        catch
        {
            // Acceso restringido o clave ausente
        }

        return null;
    }

    private static string ReadActivePowerPlan()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1000);

                int openParen = output.IndexOf('(');
                int closeParen = output.IndexOf(')');
                if (openParen >= 0 && closeParen > openParen)
                {
                    return output.Substring(openParen + 1, closeParen - openParen - 1).Trim();
                }

                if (!string.IsNullOrWhiteSpace(output))
                    return output.Trim();
            }
        }
        catch
        {
            // Fallback si powercfg falla
        }

        return "Esquema de energía de Windows";
    }
}
