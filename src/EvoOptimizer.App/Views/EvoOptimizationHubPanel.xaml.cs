using System.Windows;
using System.Windows.Controls;
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
            if (process.Importance == ProcessImportance.Critical)
            {
                MessageBox.Show(
                    $"'{process.Name}' es un proceso crítico protegido. EVO no lo modifica.",
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
}