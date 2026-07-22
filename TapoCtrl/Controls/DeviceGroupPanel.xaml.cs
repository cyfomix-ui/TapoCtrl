using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TapoCtrl.Models;
namespace TapoCtrl.Controls;
public partial class DeviceGroupPanel : System.Windows.Controls.UserControl
{
 private System.Windows.Point _down; private bool _drag; private readonly List<DevicePanel> _children=[];
 public GroupGeometry Geometry { get; } public DeviceGroupKind Kind => Geometry.Kind;
 public event Action<DeviceGroupPanel>? GeometryChanged;
 public event Action<DeviceGroupPanel>? SeriesGraphRequested;
 public DeviceGroupPanel(GroupGeometry geometry)
 {
  InitializeComponent();Geometry=geometry;ApplyGeometry();
  HeaderBorder.PreviewMouseLeftButtonDown+=Down;HeaderBorder.PreviewMouseMove+=Move;HeaderBorder.PreviewMouseLeftButtonUp+=Up;
  GraphButton.Visibility=Kind==DeviceGroupKind.Switch?Visibility.Collapsed:Visibility.Visible;GraphButton.Click+=(_,e)=>{SeriesGraphRequested?.Invoke(this);e.Handled=true;};CollapseButton.Click+=(_,e)=>{ToggleCollapsed();e.Handled=true;};
  ResizeThumb.DragDelta+=Resize;HeaderBorder.ContextMenu=BuildMenu();
 }
 public void SetChildren(IEnumerable<DevicePanel> panels)
 {
  var list=panels.ToList();_children.Clear();_children.AddRange(list);ItemsHost.Children.Clear();
  var usable=Math.Max(300,Geometry.Width-38);double x=5d;double y=5d;double rowHeight=0d;
  var summary=list.FirstOrDefault(p=>p.Device.IsPowerSummary);
  if(summary is not null)
  {
   // 使用電力計は左端だけ固定し、保存された縦位置は維持する。
   summary.Geometry.X=5;summary.Geometry.Y=Math.Max(0,summary.Geometry.Y);
   summary.ApplyGeometry();
   if(summary.Parent is System.Windows.Controls.Panel sp)sp.Children.Remove(summary);
   ItemsHost.Children.Add(summary);
   x=summary.Geometry.Width+20;y=5;rowHeight=Math.Max(rowHeight,summary.Geometry.Height);
  }
  foreach(var panel in list.Where(p=>!p.Device.IsPowerSummary))
  {
   if(panel.Parent is System.Windows.Controls.Panel parent)parent.Children.Remove(panel);
   var offscreen=panel.Geometry.X>Math.Max(usable*2,Geometry.Width*2)||panel.Geometry.Y>Math.Max(ItemsHost.Height+Geometry.Height,Geometry.Height*3);
   var unplaced=panel.Geometry.X<=20&&panel.Geometry.Y<=20;
   if(unplaced||offscreen){if(x+panel.Geometry.Width>usable){x=summary is not null?summary.Geometry.Width+20:5;y+=rowHeight+10;rowHeight=0;}panel.Geometry.X=x;panel.Geometry.Y=y;x+=panel.Geometry.Width+10;rowHeight=Math.Max(rowHeight,panel.Geometry.Height);}
   panel.ApplyGeometry();ItemsHost.Children.Add(panel);
  }
  CountText.Text=$"{list.Count} devices";
  RefreshExtent();
 }
 public void ApplyGeometry()
 {
  TitleText.Text=string.IsNullOrWhiteSpace(Geometry.Title)?DefaultTitle(Kind):Geometry.Title;var c=GroupColor(Kind);GroupBorder.BorderBrush=new SolidColorBrush(c);HeaderBorder.Background=new SolidColorBrush(c);
  Width=Math.Max(260,Geometry.Width);
  Height=Geometry.Collapsed?32:Math.Max(58,Geometry.Height);
  ItemsScrollViewer.Visibility=Geometry.Collapsed?Visibility.Collapsed:Visibility.Visible;
  ResizeThumb.Visibility=Geometry.Collapsed?Visibility.Collapsed:Visibility.Visible;
  Visibility=Geometry.Visible?Visibility.Visible:Visibility.Collapsed;Canvas.SetLeft(this,Geometry.X);Canvas.SetTop(this,Geometry.Y);
 }
 private static string DefaultTitle(DeviceGroupKind kind)=>kind switch{DeviceGroupKind.Power=>"電力系",DeviceGroupKind.Environment=>"温度・湿度系",_=>"スイッチ系"};
 private static System.Windows.Media.Color GroupColor(DeviceGroupKind kind)=>kind switch{DeviceGroupKind.Power=>System.Windows.Media.Color.FromRgb(232,216,138),DeviceGroupKind.Environment=>System.Windows.Media.Color.FromRgb(198,166,232),_=>System.Windows.Media.Color.FromRgb(147,214,163)};
 private void ToggleCollapsed()
 {
  if(!Geometry.Collapsed)Geometry.ExpandedHeight=Geometry.Height;
  Geometry.Collapsed=!Geometry.Collapsed;
  if(!Geometry.Collapsed&&Geometry.ExpandedHeight>32)Geometry.Height=Geometry.ExpandedHeight;
  CollapseButton.Content=Geometry.Collapsed?"▶":"▼";
  ApplyGeometry();GeometryChanged?.Invoke(this);
 }
 private void HeaderDoubleClick(object s,MouseButtonEventArgs e)
 {
  if(e.ClickCount<2)return;
  if(Kind!=DeviceGroupKind.Switch){SeriesGraphRequested?.Invoke(this);e.Handled=true;return;}
  if(!Geometry.Collapsed)Geometry.ExpandedHeight=Geometry.Height;
  Geometry.Collapsed=!Geometry.Collapsed;
  if(!Geometry.Collapsed&&Geometry.ExpandedHeight>32)Geometry.Height=Geometry.ExpandedHeight;
  ApplyGeometry();GeometryChanged?.Invoke(this);e.Handled=true;
 }
 private static bool IsInteractiveSource(object source){DependencyObject? current=source as DependencyObject;while(current is not null){if(current is System.Windows.Controls.Primitives.ButtonBase or Thumb or MenuItem)return true;current=current is FrameworkContentElement content?content.Parent:VisualTreeHelper.GetParent(current);}return false;}
 private void Down(object s,MouseButtonEventArgs e){if(e.ClickCount>=2||IsInteractiveSource(e.OriginalSource))return;var canvas=FindParentCanvas();if(canvas is null)return;_down=e.GetPosition(canvas);_drag=true;HeaderBorder.CaptureMouse();e.Handled=true;}
 private void Move(object s,System.Windows.Input.MouseEventArgs e){if(!_drag||e.LeftButton!=MouseButtonState.Pressed)return;var canvas=FindParentCanvas();if(canvas is null)return;var p=e.GetPosition(canvas);Geometry.X=Math.Max(0,Geometry.X+p.X-_down.X);Geometry.Y=Math.Max(0,Geometry.Y+p.Y-_down.Y);_down=p;Canvas.SetLeft(this,Geometry.X);Canvas.SetTop(this,Geometry.Y);e.Handled=true;}
 private void Up(object s,MouseButtonEventArgs e){if(!_drag)return;_drag=false;HeaderBorder.ReleaseMouseCapture();GeometryChanged?.Invoke(this);e.Handled=true;}
 private Canvas? FindParentCanvas(){DependencyObject? current=this;while(current is not null){if(current is Canvas canvas)return canvas;current=VisualTreeHelper.GetParent(current);}return null;}
 private void Resize(object s,DragDeltaEventArgs e){Geometry.Width=Math.Max(260,Geometry.Width+e.HorizontalChange);Geometry.Height=Math.Max(58,Geometry.Height+e.VerticalChange);Geometry.ExpandedHeight=Geometry.Height;Geometry.Collapsed=false;ApplyGeometry();GeometryChanged?.Invoke(this);}
 public void ArrangeChildren()
 {
  var usable=Math.Max(300,Geometry.Width-38);double x=5d;double y=5d;double rowHeight=0d;
  var summary=_children.FirstOrDefault(p=>p.Device.IsPowerSummary);
  if(summary is not null)
  {
   summary.Geometry.X=5;summary.Geometry.Y=5;summary.ApplyGeometry();
   x=summary.Geometry.Width+20;y=5;rowHeight=Math.Max(rowHeight,summary.Geometry.Height);
  }
  foreach(var panel in _children.Where(p=>!p.Device.IsPowerSummary))
  {
   if(x+panel.Geometry.Width>usable){x=summary is not null?summary.Geometry.Width+20:5;y+=rowHeight+10;rowHeight=0;}
   panel.Geometry.X=x;panel.Geometry.Y=y;panel.ApplyGeometry();x+=panel.Geometry.Width+10;rowHeight=Math.Max(rowHeight,panel.Geometry.Height);
  }
  RefreshExtent();
  GeometryChanged?.Invoke(this);
 }
 public void RefreshExtent()
 {
  var usable=Math.Max(300,Geometry.Width-38);
  ItemsHost.Width=Math.Max(usable,_children.Count==0?usable:_children.Max(p=>p.Geometry.X+p.Geometry.Width+15));
  ItemsHost.Height=Math.Max(40,_children.Count==0?40:_children.Max(p=>p.Geometry.Y+p.Geometry.Height+15));
 }

 private ContextMenu BuildMenu()
 {
  var menu=new ContextMenu();
  if(Kind!=DeviceGroupKind.Switch){var graph=new MenuItem{Header="この系列のグラフを重ねて表示"};graph.Click+=(_,__)=>SeriesGraphRequested?.Invoke(this);menu.Items.Add(graph);}
  var arrange=new MenuItem{Header="この系列枠内のデバイスパネルを整列"};arrange.Click+=(_,__)=>ArrangeChildren();menu.Items.Add(arrange);
  var collapse=new MenuItem{Header="タイトルバーだけ表示／元に戻す"};collapse.Click+=(_,__)=>{if(!Geometry.Collapsed)Geometry.ExpandedHeight=Geometry.Height;Geometry.Collapsed=!Geometry.Collapsed;if(!Geometry.Collapsed&&Geometry.ExpandedHeight>32)Geometry.Height=Geometry.ExpandedHeight;ApplyGeometry();GeometryChanged?.Invoke(this);};menu.Items.Add(collapse);
  menu.Items.Add(new Separator());
  var visible=new MenuItem{Header="この系列枠を表示／非表示"};visible.Click+=(_,__)=>{Geometry.Visible=!Geometry.Visible;ApplyGeometry();GeometryChanged?.Invoke(this);};menu.Items.Add(visible);return menu;
 }
}
