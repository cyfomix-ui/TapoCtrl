using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using TapoCtrl.Models;
namespace TapoCtrl.Windows;
public partial class SwitchTrayWindow : Window
{
 private IReadOnlyList<DeviceSnapshot> _devices=[];
 private readonly Func<DeviceSnapshot,bool,Task> _setPower;
 private bool _allowAutoClose; private bool _closingByButton; private DateTime _suppressAutoCloseUntil=DateTime.MinValue;
 public event Action? RefreshRequested;
 public SwitchTrayWindow(Func<DeviceSnapshot,bool,Task> setPower)
 {
  InitializeComponent();_setPower=setPower;
  PreviewKeyDown+=SwitchTrayPreviewKeyDown;
  SourceInitialized+=(_,__)=>HideFromAltTab();
  Loaded+=async(_,__)=>{MaxHeight=Math.Max(240,SystemParameters.WorkArea.Height-24);await Task.Delay(350);_allowAutoClose=true;};
  Deactivated+=(_,__)=>
  {
   if(!_allowAutoClose||_closingByButton)return;
   if(DateTime.Now<_suppressAutoCloseUntil)
   {
    Dispatcher.BeginInvoke(new Action(()=>{try{if(IsVisible&&!_closingByButton){Activate();Focus();}}catch{}}));
    return;
   }
   Close();
  };
 }
 private void SwitchTrayPreviewKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
 {
  if(e.Key==System.Windows.Input.Key.F5)
  {
   e.Handled=true;
   RefreshRequested?.Invoke();
  }
 }
 private void HideFromAltTab()
 {
  var hwnd=new WindowInteropHelper(this).Handle;
  if(hwnd==IntPtr.Zero)return;
  const int GWL_EXSTYLE=-20;
  const int WS_EX_TOOLWINDOW=0x00000080;
  const int WS_EX_APPWINDOW=0x00040000;
  var style=NativeMethods.GetWindowLong(hwnd,GWL_EXSTYLE);
  style|=WS_EX_TOOLWINDOW;
  style&=~WS_EX_APPWINDOW;
  NativeMethods.SetWindowLong(hwnd,GWL_EXSTYLE,style);
 }
 public void SetDevices(IEnumerable<DeviceSnapshot> devices){_devices=devices.Where(d=>(d.Kind==DeviceKind.Switch||d.Kind==DeviceKind.Power)&&!d.IsPowerSummary&&d.IsOn is not null).OrderBy(d=>d.Kind==DeviceKind.Switch?0:1).ThenBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).ToList();Render();}
 private void Render()
 {
  ItemsHost.Children.Clear();
  if(_devices.Count==0){ItemsHost.Children.Add(new TextBlock{Text="ON/OFFできるデバイスがありません",Foreground=WpfBrushes.LightGray,Margin=new Thickness(10),FontSize=14});return;}
  foreach(var device in _devices)
  {
   var online=device.Online;var on=device.IsOn==true;var canControl=!string.IsNullOrWhiteSpace(device.Ip);
   var border=new Border{Background=new SolidColorBrush(WpfColor.FromRgb(29,34,41)),BorderBrush=new SolidColorBrush(on?WpfColor.FromRgb(91,214,145):WpfColor.FromRgb(224,105,111)),BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(6),Margin=new Thickness(1,3,1,3),Padding=new Thickness(12,9,10,9),Cursor=canControl?WpfCursors.Hand:WpfCursors.Arrow,Tag=device};
   var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var displayName=device.Kind==DeviceKind.Power&&device.PowerWatts is not null?$"{device.Name}  {device.PowerWatts:0}W":device.Name;
   var name=new TextBlock{Text=displayName,Foreground=WpfBrushes.WhiteSmoke,FontSize=16,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,ToolTip=online?null:"監視通信は未取得ですが、IP指定の独立コマンドで操作できます"};
   var stateText=canControl?(on?"ON":"OFF"):"--";
   var state=new Border{Background=new SolidColorBrush(canControl?(on?WpfColor.FromRgb(42,177,102):WpfColor.FromRgb(202,67,76)):WpfColor.FromRgb(80,85,94)),CornerRadius=new CornerRadius(10),Padding=new Thickness(9,2,9,2),Margin=new Thickness(8,0,0,0),Child=new TextBlock{Text=stateText,Foreground=WpfBrushes.White,FontWeight=FontWeights.Bold,FontSize=12}};
   Grid.SetColumn(state,1);grid.Children.Add(name);grid.Children.Add(state);border.Child=grid;
   border.PreviewMouseDown+=(_,__)=>_suppressAutoCloseUntil=DateTime.Now.AddSeconds(2);
   border.MouseLeftButtonUp+=async(_,e)=>{e.Handled=true;_suppressAutoCloseUntil=DateTime.Now.AddSeconds(2);if(!canControl)return;border.IsEnabled=false;try{await _setPower(device,!on);}finally{border.IsEnabled=true;if(IsVisible){Activate();Focus();}}};
   ItemsHost.Children.Add(border);
  }
 }
 private void Close_Click(object sender,RoutedEventArgs e){_closingByButton=true;_allowAutoClose=false;Close();}

 static class NativeMethods
 {
  [System.Runtime.InteropServices.DllImport("user32.dll")]
  public static extern int GetWindowLong(IntPtr hWnd,int nIndex);
  [System.Runtime.InteropServices.DllImport("user32.dll")]
  public static extern int SetWindowLong(IntPtr hWnd,int nIndex,int dwNewLong);
 }
}
