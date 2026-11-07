using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using EvoOptimizer.Core.Tweaks;
using EvoOptimizer.Infrastructure.Common;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Tweaks;

/// <summary>
/// Motor de lectura/aplicación/rollback de tweaks de registro.
/// HKCU se escribe en proceso; HKLM siempre pasa por una única solicitud UAC
/// (<see cref="ElevatedProcessRunner"/>) y se relee desde este proceso para
/// verificar. Todo intento queda registrado en <see cref="TweakChangeJournal"/>
/// con su valor original antes de tocar nada.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryTweakService
{
    private readonly TweakChangeJournal _journal;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public RegistryTweakService(TweakChangeJournal? journal = null)
    {
        _journal = journal ?? new TweakChangeJournal(TweakChangeJournal.DefaultPath);
    }

    public sealed record TweakOperationResult(
        bool Success,
        string Message,
        RegistryTweakStatus? Status);

    public IReadOnlyList<RegistryTweakDefinition> Definitions => RegistryTweakDefinitions.All;

    public TweakChangeJournal Journal => _journal;

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (
            exception is SecurityException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Lee el estado real de los tweaks y los clasifica sin escribir nada.</summary>
    public async Task<IReadOnlyList<RegistryTweakStatus>> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TweakChange> active = await _journal.GetActiveAsync(cancellationToken);
        bool elevated = IsCurrentProcessElevated();
        DateTimeOffset now = DateTimeOffset.Now;

        var statuses = new List<RegistryTweakStatus>(RegistryTweakDefinitions.All.Count);

        foreach (RegistryTweakDefinition definition in RegistryTweakDefinitions.All)
        {
            ReadResult read = ReadValue(definition);
            bool hasActiveChange = active.Any(change =>
                change.DefinitionId == definition.Id && change.Status != TweakChangeStatus.RolledBack);

            statuses.Add(RegistryTweakCatalog.Evaluate(
                definition,
                read.Value,
                read.Succeeded,
                elevated,
                hasActiveChange,
                now));
        }

        return statuses;
    }

    public async Task<TweakOperationResult> ApplyAsync(
        string definitionId,
        bool userAuthorized,
        CancellationToken cancellationToken = default)
    {
        RegistryTweakDefinition definition = Find(definitionId);

        if (!userAuthorized)
        {
            return NotAuthorized(definition);
        }

        if (RegistryTweakCatalog.IsBlocked(definition))
        {
            return new TweakOperationResult(
                false,
                "Bloqueado por política EVO: no se tocan claves de Defender, firewall ni valores de servicio.",
                null);
        }

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            ReadResult before = ReadValue(definition);

            if (!before.Succeeded)
            {
                return new TweakOperationResult(
                    false,
                    $"No se pudo leer {definition.RegistryLocation}; EVO no escribe sobre un estado UNKNOWN.",
                    null);
            }

            if (string.Equals(before.Value, definition.DesiredValue, StringComparison.OrdinalIgnoreCase))
            {
                return new TweakOperationResult(
                    true,
                    $"Ya estaba en el valor deseado ({definition.DesiredValue}). No se modificó nada.",
                    ReadStatus(definition, before));
            }

            var change = new TweakChange(
                Guid.NewGuid().ToString("N"),
                definition.Id,
                definition.HiveLabel,
                definition.KeyPath,
                definition.ValueName,
                before.Value ?? string.Empty,
                definition.DesiredValue,
                before.Kind,
                TweakChangeStatus.Pending,
                DateTimeOffset.Now,
                CompletedAt: null,
                Error: null);

            await _journal.AppendAsync(change, cancellationToken);

            (bool wrote, string writeError) = await WriteAsync(definition, cancellationToken);

            if (!wrote)
            {
                await _journal.UpdateAsync(
                    change with
                    {
                        Status = TweakChangeStatus.Failed,
                        CompletedAt = DateTimeOffset.Now,
                        Error = writeError
                    },
                    cancellationToken);

                return new TweakOperationResult(false, writeError, null);
            }

            ReadResult after = ReadValue(definition);

            if (!string.Equals(after.Value, definition.DesiredValue, StringComparison.OrdinalIgnoreCase))
            {
                await RestoreOriginalAsync(definition, change, cancellationToken);

                await _journal.UpdateAsync(
                    change with
                    {
                        Status = TweakChangeStatus.Failed,
                        CompletedAt = DateTimeOffset.Now,
                        Error = "La verificación por relectura falló; se restauró el valor original."
                    },
                    cancellationToken);

                return new TweakOperationResult(
                    false,
                    $"Se escribió pero la relectura no coincidió en {definition.RegistryLocation}. " +
                    "Estado original restaurado.",
                    null);
            }

            await _journal.UpdateAsync(
                change with
                {
                    Status = TweakChangeStatus.Verified,
                    CompletedAt = DateTimeOffset.Now
                },
                cancellationToken);

            string message = $"Aplicado y verificado: {definition.ValueName} = {definition.DesiredValue}.";

            if (definition.RequiresReboot)
            {
                message += " Requiere reinicio para surtir efecto.";
            }

            return new TweakOperationResult(true, message, ReadStatus(definition, after));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<TweakOperationResult> RollbackAsync(
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        RegistryTweakDefinition definition = Find(definitionId);

        await _mutex.WaitAsync(cancellationToken);

        try
        {
            IReadOnlyList<TweakChange> active = await _journal.GetActiveAsync(cancellationToken);

            TweakChange? change = active
                .Where(entry => entry.DefinitionId == definition.Id)
                .OrderByDescending(entry => entry.CreatedAt)
                .FirstOrDefault();

            if (change is null)
            {
                return new TweakOperationResult(
                    false,
                    $"No hay ningún cambio de EVO registrado para «{definition.Name}»; nada que revertir.",
                    null);
            }

            await RestoreOriginalAsync(definition, change, cancellationToken);

            ReadResult now = ReadValue(definition);
            bool restored = now.Succeeded &&
                string.Equals(now.Value, change.OriginalValue, StringComparison.OrdinalIgnoreCase);

            if (!restored)
            {
                await _journal.UpdateAsync(
                    change with
                    {
                        Status = TweakChangeStatus.Failed,
                        Error = "El rollback no se verificó por relectura."
                    },
                    cancellationToken);

                return new TweakOperationResult(
                    false,
                    $"No se pudo restaurar el valor original en {definition.RegistryLocation}.",
                    null);
            }

            await _journal.UpdateAsync(
                change with
                {
                    Status = TweakChangeStatus.RolledBack,
                    CompletedAt = DateTimeOffset.Now,
                    Error = null
                },
                cancellationToken);

            return new TweakOperationResult(
                true,
                change.OriginalKind == TweakValueKind.None
                    ? $"Se eliminó el valor creado por EVO: Windows vuelve a su comportamiento por defecto."
                    : $"Valor original restaurado: {change.OriginalValue}.",
                ReadStatus(definition, now));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<TweakOperationResult>> RollbackAllAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TweakChange> active = await _journal.GetActiveAsync(cancellationToken);

        var results = new List<TweakOperationResult>();

        foreach (TweakChange change in active
            .OrderByDescending(entry => entry.CreatedAt)
            .GroupBy(entry => entry.DefinitionId)
            .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RollbackAsync(change.DefinitionId, cancellationToken));
        }

        return results;
    }

    /// <summary>Aplica en lote sólo tweaks disponibles y de riesgo bajo.</summary>
    public async Task<IReadOnlyList<TweakOperationResult>> ApplyLowRiskAsync(
        bool userAuthorized,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RegistryTweakStatus> statuses = await ScanAsync(cancellationToken);

        var results = new List<TweakOperationResult>();

        foreach (RegistryTweakStatus status in statuses.Where(status =>
            status.State == TweakState.Available && status.Definition.Risk == TweakRisk.Low))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ApplyAsync(status.Definition.Id, userAuthorized, cancellationToken));
        }

        return results;
    }

    // ── Registro ─────────────────────────────────────────────────────────────

    private sealed record ReadResult(bool Succeeded, string? Value, TweakValueKind Kind);

    private static ReadResult ReadValue(RegistryTweakDefinition definition)
    {
        try
        {
            using RegistryKey root = OpenBase(definition.Hive);
            using RegistryKey? key = root.OpenSubKey(definition.KeyPath);

            if (key is null)
            {
                return new ReadResult(true, null, TweakValueKind.None);
            }

            object? raw = key.GetValue(
                definition.ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);

            if (raw is null)
            {
                return new ReadResult(true, null, TweakValueKind.None);
            }

            RegistryValueKind kind = key.GetValueKind(definition.ValueName);

            return raw switch
            {
                int number => new ReadResult(true, unchecked((uint)number).ToString(CultureInfo.InvariantCulture), TweakValueKind.Dword),
                uint number => new ReadResult(true, number.ToString(CultureInfo.InvariantCulture), TweakValueKind.Dword),
                long number => new ReadResult(true, number.ToString(CultureInfo.InvariantCulture), TweakValueKind.Dword),
                string text => new ReadResult(true, text, TweakValueKind.String),
                _ => new ReadResult(true, Convert.ToString(raw, CultureInfo.InvariantCulture), TweakValueKind.Other)
            };
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
                SecurityException or
                InvalidComObjectException or
                ObjectDisposedException or
                IOException or
                ArgumentException)
        {
            return new ReadResult(false, null, TweakValueKind.None);
        }
    }

    private async Task<(bool Wrote, string Error)> WriteAsync(
        RegistryTweakDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.Hive == RegistryHiveChoice.CurrentUser)
        {
            try
            {
                using RegistryKey root = OpenBase(definition.Hive);
                using RegistryKey? key = root.CreateSubKey(definition.KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree);

                if (key is null)
                {
                    return (false, $"No se pudo abrir {definition.RegistryLocation} en escritura.");
                }

                key.SetValue(
                    definition.ValueName,
                    ToObject(definition),
                    ToRegistryKind(definition.ValueKind));

                return (true, string.Empty);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or ArgumentException)
            {
                return (false, $"HKCU denegó la escritura: {exception.Message}");
            }
        }

        string hive = "HKLM";
        string type = definition.ValueKind == RegistryValueChoice.Dword ? "REG_DWORD" : "REG_SZ";
        string data = definition.DesiredValue;

        if (definition.ValueKind == RegistryValueChoice.Dword)
        {
            if (!uint.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return (false, $"El valor DWORD «{data}» no es un entero válido; EVO no lo escribe.");
            }
        }
        else
        {
            data = data.Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        ElevatedProcessRunner.ElevatedResult elevated = await ElevatedProcessRunner.RunAsync(
            "reg.exe",
            $"add \"{hive}\\{definition.KeyPath}\" /v {definition.ValueName} /t {type} /d \"{data}\" /f",
            cancellationToken);

        return DescribeElevated(elevated, definition, write: true);
    }

    private static async Task<bool> RestoreOriginalAsync(
        RegistryTweakDefinition definition,
        TweakChange change,
        CancellationToken cancellationToken)
    {
        if (change.OriginalKind == TweakValueKind.None)
        {
            return definition.Hive == RegistryHiveChoice.CurrentUser
                ? DeleteCurrentUserValue(definition)
                : (await ElevatedProcessRunner.RunAsync(
                    "reg.exe",
                    $"delete \"HKLM\\{definition.KeyPath}\" /v {definition.ValueName} /f",
                    cancellationToken)).Succeeded;
        }

        string type = change.OriginalKind == TweakValueKind.Dword ? "REG_DWORD" : "REG_SZ";
        string data = change.OriginalValue.Replace("\"", "\\\"", StringComparison.Ordinal);

        if (definition.Hive == RegistryHiveChoice.CurrentUser)
        {
            try
            {
                using RegistryKey root = OpenBase(definition.Hive);
                using RegistryKey? key = root.CreateSubKey(
                    definition.KeyPath,
                    RegistryKeyPermissionCheck.ReadWriteSubTree);

                if (key is null)
                {
                    return false;
                }

                key.SetValue(
                    definition.ValueName,
                    change.OriginalKind == TweakValueKind.Dword
                        ? uint.Parse(change.OriginalValue, CultureInfo.InvariantCulture)
                        : change.OriginalValue,
                    change.OriginalKind == TweakValueKind.Dword
                        ? RegistryValueKind.DWord
                        : RegistryValueKind.String);

                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        ElevatedProcessRunner.ElevatedResult elevated = await ElevatedProcessRunner.RunAsync(
            "reg.exe",
            $"add \"HKLM\\{definition.KeyPath}\" /v {definition.ValueName} /t {type} /d \"{data}\" /f",
            cancellationToken);

        return elevated.Succeeded;
    }

    private static bool DeleteCurrentUserValue(RegistryTweakDefinition definition)
    {
        try
        {
            using RegistryKey root = OpenBase(definition.Hive);
            using RegistryKey? key = root.OpenSubKey(definition.KeyPath, writable: true);

            if (key is null)
            {
                return true;
            }

            key.DeleteValue(definition.ValueName, false);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static object ToObject(RegistryTweakDefinition definition) =>
        definition.ValueKind == RegistryValueChoice.Dword
            ? uint.Parse(definition.DesiredValue, CultureInfo.InvariantCulture)
            : definition.DesiredValue;

    private static RegistryValueKind ToRegistryKind(RegistryValueChoice kind) =>
        kind == RegistryValueChoice.Dword ? RegistryValueKind.DWord : RegistryValueKind.String;

    private static RegistryKey OpenBase(RegistryHiveChoice hive) =>
        RegistryKey.OpenBaseKey(
            hive == RegistryHiveChoice.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    private static (bool, string) DescribeElevated(
        ElevatedProcessRunner.ElevatedResult elevated,
        RegistryTweakDefinition definition,
        bool write)
    {
        if (elevated.Succeeded)
        {
            return (true, string.Empty);
        }

        if (elevated.Cancelled)
        {
            return (false, "Autorización UAC cancelada: EVO no escribió en HKLM.");
        }

        if (!elevated.Ran)
        {
            return (false, elevated.Error ?? "No se pudo solicitar elevación.");
        }

        return (false,
            $"reg.exe terminó con código {elevated.ExitCode} al {(write ? "escribir" : "restaurar")} " +
            $"{definition.RegistryLocation}.");
    }

    private RegistryTweakStatus ReadStatus(
        RegistryTweakDefinition definition,
        ReadResult read)
    {
        return RegistryTweakCatalog.Evaluate(
            definition,
            read.Value,
            read.Succeeded,
            IsCurrentProcessElevated(),
            hasActiveChange: true,
            DateTimeOffset.Now);
    }

    private static RegistryTweakDefinition Find(string definitionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);

        return RegistryTweakDefinitions.All.FirstOrDefault(definition =>
            string.Equals(definition.Id, definitionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No existe el tweak {definitionId}.", nameof(definitionId));
    }

    private TweakOperationResult NotAuthorized(RegistryTweakDefinition definition) =>
        new(
            false,
            $"Sin autorización del usuario: «{definition.Name}» no se aplicó. EVO escribe sólo con confirmación explícita.",
            null);
}
