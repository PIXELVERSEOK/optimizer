using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;

namespace EvoOptimizer.App.Views;

/// <summary>
/// Panel de escáner, tweaks de registro y debloat. Toda escritura requiere
/// confirmación explícita del usuario antes de invocar al servicio.
/// </summary>
public partial class EvoTweakPanel : UserControl
{
    public EvoTweakPanel()
    {
        InitializeComponent();
    }

    private EvoControlCenterViewModel? ViewModel => DataContext as EvoControlCenterViewModel;

    // ── SCANNER ──────────────────────────────────────────────────────────────

    private async void ScannerRun_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RunScannerAsync();
    }

    private async void ScannerExport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        string message = await ViewModel.ExportScannerReportAsync();

        Info(message, "Informe del escáner");
    }

    // ── TWEAKS ───────────────────────────────────────────────────────────────

    private async void TweakScan_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.ScanTweaksAsync();
    }

    private async void TweakApply_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { DataContext: TweakRow row })
        {
            return;
        }

        string detail =
            $"TWEAK: {row.Name}\n\n" +
            $"Qué hace: {row.Description}\n" +
            $"Ubicación: {row.RegistryLocation}\n" +
            $"Valor actual: {row.CurrentValueLabel}\n" +
            $"{row.DesiredLabel}\n" +
            $"Beneficio: {row.Benefit}\n" +
            $"Riesgo: {row.RiskLabel} · {row.ElevationLabel} · {row.RebootLabel}\n\n" +
            "EVO guarda el valor original en el diario antes de escribir y relee para " +
            "verificar. Si algo no coincide, restaura el estado original.\n\n" +
            "¿Aplicar este tweak?";

        if (!Confirm(detail, "Aplicar tweak"))
        {
            return;
        }

        Info(await ViewModel.ApplyTweakAsync(row.Id), "Resultado del tweak");
    }

    private async void TweakRollback_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { DataContext: TweakRow row })
        {
            return;
        }

        if (!Confirm($"¿Revertir «{row.Name}» al valor original registrado por EVO?\n" +
                     $"Ubicación: {row.RegistryLocation}", "Revertir tweak"))
        {
            return;
        }

        Info(await ViewModel.RollbackTweakAsync(row.Id), "Resultado del rollback");
    }

    private async void TweakApplyAllLowRisk_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!Confirm(
            "Se aplicarán SÓLO los tweaks de riesgo bajo actualmente disponibles " +
            "(los HKLM pedirán autorización UAC).\n\n" +
            "Antes de aplicar, EVO captura una línea base si hay sensores disponibles.\n\n" +
            "¿Continuar?", "Aplicar tweaks de riesgo bajo"))
        {
            return;
        }

        Info(await ViewModel.ApplyAllLowRiskTweaksAsync(), "Aplicación en lote");
    }

    private async void TweakRollbackAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!Confirm(
            "Se revertirán TODOS los cambios de tweaks registrados por EVO, en orden " +
            "inverso, restaurando cada valor original.", "Revertir todos los tweaks"))
        {
            return;
        }

        Info(await ViewModel.RollbackAllTweaksAsync(), "Rollback completo");
    }

    // ── DEBLOAT ──────────────────────────────────────────────────────────────

    private async void DebloatScan_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.ScanDebloatAsync();
    }

    private async void DebloatRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { DataContext: DebloatRow row })
        {
            return;
        }

        if (!Confirm(
            $"APP: {row.Name}\nPaquete: {row.PackageLabel}\nVersión: {row.VersionLabel}\n" +
            $"Por qué se ofrece: {row.Reason}\nBeneficio: {row.Benefit}\nRiesgo: {row.RiskLabel}\n\n" +
            "El quite es sólo para este usuario y EVO guarda PackageFullName e " +
            "InstallLocation para poder restaurarlo.\n\n" +
            $"¿Quitar «{row.Name}»?", "Quitar aplicación UWP"))
        {
            return;
        }

        Info(await ViewModel.RemoveDebloatAppAsync(row.Id), "Resultado del debloat");
    }

    private async void DebloatRestore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { DataContext: DebloatRow row })
        {
            return;
        }

        if (!Confirm($"¿Volver a registrar «{row.Name}» desde su AppxManifest?", "Restaurar aplicación"))
        {
            return;
        }

        Info(await ViewModel.RestoreDebloatAppAsync(row.Id), "Resultado de la restauración");
    }

    // ── Diálogo ──────────────────────────────────────────────────────────────

    private static bool Confirm(string message, string title) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private static void Info(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
