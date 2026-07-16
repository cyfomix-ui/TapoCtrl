using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TapoCtrl.Models;
namespace TapoCtrl.Windows;
public partial class DeviceDetailsWindow:Window
{
 public string DeviceKey{get;}
 public DeviceDetailsWindow(DeviceSnapshot d)
 {
  InitializeComponent();
  DeviceKey=d.Id;
  Title=$"TapoCtrl - {d.Name} - 詳細";
  HeaderText.Text=d.Name;
  string power=d.PowerWatts is null?"--":$"{d.PowerWatts:0} W";
  string today=d.TodayWh is null?"--":$"{d.TodayWh:0} Wh";
  string month=d.MonthWh is null?"--":$"{d.MonthWh:0} Wh";
  string temp=d.TemperatureC is null?"--":$"{d.TemperatureC:0.0} ℃";
  string hum=d.HumidityPercent is null?"--":$"{d.HumidityPercent:0} %";
  string state=d.IsOn is null?"--":(d.IsOn==true?"ON":"OFF");
  DetailsList.Items.Add(new Row("名前",d.Name));
  DetailsList.Items.Add(new Row("ID",d.Id));
  DetailsList.Items.Add(new Row("種類",d.Kind.ToString()));
  DetailsList.Items.Add(new Row("状態",d.Online?"Online":"Offline"));
  DetailsList.Items.Add(new Row("電源",state));
  DetailsList.Items.Add(new Row("現在電力",power));
  DetailsList.Items.Add(new Row("本日消費",today));
  DetailsList.Items.Add(new Row("月間消費",month));
  DetailsList.Items.Add(new Row("温度",temp));
  DetailsList.Items.Add(new Row("湿度",hum));
  DetailsList.Items.Add(new Row("IP",d.Ip));
  DetailsList.Items.Add(new Row("Hub",d.Hub));
  DetailsList.Items.Add(new Row("Model",d.Model));
  DetailsList.Items.Add(new Row("更新",d.Timestamp.ToString("yyyy/MM/dd HH:mm:ss")));
 }
 private void DetailsList_PreviewMouseRightButtonDown(object sender,MouseButtonEventArgs e)
 {
  var item=FindParent<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
  var row=item?.Content as Row ?? item?.DataContext as Row;
  if(item is null || row is null) return;
  item.IsSelected=true;
  DetailsList.SelectedItem=row;
  try
  {
   System.Windows.Clipboard.SetText(row.Value??string.Empty);
  }
  catch
  {
   // Clipboardが一時的に他プロセスで使用中の場合は、画面操作を妨げない。
  }
  e.Handled=true;
 }
 private static T? FindParent<T>(DependencyObject? source) where T:DependencyObject
 {
  while(source is not null)
  {
   if(source is T found) return found;
   source=VisualTreeHelper.GetParent(source);
  }
  return null;
 }
 private void Close_Click(object sender,RoutedEventArgs e)=>Close();
 private sealed record Row(string Name,string Value);
}
