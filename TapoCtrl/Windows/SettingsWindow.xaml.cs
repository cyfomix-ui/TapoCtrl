using System.Windows;using TapoCtrl.Models;using TapoCtrl.Services;
namespace TapoCtrl.Windows;
public partial class SettingsWindow:Window
{
 private readonly AppSettings _settings;private readonly SettingsService _service;
 public SettingsWindow(AppSettings settings,SettingsService service){InitializeComponent();_settings=settings;_service=service;var c=service.LoadSecret();UserBox.Text=c.User;PassBox.Password=c.Pass;HubBox.Text=string.Join(", ",settings.HubIps);PythonBox.Text=settings.PythonPath;}
 private void SaveClick(object s,RoutedEventArgs e){_settings.HubIps=HubBox.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToList();_settings.PythonPath=string.IsNullOrWhiteSpace(PythonBox.Text)?"python":PythonBox.Text.Trim();_service.SaveSecret(UserBox.Text.Trim(),PassBox.Password);_service.Save(_settings);DialogResult=true;}
}
