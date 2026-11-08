using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Core.Gaming;

namespace EvoOptimizer.App.Views;

public partial class EvoGameBoosterPanel : UserControl
{
    public EvoGameBoosterPanel()
    {
        InitializeComponent();
    }

    private GameBoosterViewModel? ViewModel => DataContext as GameBoosterViewModel;

    /// <summary>Handoff desde el Game Optimizer (Módulo 3): precarga juego + perfil.</summary>
    public void PrepareBoost(InstalledGame? game, GamingProfileKind profileKind) =>
        ViewModel?.PrepareBoost(game, profileKind);

    private async void RefreshCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        try
        {
            await viewModel.RefreshCandidatesAsync();
        }
        catch (Exception ex)
        {
            ShowError("EVO · Candidatos de Boost", ex);
        }
    }

    private async void BoostNow_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            "EVO aplicará ÚNICAMENTE estas acciones autorizadas:" + Environment.NewLine + Environment.NewLine +
            viewModel.BuildConfirmationSummary() + Environment.NewLine + Environment.NewLine +
            "¿Confirmás el Boost?",
            "EVO · Confirmar BOOST NOW",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await viewModel.BoostNowAsync();
        }
        catch (Exception ex)
        {
            ShowError("EVO · BOOST NOW", ex);
        }
    }

    private async void RestoreSystem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            "Se revertirán todos los cambios de la sesión de Boost en orden inverso: " +
            "plan de energía, ajustes de registro, prioridades y afinidades. " +
            "Las apps cerradas sólo se relanzan si lo autorizaste." + Environment.NewLine + Environment.NewLine +
            "¿Restaurar el sistema ahora?",
            "EVO · Confirmar RESTORE SYSTEM",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await viewModel.RestoreSystemAsync();
        }
        catch (Exception ex)
        {
            ShowError("EVO · RESTORE SYSTEM", ex);
        }
    }

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show(
            Window.GetWindow(this),
            ex.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}