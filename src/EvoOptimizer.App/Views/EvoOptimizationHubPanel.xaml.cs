using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Core.Optimization;
using EvoOptimizer.Infrastructure.Optimization;

namespace EvoOptimizer.App.Views;

public partial class EvoOptimizationHubPanel : UserControl
{
    public EvoOptimizationHubPanel()
    {
        InitializeComponent();
    }

    private EvoControlCenterViewModel? ViewModel => DataContext as EvoControlCenterViewModel;

    public void SelectCs2ConfigTab() => HubSections.SelectedIndex = 3;
    public void SelectOptimizationCatalogTab() => HubSections.SelectedIndex = 1;
    public void SelectHardwareDiagnosticsTab() => HubSections.SelectedIndex = 2;
    public void SelectEvoAiTab() => HubSections.SelectedIndex = 4;

    private async void StartMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.StartAsync();
    }

    private async void StopMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.StopAsync();
    }

    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.RefreshCatalogAsync();
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        await ViewModel.RefreshDiagnosticsAsync();
        await ViewModel.RefreshCatalogAsync();
    }

    private async void CatalogAction_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement { DataContext: OptimizationCatalogEntry entry } element)
        {
            return;
        }

        string action = element.Tag as string ?? string.Empty;

        if (action == "apply")
        {
            var confirm = MessageBox.Show(
                $"¿Aplicar '{entry.Name}'?\n\n" +
                $"Beneficio esperado: {entry.ExpectedBenefit}\n" +
                $"Riesgo: {entry.Risk}\n" +
                $"Estado actual: {entry.CurrentState}\n\n" +
                "Toda modificación queda en el ChangeJournal y puede revertirse.",
                "EVO · Autorización explícita",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
                return;
        }
        else if (action == "rollback")
        {
            var confirm = MessageBox.Show(
                $"¿Revertir '{entry.Name}' al estado original registrado?",
                "EVO · Reversión",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        await ViewModel.ExecuteCatalogActionAsync(entry, action);
    }

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement element)
            return;

        string profile = element.Tag as string ?? string.Empty;

        var confirm = MessageBox.Show(
            $"¿Activar el perfil {profile}?\n\n" +
            "Los perfiles agrupan optimizaciones de proceso ya existentes " +
            "(prioridad AboveNormal y afinidad híbrida). Son reversibles desde " +
            "el mismo panel y nunca usan High/Realtime.",
            "EVO · Perfil de uso",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        await ViewModel.ApplyProfileAsync(profile);
    }

    private async void ProcessAction_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement { DataContext: GovernedProcess process } element)
        {
            return;
        }

        string action = element.Tag as string ?? string.Empty;

        if (action == "throttle")
        {
            if (process.Importance is ProcessImportance.Critical or ProcessImportance.Unknown)
            {
                MessageBox.Show(
                    process.Importance == ProcessImportance.Critical
                        ? $"'{process.Name}' es un proceso crítico protegido. EVO no lo modifica."
                        : $"'{process.Name}' no está clasificado. EVO no cambia la prioridad de procesos desconocidos.",
                    "EVO · Protección de procesos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"¿Bajar la prioridad de '{process.Name}' a BelowNormal?\n\n" +
                "EVO no termina procesos, no usa High/Realtime y guarda la prioridad " +
                "original para restaurarla.",
                "EVO · Gobierno de procesos",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
                return;

            await ViewModel.ThrottleProcessAsync(process, userAuthorized: true);
            return;
        }

        if (action == "restore")
            await ViewModel.RestoreProcessAsync(process);
    }

    private async void BrowseCs2Config_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar autoexec.cfg de Counter-Strike 2",
            Filter = "Configuración CS2 (autoexec.cfg)|autoexec.cfg|Todos los archivos (*.*)|*.*",
            FileName = "autoexec.cfg",
            CheckFileExists = false,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(ViewModel.Cs2ConfigPath))
        {
            string? directory = System.IO.Path.GetDirectoryName(ViewModel.Cs2ConfigPath);
            if (!string.IsNullOrWhiteSpace(directory) &&
                System.IO.Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        ViewModel.Cs2ConfigPath = dialog.FileName;
        await LoadCs2ConfigAsync(ViewModel);
    }

    private async void LoadCs2Config_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            await LoadCs2ConfigAsync(ViewModel);
    }

    private void AddCs2ConfigSetting_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddCs2ConfigSetting();
    }

    private void RemoveCs2ConfigSetting_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.RemoveSelectedCs2ConfigSetting();
    }

    private async void ApplyCs2Config_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (ViewModel.Cs2ProcessCount > 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Cerrá Counter-Strike 2 antes de escribir su configuración.",
                "EVO · CS2 en ejecución",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string settings = ViewModel.Cs2ConfigSettings.Count == 0
            ? "Se quitará únicamente el bloque administrado por EVO."
            : string.Join(
                Environment.NewLine,
                ViewModel.Cs2ConfigSettings.Select(item =>
                    $"  {item.Name.Trim()} {item.Value.Trim()}"));
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"¿Escribir estas variables en el autoexec.cfg seleccionado?\n\n" +
            $"{settings}\n\n" +
            "El archivo existente tendrá un backup. Solo se reemplazará el bloque EVO; " +
            "el contenido restante se conserva. EVO no valida el efecto en el juego " +
            "ni promete una mejora. Esta acción no modifica Steam ni Windows.",
            "EVO · Confirmación de configuración CS2",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await ViewModel.ApplyCs2ConfigAsync(userAuthorized: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "EVO · No se pudo aplicar la configuración",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RestoreCs2Config_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (ViewModel.Cs2ProcessCount > 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Cerrá Counter-Strike 2 antes de restaurar su configuración.",
                "EVO · CS2 en ejecución",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            "¿Restaurar el archivo original guardado por EVO?\n\n" +
            "Se reemplazará el autoexec.cfg completo por el backup. EVO solo permite " +
            "esta acción si el archivo actual sigue coincidiendo byte por byte con " +
            "el último estado escrito por EVO.",
            "EVO · Restauración de configuración CS2",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await ViewModel.RestoreCs2ConfigAsync(userAuthorized: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "EVO · No se pudo restaurar la configuración",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static async Task LoadCs2ConfigAsync(
        EvoControlCenterViewModel viewModel)
    {
        try
        {
            await viewModel.LoadCs2ConfigAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "EVO · No se pudo leer la configuración CS2",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}