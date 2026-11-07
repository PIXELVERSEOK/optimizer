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
        // Crear el ViewModel ANTES de InitializeComponent para que el panel
        // reciba el DataContext correcto desde el primer layout pass.
        _viewModel = new EvoControlCenterViewModel();

        InitializeComponent();

        // Asignar DataContext explícito en la ventana Y en el panel para
        // romper cualquier herencia del DataContext de la ventana propietaria.
        DataContext = _viewModel;
        HubPanel.DataContext = _viewModel;
        LiveMonitorPanel.DataContext = _viewModel;
        TweakPanel.DataContext = _viewModel;

        Loaded += HandleLoaded;
        Closing += HandleClosing;
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HandleLoaded;

        try
        {
            await _viewModel.StartAsync();
            await _viewModel.RefreshWindowsOptimizationAsync();
            await _viewModel.StartOptimizationHubAsync();
            _viewModel.InitializeTweaksState();
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

            // Close() no puede reentrarse mientras el evento Closing está en
            // la pila: si DisposeAsync terminó de forma síncrona, llamarlo aquí
            // lanza "no se puede llamar a Close mientras un Window se está
            // cerrando". Se difiere al dispatcher para cerrar en el siguiente
            // turno del bucle.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
