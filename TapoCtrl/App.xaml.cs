using System.Windows;
using TapoCtrl.Services;
using TapoCtrl.Windows;

namespace TapoCtrl;

public partial class App : System.Windows.Application
{
    private MainWindow? _main;
    private SplashWindow? _splash;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            AppLog.Error("Unhandled UI exception", a.Exception);
            System.Windows.MessageBox.Show(a.Exception.Message, "TapoCtrl");
            a.Handled = true;
        };

        _splash = new SplashWindow();
        _splash.Show();
        _splash.UpdateLayout();

        _main = new MainWindow();
        _main.TrayRegistered += MainOnTrayRegistered;
        _main.Show();
    }

    private void MainOnTrayRegistered(object? sender, EventArgs e)
    {
        _splash?.Close();
        _splash = null;
    }
}
