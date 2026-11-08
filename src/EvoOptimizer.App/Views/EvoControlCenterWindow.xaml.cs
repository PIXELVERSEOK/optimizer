using System.IO;
using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Core.Gaming;
using EvoOptimizer.Infrastructure.Gaming;
using EvoOptimizer.Infrastructure.Hardware;
using EvoOptimizer.Infrastructure.History;
using EvoOptimizer.Infrastructure.Optimization;
using EvoOptimizer.Infrastructure.Telemetry;

namespace EvoOptimizer.App.Views;

public partial class EvoControlCenterWindow : UserControl
{
    private readonly EvoControlCenterViewModel _viewModel;
    private readonly GamingOptimizerViewModel _gamingOptimizerViewModel;
    private readonly GameBoosterViewModel _gameBoosterViewModel;
    private bool _disposed;
    private Task? _initializationTask;

    public EvoControlCenterWindow()
    {
        // Servicios compartidos: el monitor, la configuración gaming, la
        // topología y los optimizadores de prioridad/afinidad son las MISMAS
        // instancias para el Control Center, el Game Optimizer y el Booster.
        // No se crean motores paralelos.
        var monitor = new LivePerformanceMonitor();
        var gamingConfigService = new WindowsGamingConfigService();
        var topologyProvider = new IntelHybridTopologyProvider();
        var priorityOptimizer = new Cs2ProcessPriorityOptimizer();
        var affinityOptimizer = new Cs2AffinityOptimizer();
        EvoHistoryStore? historyStore = TryCreateHistoryStore();

        // Crear los ViewModels ANTES de InitializeComponent para que los
        // paneles reciban el DataContext correcto desde el primer layout pass.
        _viewModel = new EvoControlCenterViewModel(
            monitor: monitor,
            topologyProvider: topologyProvider,
            gamingConfigService: gamingConfigService,
            priorityOptimizer: priorityOptimizer,
            affinityOptimizer: affinityOptimizer,
            historyStore: historyStore);

        _gamingOptimizerViewModel = new GamingOptimizerViewModel(
            configService: gamingConfigService,
            topologyProvider: topologyProvider,
            monitor: monitor);

        _gameBoosterViewModel = new GameBoosterViewModel(
            new GameBoosterService(
                governance: new ProcessGovernanceService(historyStore),
                configService: gamingConfigService,
                gamePriority: priorityOptimizer,
                gameAffinity: affinityOptimizer,
                topology: topologyProvider.DiscoverTopology(),
                history: historyStore));

        InitializeComponent();

        // Asignar DataContext explícito en la ventana Y en los paneles para
        // romper cualquier herencia del DataContext de la ventana propietaria.
        DataContext = _viewModel;
        HubPanel.DataContext = _viewModel;
        LiveMonitorPanel.DataContext = _viewModel;
        TweakPanel.DataContext = _viewModel;
        GamingOptimizerPanel.DataContext = _gamingOptimizerViewModel;
        GameBoosterPanel.DataContext = _gameBoosterViewModel;

        // Módulo 3: el Optimizer recomienda juego + perfil y el shell los
        // entrega al Booster, que queda listo para autorizar y aplicar.
        GamingOptimizerPanel.BoostRequested += HandleBoostRequested;

        Loaded += HandleLoaded;
    }

    private static EvoHistoryStore? TryCreateHistoryStore()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EvoFpsOptimizer",
                "history.db");

            return new EvoHistoryStore(path);
        }
        catch
        {
            return null;
        }
    }

    public void SelectControlCenter() => ControlCenterSections.SelectedIndex = 0;

    public void SelectOptimizationCatalog()
    {
        ControlCenterSections.SelectedIndex = 0;
        HubPanel.SelectOptimizationCatalogTab();
    }

    public void SelectHardwareDiagnostics()
    {
        ControlCenterSections.SelectedIndex = 0;
        HubPanel.SelectHardwareDiagnosticsTab();
    }

    public void SelectEvoAi()
    {
        ControlCenterSections.SelectedIndex = 0;
        HubPanel.SelectEvoAiTab();
    }

    public void SelectScannerTweaksDebloat() => ControlCenterSections.SelectedIndex = 1;

    public void SelectScanner()
    {
        ControlCenterSections.SelectedIndex = 1;
        TweakPanel.SelectScannerTab();
    }

    public void SelectTweaks()
    {
        ControlCenterSections.SelectedIndex = 1;
        TweakPanel.SelectTweaksTab();
    }

    public void SelectDebloat()
    {
        ControlCenterSections.SelectedIndex = 1;
        TweakPanel.SelectDebloatTab();
    }

    public void SelectLiveMonitor() => ControlCenterSections.SelectedIndex = 2;

    public void SelectGamingOptimizer() => ControlCenterSections.SelectedIndex = 3;

    public void SelectGameBooster() => ControlCenterSections.SelectedIndex = 4;

    private void HandleBoostRequested(InstalledGame? game, GamingProfileKind profileKind)
    {
        GameBoosterPanel.PrepareBoost(game, profileKind);
        ControlCenterSections.SelectedIndex = 4;
    }

    public async Task RefreshOptimizationCatalogAsync()
    {
        if (_initializationTask is not null)
            await _initializationTask;

        await _viewModel.RefreshCatalogAsync();
    }

    public string CatalogCountsText => _viewModel.CatalogCountsText;
    public string CatalogStatusText => _viewModel.CatalogStatusText;

    public void SelectCs2Config()
    {
        ControlCenterSections.SelectedIndex = 0;
        HubPanel.SelectCs2ConfigTab();
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HandleLoaded;
        _initializationTask = InitializeAsync();
        await _initializationTask;
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Recuperación ante cierre inesperado: si quedó una sesión de
            // Boost interrumpida, se restaura desde el diario persistente
            // antes de habilitar cualquier optimización nueva.
            await _gameBoosterViewModel.InitializeAsync();

            await _viewModel.StartAsync();
            await _viewModel.RefreshWindowsOptimizationAsync();
            await _viewModel.StartOptimizationHubAsync();
            _viewModel.InitializeTweaksState();

            await _gamingOptimizerViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.ToString(),
                "EVO - Error iniciando monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_initializationTask is not null)
                await _initializationTask;

            // El Booster restaura el sistema si hay un Boost activo al cerrar
            // EVO; el diario write-ahead cubre cualquier fallo restante.
            await _gameBoosterViewModel.DisposeAsync();
            await _viewModel.DisposeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "EVO - Error de cierre",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}