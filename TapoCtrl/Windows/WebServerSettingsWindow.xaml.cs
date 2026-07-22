using System.Windows;
using System.Windows.Controls;
using TapoCtrl.Models;
using TapoCtrl.Services;

namespace TapoCtrl.Windows;
public partial class WebServerSettingsWindow:Window
{
 private readonly AppSettings _settings;
 public WebServerSettingsWindow(AppSettings settings)
 {
  InitializeComponent();_settings=settings;
  EnabledCheck.IsChecked=settings.HttpEnabled;var bind=LocalHttpService.NormalizeBind(settings.HttpBind);BindBox.Text=bind;
  foreach(var obj in BindBox.Items)if(obj is ComboBoxItem item&&string.Equals(item.Content?.ToString(),bind,StringComparison.OrdinalIgnoreCase)){BindBox.SelectedItem=item;break;}
  PortBox.Text=settings.HttpPort.ToString();
  StatusText.Text=BuildStatus(settings.HttpPort,bind);
 }
 private static string BuildStatus(int port,string bind)
 {
  var host=bind=="127.0.0.1"?"localhost":LocalHttpService.GetLocalIPv4().FirstOrDefault()??"localhost";
  return $"管理 http://{host}:{port}/Ctrl/\n閲覧 http://{host}:{port}/View/\nルート http://{host}:{port}/ は NoService";
 }
 private void Save_Click(object sender,RoutedEventArgs e)
 {
  if(!int.TryParse(PortBox.Text,out var port)||port is <1 or >65535){System.Windows.MessageBox.Show("Portは1～65535で入力してください。","TapoCtrl");return;}
  _settings.HttpEnabled=EnabledCheck.IsChecked==true;_settings.HttpBind=LocalHttpService.NormalizeBind(GetComboText(BindBox));_settings.HttpPort=port;
  DialogResult=true;
 }
 private static string GetComboText(System.Windows.Controls.ComboBox combo)=>combo.SelectedItem is ComboBoxItem item?item.Content?.ToString()??string.Empty:combo.Text??string.Empty;
 private void Cancel_Click(object sender,RoutedEventArgs e)=>DialogResult=false;
}