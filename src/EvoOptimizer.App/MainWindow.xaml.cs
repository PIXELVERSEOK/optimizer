using EvoOptimizer.App.Navigation;
using System.Windows;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.Telemetry;
using EvoOptimizer.Infrastructure.Audit;
using EvoOptimizer.Core.Diagnosis;

namespace EvoOptimizer.App;


public partial class MainWindow : Window
{
    private readonly EvoControlCenterLauncher _controlCenter = new();

    private void OpenControlCenter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _controlCenter.Open(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "EVO CONTROL CENTER",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private readonly DashboardViewModel _viewModel;
    private readonly WindowsSystemAuditor _auditor = new();
    private readonly AdaptiveDiagnosticEngine _diagnosticEngine = new();

    public MainWindow()
    {
        InitializeComponent();

        // Composition root. Fases futuras: sustituir por DI y pasar un IDecisionEngine real.
        var hardware = new RegistryHardwareProfileProvider().Capture();
        _viewModel = new DashboardViewModel(new WindowsTelemetryProvider(), hardware);
        DataContext = _viewModel;

        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void AnalyzeSystem_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeSystemButton.IsEnabled = false;
        AnalyzeSystemStatus.Text = "Recopilando informaciÃ³n del sistema...";

        try
        {
            var audit = await _auditor.CaptureAsync();

            var report = _diagnosticEngine.Analyze(audit);

            SystemDiagnosticItems.ItemsSource = report.Findings;

            var exported = await WindowsSystemAuditor.ExportAsync(audit);

            AnalyzeSystemStatus.Text =
                $"AnÃ¡lisis completado: {report.Findings.Count} hallazgos. " +
                $"Advertencias de auditorÃ­a: {audit.Warnings.Count}. " +
                $"Informe: {exported.TextPath}";
        }
        catch (Exception ex)
        {
            AnalyzeSystemStatus.Text = "Error durante el anÃ¡lisis.";

            MessageBox.Show(
                ex.Message,
                "EVO FPS OPTIMIZER",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            AnalyzeSystemButton.IsEnabled = true;
        }
    }
}

