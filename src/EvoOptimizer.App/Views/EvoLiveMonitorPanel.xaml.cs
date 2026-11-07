using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Core.Optimization;

namespace EvoOptimizer.App.Views;

public partial class EvoLiveMonitorPanel : UserControl
{
    private bool _operationInProgress;

    public EvoLiveMonitorPanel()
    {
        InitializeComponent();
    }

    private async void StartMonitoring_Click(
        object sender, RoutedEventArgs e)
    {
        await ExecuteMonitorOperationAsync(
            async vm => await vm.StartAsync(),
            "EVO - Error iniciando monitor");
    }

    private async void StopMonitoring_Click(
        object sender, RoutedEventArgs e)
    {
        await ExecuteMonitorOperationAsync(
            async vm => await vm.StopAsync(),
            "EVO - Error deteniendo monitor");
    }

    private void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EvoControlCenterViewModel vm)
            vm.ApplyAboveNormalPriority();
    }

    private void ApplyPcores_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EvoControlCenterViewModel vm)
            vm.ApplyPcoresAffinity();
    }

    private void ApplyBalanced_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EvoControlCenterViewModel vm)
            vm.ApplyBalancedAffinity();
    }

    private void RollbackAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EvoControlCenterViewModel vm)
            vm.RollbackAllOptimizations();
    }

    private async void CaptureBaseline_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteMonitorOperationAsync(
            async vm => await vm.RunBenchmarkSessionAsync(asBaseline: true),
            "EVO - Error en benchmark de línea base");
    }

    private async void CaptureCandidate_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteMonitorOperationAsync(
            async vm => await vm.RunBenchmarkSessionAsync(asBaseline: false),
            "EVO - Error en benchmark de candidato");
    }

    // ── Optimización de Windows ──────────────────────────────────────────────

    private async void ApplyWindowsOptimization_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (sender is not FrameworkElement { Tag: WindowsOptimizationTarget target })
            return;

        var item = vm.WindowsOptimizationItems
            .FirstOrDefault(x => x.Target == target);

        if (item is null)
            return;

        if (!vm.HasWindowsBaseline(target))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "MEDICIÓN REQUERIDA\n\n" +
                "Antes de aplicar este ajuste tenés que capturar una línea base " +
                "con el botón 'MEDIR LÍNEA BASE'.\n\n" +
                "EVO no modifica Windows sin evidencia antes/después.",
                "EVO - Medición obligatoria",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string message =
            "¿Autorizás este cambio en Windows?\n\n" +
            $"{item.Title}\n\n" +
            $"Estado actual: {item.CurrentState}\n" +
            $"Cambio propuesto: {item.ProposedState}\n\n" +
            $"Beneficio esperado: {item.ExpectedBenefit}\n" +
            $"Riesgo: {item.Risk}\n\n" +
            "Ya existe una línea base medida. EVO guardará el estado original " +
            "antes de modificar, podrá revertirlo y exigirá una medición " +
            "posterior para conservar el cambio.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Autorizar cambio en Windows",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.ApplyWindowsOptimizationAsync(
                target,
                userAuthorized: true),
            "EVO - Error aplicando ajuste de Windows");
    }

    private async void DisableStartupApp_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (sender is not FrameworkElement { Tag: string valueName } ||
            string.IsNullOrWhiteSpace(valueName))
        {
            return;
        }

        var candidate = vm.StartupCandidates
            .FirstOrDefault(x => string.Equals(
                x.Name,
                valueName,
                StringComparison.OrdinalIgnoreCase));

        if (!vm.HasStartupBaseline(valueName))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "MEDICIÓN REQUERIDA\n\n" +
                $"Antes de desactivar '{valueName}' tenés que capturar su " +
                "línea base con el botón 'MEDIR LÍNEA BASE'.",
                "EVO - Medición obligatoria",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string message =
            "¿Desactivar esta aplicación del inicio de Windows?\n\n" +
            $"Aplicación: {valueName}\n" +
            (candidate is null
                ? string.Empty
                : $"Comando: {candidate.Command}\n" +
                  $"Recomendación: {candidate.Recommendation}\n") +
            "\nYa existe una línea base medida. Se quitará sólo de " +
            "HKCU\\...\\Run, podrá restaurarse desde los cambios activos y " +
            "exigirá una medición posterior para conservar el cambio.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Autorizar cambio de inicio",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.DisableStartupAppAsync(
                valueName,
                userAuthorized: true),
            "EVO - Error desactivando aplicación de inicio");
    }

    private async void RollbackWindowsChange_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (sender is not FrameworkElement { Tag: Guid changeId })
            return;

        var change = vm.WindowsChanges
            .FirstOrDefault(x => x.Id == changeId);

        string message =
            "¿Revertir este cambio de Windows?\n\n" +
            $"{change?.SettingKey ?? changeId.ToString()}\n\n" +
            "Se restaurará exactamente el estado guardado antes de aplicarlo.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Revertir cambio de Windows",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.RollbackWindowsChangeAsync(changeId),
            "EVO - Error revirtiendo cambio de Windows");
    }

    private async void RollbackAllWindowsChanges_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (vm.WindowsChanges.Count == 0)
            return;

        string message =
            $"¿Revertir los {vm.WindowsChanges.Count} cambios de Windows activos?\n\n" +
            "Se restaurará el estado original guardado antes de cada ajuste.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Revertir todos los cambios de Windows",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.RollbackAllWindowsChangesAsync(),
            "EVO - Error revirtiendo cambios de Windows");
    }

    private async void CaptureWindowsBaseline_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (sender is not FrameworkElement
            { Tag: WindowsOptimizationTarget target })
        {
            return;
        }

        var item = vm.WindowsOptimizationItems
            .FirstOrDefault(x => x.Target == target);

        string message =
            "¿Capturar la línea base de este ajuste?\n\n" +
            $"{item?.Title ?? target.ToString()}\n\n" +
            "La medición durará 30 s. Se registrarán CPU, GPU, RAM, Commit y " +
            "procesos. Si CS2 está abierto y PresentMon está disponible, " +
            "también se medirán FPS, 1% Low, 0.1% Low y frametimes.\n\n" +
            "Sin línea base no se puede aplicar el ajuste.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Línea base obligatoria",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.CaptureWindowsBaselineAsync(target),
            "EVO - Error capturando línea base");
    }

    private async void CaptureStartupBaseline_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (sender is not FrameworkElement { Tag: string valueName } ||
            string.IsNullOrWhiteSpace(valueName))
        {
            return;
        }

        string message =
            "¿Capturar la línea base de esta aplicación de inicio?\n\n" +
            $"Aplicación: {valueName}\n\n" +
            "La medición durará 30 s y registrará CPU, RAM, Commit y procesos. " +
            "Sin línea base no se puede desactivar la aplicación.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Línea base obligatoria",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.CaptureStartupBaselineAsync(valueName),
            "EVO - Error capturando línea base de inicio");
    }

    private async void EvaluateWindowsMeasurement_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        if (sender is not FrameworkElement { Tag: Guid changeId })
            return;

        var change = vm.WindowsChanges
            .FirstOrDefault(x => x.Id == changeId);

        string message =
            "¿Ejecutar la medición posterior?\n\n" +
            $"{change?.SettingKey ?? changeId.ToString()}\n\n" +
            "La medición durará 30 s y se comparará contra la línea base.\n\n" +
            "Si EVO detecta una regresión o un intercambio perjudicial, " +
            "revertirá el cambio automáticamente. Si falta evidencia, el " +
            "resultado quedará INCONCLUSO y no se conservará como mejora.";

        if (MessageBox.Show(
                Window.GetWindow(this),
                message,
                "EVO - Medición posterior obligatoria",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteMonitorOperationAsync(
            async model => await model.EvaluateWindowsMeasurementAsync(changeId),
            "EVO - Error evaluando la medición posterior");
    }

    private async Task ExecuteMonitorOperationAsync(
        Func<EvoControlCenterViewModel, Task> operation,
        string errorTitle)
    {
        if (_operationInProgress)
            return;

        if (DataContext is not EvoControlCenterViewModel vm)
            return;

        _operationInProgress = true;

        try
        {
            await operation(vm);
        }
        catch (OperationCanceledException)
        {
            // La cancelación solicitada no es un error de interfaz.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                errorTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _operationInProgress = false;
        }
    }
}
