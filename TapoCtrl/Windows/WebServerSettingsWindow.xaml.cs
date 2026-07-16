using System.Windows;
using TapoCtrl.Models;
using TapoCtrl.Services;

namespace TapoCtrl.Windows;
public partial class WebServerSettingsWindow:Window
{
 private readonly AppSettings _settings;
 public WebServerSettingsWindow(AppSettings settings)
 {
  InitializeComponent();
  _settings=settings;
  EnabledCheck.IsChecked=settings.HttpEnabled;
  var bind=LocalHttpService.NormalizeBind(settings.HttpBind);
  BindBox.Text=bind;
  foreach(var obj in BindBox.Items){ if(obj is System.Windows.Controls.ComboBoxItem item && string.Equals(item.Content?.ToString(), bind, StringComparison.OrdinalIgnoreCase)){ BindBox.SelectedItem=item; break; } }
  PortBox.Text=settings.HttpPort.ToString();
  StatusText.Text=BuildStatus(settings.HttpPort, bind);
 }
 private static string BuildStatus(int port,string bind)
 {
  var addresses=new List<string>();
  if(bind=="127.0.0.1")
  {
   addresses.Add($"http://localhost:{port}/");
   addresses.Add($"http://127.0.0.1:{port}/");
   addresses.Add("※現在はこのPCからだけ接続できます。LAN端末から使う場合は Bind を 0.0.0.0 にしてください。");
   return string.Join(Environment.NewLine,addresses.Distinct());
  }
  addresses.Add($"http://localhost:{port}/");
  addresses.Add($"http://127.0.0.1:{port}/");
  foreach(var ip in LocalHttpService.GetLocalIPv4()) addresses.Add($"http://{ip}:{port}/");
  addresses.Add("※他PC/スマホから見えない場合は Windows Defender Firewall と URL予約を設定してください。Allow_TapoCtrl_WebServer_Firewall.ps1 を管理者PowerShellで実行します。");
  return string.Join(Environment.NewLine,addresses.Distinct());
 }
 private void Save_Click(object sender,RoutedEventArgs e)
 {
  if(!int.TryParse(PortBox.Text,out var port)||port is <1 or >65535){System.Windows.MessageBox.Show("Portは1～65535で入力してください。","TapoCtrl");return;}
  var bind=LocalHttpService.NormalizeBind(GetComboText(BindBox));
  _settings.HttpEnabled=EnabledCheck.IsChecked==true;
  _settings.HttpBind=bind;
  _settings.HttpPort=port;
  DialogResult=true;
 }
 private static string GetComboText(System.Windows.Controls.ComboBox combo)
 {
  if(combo.SelectedItem is System.Windows.Controls.ComboBoxItem item) return item.Content?.ToString() ?? string.Empty;
  return combo.Text ?? string.Empty;
 }
 private void Cancel_Click(object sender,RoutedEventArgs e)=>DialogResult=false;
}
