using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using EvoOptimizer.Infrastructure.GameProfiles;

namespace EvoOptimizer.App.ViewModels;

public sealed partial class EvoControlCenterViewModel
{
    private readonly Cs2AutoexecConfigService _cs2AutoexecConfigService =
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EvoFpsOptimizer",
            "GameProfiles",
            "CS2"));

    private string _cs2ConfigPath = string.Empty;
    private string _cs2ConfigStatusText =
        "Elegí el autoexec.cfg de CS2 para leer su bloque EVO o preparar uno nuevo.";
    private string _cs2ConfigBackupText = "No hay backup pendiente de restauración.";
    private string _newCs2ConfigName = string.Empty;
    private string _newCs2ConfigValue = string.Empty;
    private bool _isCs2ConfigLoaded;
    private Cs2ConfigSettingItem? _selectedCs2ConfigSetting;
    private Cs2AutoexecSnapshot? _cs2ConfigSnapshot;

    public ObservableCollection<Cs2ConfigSettingItem> Cs2ConfigSettings { get; } = new();

    public IReadOnlyCollection<string> Cs2SupportedVariables =>
        Cs2AutoexecConfigService.SupportedVariableNames;

    public string Cs2ConfigPath
    {
        get => _cs2ConfigPath;
        set
        {
            if (!SetField(ref _cs2ConfigPath, value))
                return;

            _isCs2ConfigLoaded = false;
            _cs2ConfigSnapshot = null;
            Cs2ConfigBackupText = "Leé la ruta para consultar el estado de restauración.";
            OnPropertyChanged(nameof(CanApplyCs2Config));
            OnPropertyChanged(nameof(CanRestoreCs2Config));
        }
    }

    public string Cs2ConfigStatusText
    {
        get => _cs2ConfigStatusText;
        private set => SetField(ref _cs2ConfigStatusText, value);
    }

    public string Cs2ConfigBackupText
    {
        get => _cs2ConfigBackupText;
        private set => SetField(ref _cs2ConfigBackupText, value);
    }

    public string NewCs2ConfigName
    {
        get => _newCs2ConfigName;
        set => SetField(ref _newCs2ConfigName, value);
    }

    public string NewCs2ConfigValue
    {
        get => _newCs2ConfigValue;
        set => SetField(ref _newCs2ConfigValue, value);
    }

    public Cs2ConfigSettingItem? SelectedCs2ConfigSetting
    {
        get => _selectedCs2ConfigSetting;
        set => SetField(ref _selectedCs2ConfigSetting, value);
    }

    public bool CanApplyCs2Config =>
        _isCs2ConfigLoaded && !_isCs2ConfigBusy && !_disposed;

    public bool CanRestoreCs2Config =>
        _cs2ConfigSnapshot?.CanRestore == true &&
        !_isCs2ConfigBusy &&
        !_disposed;

    public async Task LoadCs2ConfigAsync()
    {
        if (_disposed)
            return;

        IsCs2ConfigBusy = true;
        try
        {
            var snapshot = await _cs2AutoexecConfigService.LoadAsync(Cs2ConfigPath);
            UpdateCs2ConfigSnapshot(snapshot);
            Cs2ConfigStatusText = snapshot.FileExists
                ? $"Leído: {snapshot.Settings.Count} variable(s) administrada(s) por EVO. " +
                  "El contenido fuera del bloque EVO se conserva."
                : "No existe autoexec.cfg en esta ruta. EVO puede crear uno al aplicar; " +
                  "la restauración quitará el archivo si no cambia.";
        }
        catch (Exception ex)
        {
            Cs2ConfigStatusText = $"No se pudo leer la configuración CS2: {ex.Message}";
            throw;
        }
        finally
        {
            IsCs2ConfigBusy = false;
        }
    }

    public void AddCs2ConfigSetting()
    {
        try
        {
            var setting = new Cs2AutoexecSetting(
                NewCs2ConfigName.Trim(),
                NewCs2ConfigValue.Trim());
            Cs2AutoexecConfigService.ValidateSettings(new[] { setting });

            if (Cs2ConfigSettings.Any(existing =>
                    string.Equals(
                        existing.Name,
                        setting.Name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"La variable '{setting.Name}' ya está en el perfil.");
            }

            Cs2ConfigSettings.Add(
                new Cs2ConfigSettingItem(setting.Name, setting.Value));
            NewCs2ConfigName = string.Empty;
            NewCs2ConfigValue = string.Empty;
            Cs2ConfigStatusText =
                "Variable agregada al borrador. Todavía no se escribió en el archivo.";
        }
        catch (ArgumentException ex)
        {
            Cs2ConfigStatusText = ex.Message;
        }
    }

    public void RemoveSelectedCs2ConfigSetting()
    {
        if (SelectedCs2ConfigSetting is null)
            return;

        Cs2ConfigSettings.Remove(SelectedCs2ConfigSetting);
        SelectedCs2ConfigSetting = null;
        Cs2ConfigStatusText =
            "Variable quitada del borrador. El archivo todavía no se modificó.";
    }

    public async Task ApplyCs2ConfigAsync(bool userAuthorized)
    {
        if (_disposed)
            return;
        if (!userAuthorized)
            throw new UnauthorizedAccessException(
                "La escritura de configuración requiere confirmación explícita.");
        EnsureCs2IsClosed("escribir");
        if (!_isCs2ConfigLoaded)
            throw new InvalidOperationException(
                "Primero leé una ruta válida de autoexec.cfg.");

        IsCs2ConfigBusy = true;
        try
        {
            var settings = Cs2ConfigSettings
                .Select(setting => new Cs2AutoexecSetting(
                    setting.Name.Trim(),
                    setting.Value.Trim()))
                .ToArray();
            var snapshot = await _cs2AutoexecConfigService.ApplyAsync(
                Cs2ConfigPath,
                settings);
            UpdateCs2ConfigSnapshot(snapshot);
            Cs2ConfigStatusText =
                $"Operación completada: {snapshot.Settings.Count} variable(s) administrada(s). " +
                "Si hubo escritura, fue verificada; EVO no midió ni promete una mejora.";
        }
        catch (Exception ex)
        {
            Cs2ConfigStatusText = $"No se aplicó la configuración: {ex.Message}";
            throw;
        }
        finally
        {
            IsCs2ConfigBusy = false;
        }
    }

    public async Task RestoreCs2ConfigAsync(bool userAuthorized)
    {
        if (_disposed)
            return;
        if (!userAuthorized)
            throw new UnauthorizedAccessException(
                "La restauración de configuración requiere confirmación explícita.");
        EnsureCs2IsClosed("restaurar");

        IsCs2ConfigBusy = true;
        try
        {
            var snapshot = await _cs2AutoexecConfigService.RestoreAsync(
                Cs2ConfigPath);
            UpdateCs2ConfigSnapshot(snapshot);
            Cs2ConfigStatusText =
                "Configuración anterior restaurada y verificada. El backup se conserva.";
        }
        catch (Exception ex)
        {
            Cs2ConfigStatusText = $"No se pudo restaurar el archivo: {ex.Message}";
            throw;
        }
        finally
        {
            IsCs2ConfigBusy = false;
        }
    }

    private bool _isCs2ConfigBusy;

    private bool IsCs2ConfigBusy
    {
        get => _isCs2ConfigBusy;
        set
        {
            if (!SetField(ref _isCs2ConfigBusy, value))
                return;

            OnPropertyChanged(nameof(CanApplyCs2Config));
            OnPropertyChanged(nameof(CanRestoreCs2Config));
        }
    }

    private void UpdateCs2ConfigSnapshot(Cs2AutoexecSnapshot snapshot)
    {
        Cs2ConfigPath = snapshot.Path;
        _cs2ConfigSnapshot = snapshot;
        _isCs2ConfigLoaded = true;

        Cs2ConfigSettings.Clear();
        foreach (var setting in snapshot.Settings)
        {
            Cs2ConfigSettings.Add(
                new Cs2ConfigSettingItem(setting.Name, setting.Value));
        }

        Cs2ConfigBackupText = snapshot.BackupPath is not null
            ? snapshot.BackupPath
            : snapshot.CanRestore
                ? "No existía archivo anterior; restaurar quitará el archivo EVO sin cambios externos."
                : "No hay backup pendiente de restauración.";
        Cs2ConfigStatusText = snapshot.RestoreStatus;
        OnPropertyChanged(nameof(CanApplyCs2Config));
        OnPropertyChanged(nameof(CanRestoreCs2Config));
    }

    private void EnsureCs2IsClosed(string action)
    {
        Process[] running = Process.GetProcessesByName("cs2");
        try
        {
            if (Cs2ProcessCount > 0 || running.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Cerrá Counter-Strike 2 antes de {action} su configuración.");
            }
        }
        finally
        {
            foreach (Process process in running)
                process.Dispose();
        }
    }
}

public sealed class Cs2ConfigSettingItem : INotifyPropertyChanged
{
    private string _name;
    private string _value;

    public Cs2ConfigSettingItem(string name, string value)
    {
        _name = name;
        _value = value;
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(
        ref string field,
        string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
