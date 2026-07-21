using System.Reflection;
using System.Windows;

namespace TapoCtrl.Windows;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.01";
        var suffixIndex = version.IndexOf('+');
        VersionText.Text = "Ver " + (suffixIndex >= 0 ? version[..suffixIndex] : version);
    }
}
