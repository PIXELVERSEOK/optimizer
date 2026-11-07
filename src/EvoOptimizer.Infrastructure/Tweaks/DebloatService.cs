using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using EvoOptimizer.Core.Tweaks;
using EvoOptimizer.Infrastructure.Common;

namespace EvoOptimizer.Infrastructure.Tweaks;

/// <summary>
/// Escáner y ejecutor de debloat. Lee el usuario real con Get-AppxPackage,
/// ofrece quitar sólo lo catalogado, nunca un paquete protegido, y guarda
/// PackageFullName + InstallLocation en el diario para poder volver a
/// registrar la app con Add-AppxPackage -Register.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DebloatService
{
    private readonly DebloatChangeJournal _journal;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DebloatService(DebloatChangeJournal? journal = null)
    {
        _journal = journal ?? new DebloatChangeJournal(DebloatChangeJournal.DefaultPath);
    }

    public sealed record DebloatOperationResult(bool Success, string Message, DebloatAppStatus? Status);

    public sealed record DebloatScanResult(
        IReadOnlyList<InstalledUwpApp> Apps,
        IReadOnlyList<DebloatAppStatus> Statuses,
        DateTimeOffset? ScannedAt,
        string? Error)
    {
        public int RemovableCount => Statuses.Count(status => status.CanRemove);
        public int InstalledCount => Statuses.Count(status => status.IsInstalled);
        public bool HasData => ScannedAt is not null;
    }

    public IReadOnlyList<DebloatAppDefinition> Definitions => DebloatCatalog.RemovableApps;

    public DebloatChangeJournal Journal => _journal;

    /// <summary>
    /// Escanea los paquetes del usuario actual. Sin PowerShell o sin permisos el
    /// resultado devuelve <see cref="DebloatState.Unknown"/>, nunca una lista vacía
    /// interpretada como "no hay bloatware".
    /// </summary>
    public Task<DebloatScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
        ScanInternalAsync(cancellationToken);

    public async Task<DebloatOperationResult> RemoveAsync(
        string definitionId,
        bool userAuthorized,
        CancellationToken cancellationToken = default)
    {
        DebloatAppDefinition definition = FindDefinition(definitionId);

        if (!userAuthorized)
        {
            return new DebloatOperationResult(
                false,
                $"Sin autorización del usuario: «{definition.DisplayName}» no se quitó.",
                null);
        }

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            DebloatScanResult scan = await ScanInternalAsync(cancellationToken);

            if (scan.ScannedAt is null)
            {
                return new DebloatOperationResult(
                    false,
                    scan.Error ?? "Escaneo imposible: EVO no quita apps sin verificar que están instaladas.",
                    null);
            }

            InstalledUwpApp? app = scan.Apps.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageName, definition.NamePrefix, StringComparison.OrdinalIgnoreCase));

            if (app is null)
            {
                return new DebloatOperationResult(
                    false,
                    $"«{definition.DisplayName}» no está instalada para este usuario. No se hizo nada.",
                    DebloatCatalog.Evaluate(definition, null, hasActiveChange: false, scan.ScannedAt));
            }

            if (DebloatCatalog.IsProtected(app.PackageName))
            {
                return new DebloatOperationResult(
                    false,
                    $"«{app.PackageName}» está en la lista protegida de EVO (sistema, seguridad o runtime). " +
                    "No se quita.",
                    DebloatCatalog.Evaluate(definition, app, hasActiveChange: false, scan.ScannedAt));
            }

            var change = new DebloatChange(
                Guid.NewGuid().ToString("N"),
                definition.Id,
                app.PackageName,
                app.PackageFullName,
                app.InstallLocation,
                app.Version,
                DebloatChangeStatus.Pending,
                DateTimeOffset.Now,
                CompletedAt: null,
                Error: null);

            await _journal.AppendAsync(change, cancellationToken);

            PowerShellScriptRunner.ScriptResult remove = await PowerShellScriptRunner.RunAsync(
                BuildRemoveScript(app.PackageFullName),
                cancellationToken);

            bool reportedOk = remove.StdOut.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);

            DebloatScanResult verify = await ScanInternalAsync(cancellationToken);

            if (verify.ScannedAt is null)
            {
                string detail = $"quitar requiere verificación y el escaneo posterior falló " +
                    $"({verify.Error ?? "Get-AppxPackage sin salida"}).";

                await _journal.UpdateAsync(
                    change with
                    {
                        Status = DebloatChangeStatus.Failed,
                        CompletedAt = DateTimeOffset.Now,
                        Error = detail
                    },
                    cancellationToken);

                return new DebloatOperationResult(
                    false,
                    $"No se pudo verificar «{definition.DisplayName}»: {detail}",
                    null);
            }

            bool stillInstalled = verify.Apps.Any(candidate =>
                string.Equals(candidate.PackageFullName, app.PackageFullName, StringComparison.Ordinal));

            if (stillInstalled)
            {
                string detail = reportedOk
                    ? "Remove-AppxPackage reportó éxito pero el paquete sigue presente."
                    : FirstLine(remove.StdOut.Length > 0 ? remove.StdOut : remove.StdErr);

                await _journal.UpdateAsync(
                    change with
                    {
                        Status = DebloatChangeStatus.Failed,
                        CompletedAt = DateTimeOffset.Now,
                        Error = detail
                    },
                    cancellationToken);

                return new DebloatOperationResult(
                    false,
                    $"No se pudo quitar «{definition.DisplayName}»: {detail}",
                    DebloatCatalog.Evaluate(definition, app, hasActiveChange: false, verify.ScannedAt));
            }

            await _journal.UpdateAsync(
                change with
                {
                    Status = DebloatChangeStatus.Removed,
                    CompletedAt = DateTimeOffset.Now
                },
                cancellationToken);

            return new DebloatOperationResult(
                true,
                $"«{definition.DisplayName}» quitada de este usuario. " +
                (app.CanRestore
                    ? "Se puede volver a registrar desde su AppxManifest."
                    : "Sin InstallLocation conocida: reinstalala desde la Microsoft Store."),
                new DebloatAppStatus(
                    definition,
                    app,
                    DebloatState.Removed,
                    $"Quitada por EVO ({app.PackageFullName}).",
                    DateTimeOffset.Now));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<DebloatOperationResult> RestoreAsync(
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        DebloatAppDefinition definition = FindDefinition(definitionId);

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            IReadOnlyList<DebloatChange> active = await _journal.GetActiveAsync(cancellationToken);

            DebloatChange? change = active
                .Where(entry => string.Equals(entry.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.CreatedAt)
                .FirstOrDefault();

            if (change is null)
            {
                return new DebloatOperationResult(
                    false,
                    $"EVO no quitó «{definition.DisplayName}» en este equipo; nada que restaurar.",
                    null);
            }

            if (string.IsNullOrWhiteSpace(change.InstallLocation))
            {
                return new DebloatOperationResult(
                    false,
                    $"No se registró InstallLocation para «{definition.DisplayName}»; " +
                    "restaurala desde la Microsoft Store.",
                    null);
            }

            string manifest = Path.Combine(change.InstallLocation!, "AppxManifest.xml");

            PowerShellScriptRunner.ScriptResult restore = await PowerShellScriptRunner.RunAsync(
                BuildRestoreScript(manifest),
                cancellationToken);

            bool reportedOk = restore.StdOut.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);

            DebloatScanResult verify = await ScanInternalAsync(cancellationToken);

            bool back = verify.ScannedAt is not null && verify.Apps.Any(candidate =>
                string.Equals(candidate.PackageName, change.PackageName, StringComparison.OrdinalIgnoreCase));

            if (!back)
            {
                string detail = reportedOk
                    ? "Add-AppxPackage reportó éxito pero el paquete sigue sin aparecer."
                    : FirstLine(restore.StdOut.Length > 0 ? restore.StdOut : restore.StdErr);

                return new DebloatOperationResult(
                    false,
                    $"No se pudo restaurar «{definition.DisplayName}»: {detail}",
                    null);
            }

            await _journal.UpdateAsync(
                change with
                {
                    Status = DebloatChangeStatus.Restored,
                    CompletedAt = DateTimeOffset.Now,
                    Error = null
                },
                cancellationToken);

            InstalledUwpApp? app = verify.Apps.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageName, change.PackageName, StringComparison.OrdinalIgnoreCase));

            return new DebloatOperationResult(
                true,
                $"«{definition.DisplayName}» vuelve a estar registrada para este usuario.",
                DebloatCatalog.Evaluate(definition, app, hasActiveChange: false, verify.ScannedAt));
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Escaneo real usado por las operaciones (mismo código que el público).</summary>
    private async Task<DebloatScanResult> ScanInternalAsync(CancellationToken cancellationToken)
    {
        PowerShellScriptRunner.ScriptResult scan = await PowerShellScriptRunner.RunAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            $apps = @(Get-AppxPackage |
                Where-Object { $_.PackageFullName } |
                Select-Object Name, PackageFullName, InstallLocation, Version, Publisher)
            if ($apps.Count -eq 0) { '[]' } else { $apps | ConvertTo-Json -Compress -Depth 3 }
            """,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(scan.StdOut))
        {
            return new DebloatScanResult(
                Array.Empty<InstalledUwpApp>(),
                DebloatCatalog.Match(Array.Empty<InstalledUwpApp>(), null),
                ScannedAt: null,
                $"No se pudo ejecutar Get-AppxPackage ({SummarizeError(scan)}).");
        }

        List<InstalledUwpApp> apps = ParseApps(scan.StdOut);
        DateTimeOffset now = DateTimeOffset.Now;

        IReadOnlyList<DebloatChange> active = await _journal.GetActiveAsync(cancellationToken);

        var statuses = new List<DebloatAppStatus>(DebloatCatalog.RemovableApps.Count);

        foreach (DebloatAppDefinition definition in DebloatCatalog.RemovableApps)
        {
            DebloatChange? change = active.FirstOrDefault(entry =>
                string.Equals(entry.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase));

            InstalledUwpApp? found = apps.FirstOrDefault(app =>
                string.Equals(app.PackageName, definition.NamePrefix, StringComparison.OrdinalIgnoreCase));

            if (found is null && change is not null)
            {
                found = new InstalledUwpApp(
                    change.PackageName,
                    change.PackageFullName,
                    change.InstallLocation,
                    change.Version,
                    Publisher: null);
            }

            statuses.Add(DebloatCatalog.Evaluate(definition, found, change is not null, now));
        }

        return new DebloatScanResult(apps, statuses, now, scan.ExitCode == 0 ? null : SummarizeError(scan));
    }

    private static List<InstalledUwpApp> ParseApps(string json)
    {
        var apps = new List<InstalledUwpApp>();

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    AddApp(apps, element);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddApp(apps, doc.RootElement);
            }
        }
        catch (JsonException)
        {
            // Salida ilegible: se reporta como UNKNOWN más arriba, nunca como lista vacía real.
        }

        return apps;
    }

    private static void AddApp(List<InstalledUwpApp> apps, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? name = ReadString(element, "Name");
        string? fullName = ReadString(element, "PackageFullName");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fullName))
        {
            return;
        }

        apps.Add(new InstalledUwpApp(
            name,
            fullName,
            ReadString(element, "InstallLocation"),
            ReadString(element, "Version"),
            ReadString(element, "Publisher")));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DebloatAppDefinition FindDefinition(string definitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);

        return DebloatCatalog.RemovableApps.FirstOrDefault(definition =>
            string.Equals(definition.Id, definitionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No existe la app de debloat {definitionId}.", nameof(definitionId));
    }

    /// <summary>Duplica las comillas simples para el literal de PowerShell.</summary>
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Script de quite. Se construye por concatenación (no por raw string
    /// interpolado) para que las llaves de PowerShell no choquen con la sintaxis
    /// de interpolación de C#.
    /// </summary>
    private static string BuildRemoveScript(string packageFullName) =>
        "$ErrorActionPreference = 'Stop'\n" +
        "try {\n" +
        $"    Get-AppxPackage -Package '{Escape(packageFullName)}' | Remove-AppxPackage -ErrorAction Stop\n" +
        "    'OK'\n" +
        "} catch {\n" +
        "    'ERR:' + $_.Exception.Message\n" +
        "}\n";

    /// <summary>Script de restauración por registro del AppxManifest original.</summary>
    private static string BuildRestoreScript(string manifestPath) =>
        "$ErrorActionPreference = 'Stop'\n" +
        $"$manifest = '{Escape(manifestPath)}'\n" +
        "if (-not (Test-Path -LiteralPath $manifest)) { 'ERR:manifest' }\n" +
        "else {\n" +
        "    try {\n" +
        "        Add-AppxPackage -Register $manifest -DisableDevelopmentMode -ErrorAction Stop\n" +
        "        'OK'\n" +
        "    } catch {\n" +
        "        'ERR:' + $_.Exception.Message\n" +
        "    }\n" +
        "}\n";

    private static string SummarizeError(PowerShellScriptRunner.ScriptResult result) =>
        result.StdErr.Length > 0
            ? FirstLine(result.StdErr)
            : $"código {result.ExitCode.ToString(CultureInfo.InvariantCulture)}";

    private static string FirstLine(string text)
    {
        string trimmed = text.Trim();
        int breakAt = trimmed.IndexOfAny(new[] { '\r', '\n' });

        return breakAt < 0 ? trimmed : string.Concat(trimmed.AsSpan(0, breakAt), "…");
    }

    /// <summary>Texto legible del escaneo para el panel de estado.</summary>
    public static string DescribeScan(DebloatScanResult result)
    {
        var builder = new StringBuilder();
        builder.Append(result.ScannedAt is null
            ? "Debloat UNKNOWN: no hubo escaneo válido."
            : $"Escaneo: {result.ScannedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " +
              $"{result.Apps.Count} paquetes del usuario · " +
              $"{result.InstalledCount} catalogados instalados · " +
              $"{result.RemovableCount} disponibles para quitar.");

        if (result.Error is not null)
        {
            builder.Append(" Aviso: ").Append(result.Error);
        }

        return builder.ToString();
    }
}
