using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;

namespace EvoOptimizer.App.Views;

public partial class EvoHistoryPanel : UserControl
{
    public event RoutedEventHandler? ActiveChangesRequested;

    public EvoHistoryPanel()
    {
        InitializeComponent();
        Loaded += HandleLoaded;
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HandleLoaded;
        await RefreshHistoryAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshHistoryAsync();

    private async Task RefreshHistoryAsync()
    {
        if (DataContext is HistoryDashboardViewModel viewModel)
            await viewModel.LoadAsync();
    }

    private void OpenActiveChanges_Click(object sender, RoutedEventArgs e) =>
        ActiveChangesRequested?.Invoke(this, e);
}
