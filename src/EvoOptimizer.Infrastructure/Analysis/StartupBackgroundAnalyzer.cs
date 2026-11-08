using System.Runtime.Versioning;
using System.Diagnostics;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Analysis;

public enum StartupImpact
{
    Low,
    Medium,
    High,
    Unknown
}

public sealed record StartupItem(
    string Name,
    string Command,
    string Source,
    StartupImpact Impact,
    string Recommendation);

public sealed record BackgroundProcess(
    int ProcessId,
    string Name,
    double WorkingSetMb,
    string? MainWindowTitle);

public sealed record StartupAnalysisReport(
    DateTimeOffset CapturedAt,
    IReadOnlyList<StartupItem> StartupItems,
    IReadOnlyList<BackgroundProcess> BackgroundProcesses,
    double TotalWorkingSetMb,
    IReadOnlyList<string> Warnings);

[SupportedOSPlatform("windows")]
public sealed class StartupBackgroundAnalyzer
{
    private static readonly string[] RunKeyPaths =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
    };

    public Task<StartupAnalysisReport> AnalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startup = new List<StartupItem>();
        var processes = new List<BackgroundProcess>();
        var warnings = new List<string>();

        ReadRunKeys(Registry.CurrentUser, "HKCU", startup, warnings);
        ReadRunKeys(Registry.LocalMachine, "HKLM", startup, warnings);

        ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Usuario",
            startup,
            warnings);

        ReadStartupFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            "Todos los usuarios",
            startup,
            warnings);

        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                processes.Add(new BackgroundProcess(
                    process.Id,
                    process.ProcessName,
                    process.WorkingSet64 / 1024d / 1024d,
                    process.MainWindowTitle));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
            {
                // Algunos procesos protegidos no exponen sus datos.
            }
            finally
            {
                process.Dispose();
            }
        }

        var orderedProcesses = processes
            .OrderByDescending(x => x.WorkingSetMb)
            .ToArray();

        return Task.FromResult(new StartupAnalysisReport(
            DateTimeOffset.UtcNow,
            startup
                .OrderByDescending(x => x.Impact)
                .ThenBy(x => x.Name)
                .ToArray(),
            orderedProcesses,
            orderedProcesses.Sum(x => x.WorkingSetMb),
            warnings));
    }

    private static void ReadRunKeys(
        RegistryKey root,
        string hive,
        ICollection<StartupItem> output,
        ICollection<string> warnings)
    {
        foreach (var path in RunKeyPaths)
        {
            try
            {
                using var key = root.OpenSubKey(path, writable: false);

                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                {
                    try
                    {
                        var command = Convert.ToString(
                            key.GetValue(
                                name,
                                null,
                                RegistryValueOptions.DoNotExpandEnvironmentNames))
                            ?? string.Empty;

                        output.Add(CreateItem(
                            name,
                            command,
                            $"{hive}\\{path}"));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            $"No se pudo leer {hive}\\{path}\\{name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"No se pudo acceder a {hive}\\{path}: {ex.Message}");
            }
        }
    }

    private static void ReadStartupFolder(
        string folder,
        string source,
        ICollection<StartupItem> output,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                output.Add(CreateItem(
                    Path.GetFileName(file),
                    file,
                    $"Carpeta de inicio: {source}"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"No se pudo leer la carpeta {folder}: {ex.Message}");
        }
    }

    private static StartupItem CreateItem(
        string name,
        string command,
        string source)
    {
        return new StartupItem(
            name,
            command,
            source,
            StartupImpact.Unknown,
            "Impacto de arranque no medido. El nombre del producto no demuestra consumo ni retraso; identifica su función antes de modificar el inicio.");
    }
}
