using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace EvoOptimizer.Infrastructure.Common;

/// <summary>
/// Ejecuta un script de PowerShell devolviendo su stdout. Usa siempre la shell
/// de Windows PowerShell 5.1 (la compatible con CIM/Get-AppxPackage que ya usa
/// el auditor de EVO) y pasa el script codificado en base64 para evitar
/// problemas de comillas.
/// </summary>
internal static class PowerShellScriptRunner
{
    internal sealed record ScriptResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0 && !string.IsNullOrWhiteSpace(StdOut);
    }

    public static async Task<ScriptResult> RunAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FindPowerShell(),
                Arguments =
                    $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new ScriptResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string FindPowerShell() => new[]
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe"),
        "powershell.exe"
    }.First(File.Exists);
}

/// <summary>
/// Ejecuta un comando elevado mostrando una sola solicitud de UAC. Se usa para
/// los tweaks HKLM: si el usuario cancela, el error 1223 se traduce a
/// "no autorizado" y EVO no escribe nada.
/// </summary>
internal static class ElevatedProcessRunner
{
    /// <summary>1223 = ERROR_CANCELLED (UAC denegado o cancelado).</summary>
    private const int ErrorCancelled = 1223;

    public sealed record ElevatedResult(bool Ran, bool Cancelled, int? ExitCode, string? Error)
    {
        public bool Succeeded => Ran && ExitCode == 0;
    }

    public static async Task<ElevatedResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("No se pudo iniciar el proceso elevado.");

            await process.WaitForExitAsync(cancellationToken);

            return new ElevatedResult(true, false, process.ExitCode, null);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedResult(false, true, null, "Autorización UAC cancelada.");
        }
        catch (Win32Exception exception)
        {
            return new ElevatedResult(false, false, null, exception.Message);
        }
    }
}
