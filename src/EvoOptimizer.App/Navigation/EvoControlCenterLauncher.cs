using System.Windows;
using EvoOptimizer.App.Views;

namespace EvoOptimizer.App.Navigation;

public sealed class EvoControlCenterLauncher
{
    private EvoControlCenterWindow? _window;

    public void Open(Window? owner = null)
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;

            _window.Activate();
            return;
        }

        _window = new EvoControlCenterWindow();

        if (owner is not null)
            _window.Owner = owner;

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
