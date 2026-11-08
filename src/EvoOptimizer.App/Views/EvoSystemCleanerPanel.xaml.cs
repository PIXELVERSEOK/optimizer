using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;

namespace EvoOptimizer.App.Views;

public partial class EvoSystemCleanerPanel : UserControl
{
    private bool _historyLoaded;

    public EvoSystemCleanerPanel()
    {
        InitializeComponent();
        Loaded += HandleLoaded;
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        if (_historyLoaded || DataContext is not AdvancedSystemCleanupViewModel viewModel)
            return;

        _historyLoaded = true;
        try
        {
            await viewModel.LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "EVO · Historial de limpieza",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AdvancedSystemCleanupViewModel viewModel)
            return;

        try
        {
            await viewModel.AnalyzeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "EVO · Análisis de limpieza",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AdvancedSystemCleanupViewModel viewModel)
            return;

        string categories = string.Join(
            Environment.NewLine,
            viewModel.Categories
                .Where(category => category.IsSelected && category.EligibleFileCount > 0)
                .Select(category => $"• {category.Name}: {category.EligibleFileCount:N0} archivos"));
        if (string.IsNullOrEmpty(categories))
            return;

        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Se intentará eliminar solo archivos de estas categorías, con al menos {viewModel.SelectedRetention.Label} de antigüedad:{Environment.NewLine}{Environment.NewLine}" +
            $"{categories}{Environment.NewLine}{Environment.NewLine}" +
            $"Estimación: {viewModel.SelectedFileCount:N0} archivos · {viewModel.SelectedEstimatedBytesText}.{Environment.NewLine}" +
            "Los archivos bloqueados, recientes, protegidos o inaccesibles se omitirán. Esta acción no tiene deshacer. ¿Continuar?",
            "Confirmar limpieza",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await viewModel.CleanSelectedAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "EVO · Limpieza del sistema",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdvancedSystemCleanupViewModel viewModel)
            viewModel.CancelOperation();
    }
}
