using System.Windows;
using System.Windows.Controls;using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TapoCtrl.Models;
namespace TapoCtrl.Controls;
public partial class DevicePanel:System.Windows.Controls.UserControl
{
 private System.Windows.Point _dragPoint; private bool _dragging; private bool _movedDuringPress; private readonly DispatcherTimer _singleClickTimer=new(){Interval=TimeSpan.FromMilliseconds(260)};
 public DeviceSnapshot Device{get;private set;} public PanelGeometry Geometry{get;}
 public bool IsSelected{get;private set;}
 public event Action<DevicePanel>? GeometryChanged;
 public event Action<DeviceSnapshot>? OpenGraphRequested;
 public event Action<DeviceSnapshot>? DetailsRequested;
 public event Action<DeviceSnapshot,bool>? PowerRequested;
 public event Action<DevicePanel,bool>? SelectionRequested;
 public event Action<DevicePanel,double,double>? MoveRequested;
 public event Action<DevicePanel,double,double>? ResizeRequested;
 public event Action<DevicePanel,PanelLayoutCommand>? LayoutCommandRequested;
 public Func<DevicePanel,int>? SelectedPanelCountProvider;
 public DevicePanel(DeviceSnapshot d,PanelGeometry g)
 {
  InitializeComponent();Device=d;Geometry=g;_singleClickTimer.Tick+=SingleClickTimerTick;ApplyGeometry();UpdateDevice(d);
  NameText.MouseWheel+=NameWheel;
  ValueNumberText.MouseWheel+=ValueWheel;ValueUnitText.MouseWheel+=ValueWheel;
  SecondaryNumberText.MouseWheel+=ValueWheel;SecondaryUnitText.MouseWheel+=ValueWheel;
  SummaryText.MouseWheel+=ValueWheel;
  ResizeThumb.DragDelta+=Resize;
  RootBorder.PreviewMouseLeftButtonDown+=PanelDown;
  RootBorder.PreviewMouseMove+=PanelMove;
  RootBorder.PreviewMouseLeftButtonUp+=PanelUp;
  RootBorder.PreviewMouseRightButtonUp+=PanelRightClick;
  RootBorder.MouseRightButtonUp+=PanelRightClick;
  MouseDoubleClick+=DoubleClick;
  ContextMenu=BuildMenu();
 }
 public void UpdateDevice(DeviceSnapshot d)
 {
  Device=d;NameText.Text=d.Name;SummaryText.Visibility=Visibility.Collapsed;
  if(d.IsPowerSummary)
  {
   ValueNumberText.Text=$"{d.PowerWatts ?? 0:0}";ValueUnitText.Text="W";
   ValueNumberText.Foreground=ValueUnitText.Foreground=System.Windows.Media.Brushes.MediumPurple;
   SecondaryValuePanel.Visibility=Visibility.Collapsed;SummaryText.Visibility=Visibility.Visible;
   SummaryText.Text=$"使用電力 {d.PowerWatts ?? 0:0} W\n本日消費 {d.TodayWh ?? 0:N0} Wh\n概算 ¥{d.MonthWh ?? 0:N0}";
  }
  else if(d.Kind==DeviceKind.Environment)
  {
   ValueNumberText.Text=$"{d.TemperatureC ?? 0:0.0}";ValueUnitText.Text="℃";
   ValueNumberText.Foreground=ValueUnitText.Foreground=TemperatureBrush(d.TemperatureC);
   SecondaryNumberText.Text=$"{d.HumidityPercent ?? 0:0}";SecondaryUnitText.Text="%";
   SecondaryNumberText.Foreground=SecondaryUnitText.Foreground=HumidityBrush(d.HumidityPercent);SecondaryValuePanel.Visibility=Visibility.Visible;
  }
  else if(d.Kind==DeviceKind.Power)
  {
   ValueNumberText.Text=$"{d.PowerWatts ?? 0:0}";ValueUnitText.Text="W";
   ValueNumberText.Foreground=ValueUnitText.Foreground=ValueBrush(d);SecondaryValuePanel.Visibility=Visibility.Collapsed;
  }
  else if(d.Kind==DeviceKind.Switch)
  {
   ValueNumberText.Text=d.IsOn==true?"ON":"OFF";ValueUnitText.Text="";
   ValueNumberText.Foreground=ValueBrush(d);SecondaryValuePanel.Visibility=Visibility.Collapsed;
  }
  else
  {
   ValueNumberText.Text=d.CurrentValue;ValueUnitText.Text="";ValueNumberText.Foreground=ValueBrush(d);SecondaryValuePanel.Visibility=Visibility.Collapsed;
  }
  var supportsPower=!d.IsPowerSummary && (d.Kind is DeviceKind.Power or DeviceKind.Switch);
  PowerStateBorder.Visibility=supportsPower?Visibility.Visible:Visibility.Collapsed;
  if(supportsPower)
  {
   var on=d.IsOn==true;PowerStateText.Text=on?"ON":"OFF";
   PowerStateBorder.Background=new SolidColorBrush(on?System.Windows.Media.Color.FromRgb(58,166,95):System.Windows.Media.Color.FromRgb(205,75,82));
  }
  var stale=!d.Online||(DateTime.Now-d.Timestamp)>TimeSpan.FromMinutes(5);
  RootBorder.BorderBrush=new SolidColorBrush(stale?System.Windows.Media.Color.FromRgb(235,70,78):CategoryColor(d.GroupKind));
  RootBorder.Background=stale?new SolidColorBrush(System.Windows.Media.Color.FromRgb(74,31,36)):new SolidColorBrush(System.Windows.Media.Color.FromRgb(36,41,50));
  SubText.Text=d.IsPowerSummary?$"{d.Timestamp:yyyy/MM/dd HH:mm:ss}":$"{(d.Online?"Online":"Offline")}  {d.Timestamp:HH:mm:ss}";
 }
 public void SetSelected(bool selected)
 {
  IsSelected=selected;SelectionBadge.Visibility=selected?Visibility.Visible:Visibility.Collapsed;
  RootBorder.BorderThickness=selected?new Thickness(4):new Thickness(2);
  RootBorder.Effect=selected?new System.Windows.Media.Effects.DropShadowEffect{Color=System.Windows.Media.Colors.White,BlurRadius=8,Opacity=.35,ShadowDepth=0}:null;
 }
 private void DoubleClick(object s,MouseButtonEventArgs e)
 {
  _singleClickTimer.Stop();
  DetailsRequested?.Invoke(Device);
  e.Handled=true;
 }
 private void PanelDown(object s,MouseButtonEventArgs e)
 {
  if(e.ClickCount>=2)return;
  if(IsInsideResizeThumb(e.OriginalSource as DependencyObject))return;
  var ctrl=Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
  SelectionRequested?.Invoke(this,ctrl);
  if(ctrl){e.Handled=true;return;}
  _dragPoint=e.GetPosition(Parent as IInputElement ?? this);_dragging=true;_movedDuringPress=false;RootBorder.CaptureMouse();e.Handled=true;
 }
 private bool IsInsideResizeThumb(DependencyObject? element)
 {
  while(element is not null){if(ReferenceEquals(element,ResizeThumb))return true;element=VisualTreeHelper.GetParent(element);}
  return false;
 }
 private void PanelMove(object s,System.Windows.Input.MouseEventArgs e)
 {
  if(!_dragging||e.LeftButton!=MouseButtonState.Pressed)return;
  var parent=Parent as IInputElement;if(parent is null)return;
  var p=e.GetPosition(parent);var dx=p.X-_dragPoint.X;var dy=p.Y-_dragPoint.Y;
  if(Math.Abs(dx)<3&&Math.Abs(dy)<3)return;
  _movedDuringPress=true;_dragPoint=p;
  MoveRequested?.Invoke(this,dx,dy);
  e.Handled=true;
 }
 private void PanelUp(object s,MouseButtonEventArgs e)
 {
  if(!_dragging)return;
  _dragging=false;RootBorder.ReleaseMouseCapture();
  if(_movedDuringPress){GeometryChanged?.Invoke(this);}
  else if(IsGraphable(Device))
  {
   _singleClickTimer.Stop();
   _singleClickTimer.Start();
  }
  e.Handled=true;
 }
 private void SingleClickTimerTick(object? sender,EventArgs e)
 {
  _singleClickTimer.Stop();
  OpenGraphRequested?.Invoke(Device);
 }
 private static bool IsGraphable(DeviceSnapshot d)=>d.IsPowerSummary||d.Kind is DeviceKind.Power or DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity;
 private static System.Windows.Media.Color CategoryColor(DeviceGroupKind kind)=>kind switch{DeviceGroupKind.Power=>System.Windows.Media.Color.FromRgb(232,216,138),DeviceGroupKind.Environment=>System.Windows.Media.Color.FromRgb(198,166,232),_=>System.Windows.Media.Color.FromRgb(147,214,163)};
 private static System.Windows.Media.Brush ColorBrush(byte r,byte g,byte b)=>new SolidColorBrush(System.Windows.Media.Color.FromRgb(r,g,b));
 private static System.Windows.Media.Brush TemperatureBrush(double? value)
 {
  var v=value??0;
  if(v<=0)return System.Windows.Media.Brushes.White;
  if(v>=35)return ColorBrush(194,24,91);        // 濃い赤紫
  if(v>=30)return ColorBrush(255,75,60);       // 赤
  if(v>=25)return ColorBrush(255,214,31);      // 黄色
  if(v>=20)return ColorBrush(136,246,157);     // 緑
  if(v>=15)return ColorBrush(92,210,255);      // 薄い青
  return ColorBrush(32,96,255);                // 濃い青
 }
 private static System.Windows.Media.Brush HumidityBrush(double? value)
 {
  var v=value??0;
  if(v>=80)return ColorBrush(255,75,60);        // 赤
  if(v>=60)return ColorBrush(255,214,31);       // 黄色
  if(v>=40)return ColorBrush(136,246,157);      // 緑
  if(v>=20)return ColorBrush(92,210,255);       // 薄い青
  return ColorBrush(32,96,255);                 // 濃い青
 }
 private static System.Windows.Media.Brush PowerBrush(double? value)
 {
  var v=value??0;
  if(v>=1000)return ColorBrush(110,44,255);     // 濃い紫
  if(v>=800)return ColorBrush(255,75,60);       // 赤
  if(v>=400)return ColorBrush(255,214,31);      // 黄色
  if(v>=100)return ColorBrush(190,255,72);      // 黄緑
  return ColorBrush(36,255,96);                 // 緑
 }
 private static System.Windows.Media.Brush ValueBrush(DeviceSnapshot d)=>d.Kind switch{DeviceKind.Temperature=>TemperatureBrush(d.TemperatureC),DeviceKind.Humidity=>HumidityBrush(d.HumidityPercent),DeviceKind.Power=>PowerBrush(d.PowerWatts),DeviceKind.Switch when d.IsOn==true=>System.Windows.Media.Brushes.LightGreen,DeviceKind.Switch=>System.Windows.Media.Brushes.LightCoral,_=>System.Windows.Media.Brushes.White};
 public void ApplyGeometry()
 {
  var minHeight=MinimumPanelHeight();
  Width=Math.Max(80,Geometry.Width);Height=Math.Max(minHeight,Geometry.Height);
  var impactNumberPanel=Device.IsPowerSummary || Device.Kind is DeviceKind.Power or DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity;
  var numberFont=new System.Windows.Media.FontFamily(impactNumberPanel?"Impact":Geometry.FontFamily);
  ValueNumberText.FontFamily=numberFont;ValueNumberText.FontSize=Geometry.FontSize;
  ValueUnitText.FontFamily=new System.Windows.Media.FontFamily(Geometry.FontFamily);ValueUnitText.FontSize=Math.Max(10,Geometry.FontSize*.5);
  SecondaryNumberText.FontFamily=numberFont;SecondaryNumberText.FontSize=Math.Clamp(Geometry.SecondaryFontSize,9,96);
  SecondaryUnitText.FontFamily=ValueUnitText.FontFamily;SecondaryUnitText.FontSize=Math.Max(8,SecondaryNumberText.FontSize*.5);
  NameText.FontFamily=ValueNumberText.FontFamily;NameText.FontSize=Math.Clamp(Geometry.TitleFontSize,9,48);
  RootBorder.Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(Geometry.Background)!;
  NameText.Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(Geometry.TitleForeground)!;
  Visibility=Geometry.Visible?Visibility.Visible:Visibility.Collapsed;
  Canvas.SetLeft(this,Geometry.X);Canvas.SetTop(this,Geometry.Y);
 }
 private double MinimumPanelHeight()
 {
  // 手動レイアウト時に縦幅をかなり小さくできるよう、種類別の強い下限制限を外す。
  // 内容が入りきらない場合はRootBorder側でクリップされ、後から再拡大できる。
  return Device.Kind==DeviceKind.Switch ? 28d : 34d;
 }

 private void Resize(object s,DragDeltaEventArgs e)
 {
  var minHeight=MinimumPanelHeight();
  Geometry.Width=Math.Max(80,Geometry.Width+e.HorizontalChange);
  Geometry.Height=Math.Max(minHeight,Geometry.Height+e.VerticalChange);
  ApplyGeometry();ResizeRequested?.Invoke(this,Geometry.Width,Geometry.Height);GeometryChanged?.Invoke(this);
 }
 private void NameWheel(object s,MouseWheelEventArgs e)
 {
  if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
  {
   var colors=new[]{"#B8C1CC","#7CFFC8","#FFD166","#66B3FF","#FF7AAC","#FFFFFF"};var i=Array.IndexOf(colors,Geometry.TitleForeground);if(i<0)i=0;Geometry.TitleForeground=colors[(i+(e.Delta>0?1:colors.Length-1))%colors.Length];
  }
  else Geometry.TitleFontSize=Math.Clamp(Geometry.TitleFontSize+(e.Delta>0?1:-1),9,48);
  ApplyGeometry();GeometryChanged?.Invoke(this);e.Handled=true;
 }
 private void ValueWheel(object s,MouseWheelEventArgs e)
 {
  var delta=e.Delta>0?2:-2;
  if(ReferenceEquals(s,SecondaryNumberText)||ReferenceEquals(s,SecondaryUnitText))
   Geometry.SecondaryFontSize=Math.Clamp(Geometry.SecondaryFontSize+delta,9,96);
  else
   Geometry.FontSize=Math.Clamp(Geometry.FontSize+delta,9,96);
  ApplyGeometry();GeometryChanged?.Invoke(this);e.Handled=true;
 }
 private void PanelRightClick(object s,MouseButtonEventArgs e)
 {
  // 未選択パネルを右クリックした場合は、そのパネルを操作対象にする。
  if(!IsSelected)SelectionRequested?.Invoke(this,false);
  ContextMenu=BuildMenu();
  ContextMenu.PlacementTarget=this;
  ContextMenu.IsOpen=true;
  e.Handled=true;
 }
 private ContextMenu BuildMenu()
 {
  var m=new ContextMenu();
  var selectedCount=SelectedPanelCountProvider?.Invoke(this)??(IsSelected?1:0);
  if(selectedCount>=2)
  {
   void AddLayout(string header,PanelLayoutCommand command)
   {
    var item=new MenuItem{Header=header};
    item.Click+=(_,__)=>LayoutCommandRequested?.Invoke(this,command);
    m.Items.Add(item);
   }
   AddLayout("選択パネルの上端を合わす",PanelLayoutCommand.AlignTop);
   AddLayout("選択パネルの左端を合わす",PanelLayoutCommand.AlignLeft);
   AddLayout("選択パネルの右端を合わす",PanelLayoutCommand.AlignRight);
   AddLayout("選択パネルの下端を合わす",PanelLayoutCommand.AlignBottom);
   AddLayout("選択パネルの縦幅を合わす",PanelLayoutCommand.MatchHeight);
   AddLayout("選択パネルの横幅を合わす",PanelLayoutCommand.MatchWidth);
   AddLayout("選択パネルを均等に割り振る",PanelLayoutCommand.DistributeEvenly);
   m.Items.Add(new Separator());
  }
  var hide=new MenuItem{Header="このデバイスパネルを表示／非表示"};
  hide.Click+=(_,__)=>{Geometry.Visible=false;ApplyGeometry();GeometryChanged?.Invoke(this);};m.Items.Add(hide);
  if(!Device.IsPowerSummary && (Device.Kind is DeviceKind.Power or DeviceKind.Switch)){m.Items.Add(new Separator());var on=new MenuItem{Header="電源 ON"};on.Click+=(_,__)=>PowerRequested?.Invoke(Device,true);m.Items.Add(on);var off=new MenuItem{Header="電源 OFF"};off.Click+=(_,__)=>PowerRequested?.Invoke(Device,false);m.Items.Add(off);}return m;
 }
}

public enum PanelLayoutCommand
{
 AlignTop,
 AlignLeft,
 AlignRight,
 AlignBottom,
 MatchHeight,
 MatchWidth,
 DistributeEvenly
}
