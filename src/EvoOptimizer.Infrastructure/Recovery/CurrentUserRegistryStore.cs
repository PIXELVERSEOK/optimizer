using System.Globalization;
using System.Runtime.Versioning;
using EvoOptimizer.Core.Recovery;
using Microsoft.Win32;

namespace EvoOptimizer.Infrastructure.Recovery;

[SupportedOSPlatform("windows")]
public sealed class CurrentUserRegistryStore : ISettingStore
{
    private static readonly HashSet<(string Path, string Name)> Allowed =
        new()
        {
            (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
            (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"),
            (@"Software\Microsoft\GameBar", "AutoGameModeEnabled")
        };

    public Task<StoredSetting> ReadAsync(
        string path,
        string valueName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(path, valueName);

        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);

        if (key is null || !key.GetValueNames().Contains(
                valueName, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(
                new StoredSetting(false, StoredValueKind.DWord, null));
        }

        var kind = key.GetValueKind(valueName);
        var value = key.GetValue(
            valueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        return kind switch
        {
            RegistryValueKind.DWord =>
                Task.FromResult(new StoredSetting(
                    true, StoredValueKind.DWord,
                    Convert.ToInt32(value, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture))),

            RegistryValueKind.QWord =>
                Task.FromResult(new StoredSetting(
                    true, StoredValueKind.QWord,
                    Convert.ToInt64(value, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture))),

            RegistryValueKind.String =>
                Task.FromResult(new StoredSetting(
                    true, StoredValueKind.String,
                    Convert.ToString(value, CultureInfo.InvariantCulture))),

            _ => throw new NotSupportedException(
                $"Tipo de Registro no compatible: {kind}")
        };
    }

    public Task WriteAsync(
        string path,
        string valueName,
        StoredSetting value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(path, valueName);

        if (!value.Exists || value.Value is null)
            throw new ArgumentException("El valor solicitado no es válido.");

        object registryValue = value.Kind switch
        {
            StoredValueKind.DWord =>
                int.Parse(value.Value, CultureInfo.InvariantCulture),

            StoredValueKind.QWord =>
                long.Parse(value.Value, CultureInfo.InvariantCulture),

            StoredValueKind.String => value.Value,

            _ => throw new NotSupportedException("Tipo de valor no compatible.")
        };

        var registryKind = value.Kind switch
        {
            StoredValueKind.DWord => RegistryValueKind.DWord,
            StoredValueKind.QWord => RegistryValueKind.QWord,
            StoredValueKind.String => RegistryValueKind.String,
            _ => throw new NotSupportedException()
        };

        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException(
                $"No se pudo abrir HKCU\\{path}");

        key.SetValue(valueName, registryValue, registryKind);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string path,
        string valueName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(path, valueName);

        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);

        key?.DeleteValue(valueName, throwOnMissingValue: false);

        return Task.CompletedTask;
    }

    private static void Validate(string path, string valueName)
    {
        if (!Allowed.Contains((path, valueName)))
        {
            throw new UnauthorizedAccessException(
                $"Acceso al Registro bloqueado: HKCU\\{path}\\{valueName}");
        }
    }
}
