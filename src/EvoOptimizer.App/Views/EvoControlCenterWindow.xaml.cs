using System.ComponentModel;
using System.Windows;
using EvoOptimizer.App.ViewModels;

namespace EvoOptimizer.App.Views;

public partial class EvoControlCenterWindow : Window
{
    private readonly EvoControlCenterViewModel _viewModel;
    private bool _shutdownStarted;
    private bool _allowClose;

    public EvoControlCenterWindow()
    {
        InitializeComponent();

        _viewModel = new EvoControlCenterViewModel();
        LiveMonitorPanel.DataContext = _viewModel;

        Loaded += HandleLoaded;
        Closing += HandleClosing;
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HandleLoaded;

        try
        {
            await _viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.ToString(),
                "EVO - Error iniciando monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;

        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        IsEnabled = false;

        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "EVO - Error de cierre",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }
}
