using System.Windows;
using System.Windows.Controls;
using EvoOptimizer.App.ViewModels;
using EvoOptimizer.Core.Gaming;
using Microsoft.Win32;

namespace EvoOptimizer.App.Views;

public partial class EvoGamingOptimizerPanel : UserControl
{
    /// <summary>
    /// Handoff Módulo 3: el shell enruta juego + perfil al Game Booster y
    /// cambia a su pestaña. El Optimizer nunca aplica cambios por su cuenta.
    /// </summary>
    public event Action<InstalledGame?, GamingProfileKind>? BoostRequested;

    public EvoGamingOptimizerPanel()
    {
        InitializeComponent();
    }

    private GamingOptimizerViewModel? ViewModel => DataContext as GamingOptimizerViewModel;

    private async void AnalyzeEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        try
        {
            await viewModel.RefreshEnvironmentAsync();
        }
        catch (Exception ex)
        {
            ShowError("EVO · Análisis del entorno", ex);
        }
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        try
        {
            await viewModel.ScanGamesAsync();
        }
        catch (Exception ex)
        {
            ShowError("EVO · Detección de juegos", ex);
        }
    }

    private async void AddManualGame_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Seleccioná el ejecutable del juego",
            Filter = "Ejecutables (*.exe)|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            InstalledGame? game = await viewModel.AddManualGameAsync(dialog.FileName);

            if (game is null)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    "El archivo seleccionado no es un ejecutable válido.",
                    "EVO · Juego manual",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowError("EVO · Juego manual", ex);
        }
    }

    private async void RemoveManualGame_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        if (viewModel.SelectedGame is not { Launcher: GameLauncher.Manual } game)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Sólo se pueden quitar juegos agregados manualmente. Los juegos de Steam/Epic se detectan automáticamente.",
                "EVO · Juego manual",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                Window.GetWindow(this),
                $"¿Quitar '{game.Name}' de la lista manual? No se desinstala nada.",
                "EVO · Quitar juego manual",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            await viewModel.RemoveSelectedManualGameAsync();
        }
        catch (Exception ex)
        {
            ShowError("EVO · Juego manual", ex);
        }
    }

    private void PrepareBoost_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        GamingProfileKind kind = viewModel.SelectedProfile?.Kind ?? GamingProfileKind.Balanced;
        BoostRequested?.Invoke(viewModel.SelectedGame, kind);
    }

    private void ShowError(string title, Exception ex) =>
        MessageBox.Show(
            Window.GetWindow(this),
            ex.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}