using System.Windows;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Composition root. Fases futuras: sustituir por DI y pasar un IDecisionEngine real.
        var hardware = new RegistryHardwareProfileProvider().Capture();
        _viewModel = new DashboardViewModel(new WindowsTelemetryProvider(), hardware);
        DataContext = _viewModel;

        Closed += (_, _) => _viewModel.Dispose();
    }
}
