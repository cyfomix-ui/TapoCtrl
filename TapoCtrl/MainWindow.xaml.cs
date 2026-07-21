using System.Diagnostics;using System.Windows.Interop;using System.Reflection;using System.Windows;using System.Windows.Controls;using System.Windows.Input;using Forms=System.Windows.Forms;using TapoCtrl.Controls;using TapoCtrl.Models;using TapoCtrl.Services;using TapoCtrl.Windows;
namespace TapoCtrl;
public partial class MainWindow:Window
{
 public event EventHandler? TrayRegistered;
 private readonly SettingsService _settingsService=new();private readonly HistoryService _history=new();private readonly DeviceCoordinator _devices;private readonly PythonTapoTransport _transport;private AppSettings _settings;private readonly List<DevicePanel> _panels=[];private readonly List<DevicePanel> _selectedPanels=[];private readonly List<DeviceGroupPanel> _groups=[];private Forms.NotifyIcon? _tray;private Forms.Timer? _trayClickTimer;private Forms.Timer? _miniPanelRefreshTimer;private DateTime _trayDoubleClickSuppressUntil=DateTime.MinValue;private SwitchTrayWindow? _switchTrayWindow;private LocalHttpService? _http;private readonly Dictionary<string,GraphWindow> _graphWindows=new(StringComparer.OrdinalIgnoreCase);private readonly Dictionary<string,SeriesGraphWindow> _seriesGraphWindows=new(StringComparer.OrdinalIgnoreCase);private readonly Dictionary<string,(double Value,int Count)> _pendingPowerSpikes=new(StringComparer.OrdinalIgnoreCase);private bool _exit;private readonly System.Windows.Threading.DispatcherTimer _clockTimer=new(){Interval=TimeSpan.FromSeconds(1)};
 public MainWindow(){InitializeComponent();_settings=_settingsService.Load();AppLog.Configure(_settings.LoggingEnabled,_settings.LogLevel,_settings.VerboseFunctionEntryLogging);AppLog.Info("TapoCtrl starting");_transport=new(_settingsService,_settings);_devices=new(_transport,_history);_devices.Updated+=x=>Dispatcher.Invoke(()=>Render(x));_devices.StatusChanged+=(text,busy)=>Dispatcher.Invoke(()=>SetStatus(text,busy,!busy&&text.Contains("失敗")));_clockTimer.Tick+=(_,__)=>UpdateClock();_clockTimer.Start();UpdateClock();PreviewKeyDown+=MainPreviewKeyDown;Loaded+=OnLoaded;Closing+=OnClosing;}
 private void MainPreviewKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
 {
  if(e.Key==System.Windows.Input.Key.F5 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
  {
   e.Handled=true;
   RediscoverNow();
   return;
  }
  if(e.Key==System.Windows.Input.Key.F5)
  {
   e.Handled=true;
   RefreshNow();
  }
 }
 private async void OnLoaded(object s,RoutedEventArgs e){Left=_settings.Left;Top=_settings.Top;Width=_settings.Width;Height=_settings.Height;SetStatus("起動処理を開始しています…",true);try{SetupTray();}catch(Exception ex){StatusText.Text="トレイ初期化失敗: "+ex.Message;}finally{TrayRegistered?.Invoke(this,EventArgs.Empty);}BuildTabs();SetStatus("PythonとTapoライブラリを確認しています…",true);var dependency=await PythonDependencyService.CheckAsync(_settings.PythonPath);if(!dependency.Ready){SetStatus("Python環境の準備が必要です。",false,true);var guide=new PythonInstallWindow(dependency){Owner=this};guide.ShowDialog();SetStatus("Python環境を再確認しています…",true);dependency=await PythonDependencyService.CheckAsync(_settings.PythonPath);}try{if(dependency.Ready)await _devices.StartAsync(_settings.Devices,_settings.ValuePollSeconds,_settings.MetadataPollMinutes);else SetStatus("Python環境が未準備のため、Tapoデバイス監視を開始できません。右クリック→設定で確認してください。",false,true);}catch(Exception ex){SetStatus("デバイス開始失敗: "+ex.Message,false,true);}if(_settings.HttpEnabled){try{_http=new(GetHttpDevices,SetPowerById,OpenGraphById,()=>_settings.ElectricityRateYenPerKwh,ReadWebHistoryAsync,_history,()=>_settings.WebViewGraphDeviceIds,()=>_settings.StaleDeviceMinutes);_http.Start(LocalHttpService.NormalizeBind(_settings.HttpBind),_settings.HttpPort);}catch(Exception ex){SetStatus("HTTPサービス開始失敗: "+ex.Message,false,true);}}}
 private void BuildTabs(){Tabs.Items.Clear();foreach(var tab in _settings.Tabs){var c=new Canvas{Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23,26,31)),ClipToBounds=true,Tag=tab};c.MouseRightButtonUp+=CanvasRightClick;Tabs.Items.Add(new TabItem{Header=tab.Name,Content=c});}if(Tabs.Items.Count==0){_settings.Tabs.Add(new());BuildTabs();return;}UpdateTabHeaderVisibility();}
 private void UpdateTabHeaderVisibility(){if(Tabs.Items.Count==1){var template=new DataTemplate();var f=new FrameworkElementFactory(typeof(Border));f.SetValue(HeightProperty,0d);f.SetValue(WidthProperty,0d);template.VisualTree=f;((TabItem)Tabs.Items[0]).HeaderTemplate=template;}else foreach(TabItem x in Tabs.Items)x.ClearValue(HeaderedContentControl.HeaderTemplateProperty);}
 private Canvas CurrentCanvas=>((TabItem)Tabs.SelectedItem).Content as Canvas??((TabItem)Tabs.Items[0]).Content as Canvas??throw new InvalidOperationException();
 private TabLayout CurrentLayout=>(CurrentCanvas.Tag as TabLayout)??_settings.Tabs[0];
 private void SetupTray(){System.Drawing.Icon? icon=null;using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("TapoCtrl.Assets.TapoCtrl.ico"))if(stream!=null)icon=new System.Drawing.Icon(stream);icon??=System.Drawing.SystemIcons.Application;_tray=new Forms.NotifyIcon{Icon=icon,Visible=true,Text=$"TapoCtrl Ver {VersionText}"};var m=new Forms.ContextMenuStrip{BackColor=System.Drawing.Color.FromArgb(32,37,45),ForeColor=System.Drawing.Color.White,ShowImageMargin=false,Renderer=new DarkMenuRenderer()};m.Items.Add("設定",null,(_,__)=>ShowSettings());m.Items.Add("電気料金設定",null,(_,__)=>ShowElectricitySettings());m.Items.Add("Webサーバー設定",null,(_,__)=>ShowWebServerSettings());m.Items.Add("パネル表示",null,(_,__)=>ShowPanel());m.Items.Add("ミニパネル表示",null,(_,__)=>Dispatcher.BeginInvoke(new Action(ShowSwitchTray),System.Windows.Threading.DispatcherPriority.ApplicationIdle));m.Items.Add(new Forms.ToolStripSeparator());m.Items.Add("このアプリについて",null,(_,__)=>System.Windows.MessageBox.Show($"TapoCtrl Ver {VersionText}","TapoCtrl"));m.Items.Add("Help",null,(_,__)=>OpenHelp());m.Items.Add(new Forms.ToolStripSeparator());m.Items.Add("終了",null,(_,__)=>Exit());_tray.ContextMenuStrip=m;
 _trayClickTimer=new Forms.Timer{Interval=320};_trayClickTimer.Tick+=(_,__)=>{_trayClickTimer.Stop();Dispatcher.BeginInvoke(new Action(ShowSwitchTray),System.Windows.Threading.DispatcherPriority.ApplicationIdle);};
 void scheduleSwitchTray(){if(DateTime.Now<_trayDoubleClickSuppressUntil)return;_trayClickTimer?.Stop();_trayClickTimer?.Start();}
 _tray.MouseClick+=(_,a)=>{if(a.Button==Forms.MouseButtons.Left)scheduleSwitchTray();};
 _tray.MouseUp+=(_,a)=>{if(a.Button==Forms.MouseButtons.Left)scheduleSwitchTray();};
 _tray.MouseDoubleClick+=(_,a)=>{if(a.Button==Forms.MouseButtons.Left){_trayDoubleClickSuppressUntil=DateTime.Now.AddMilliseconds(650);_trayClickTimer?.Stop();Dispatcher.BeginInvoke(new Action(ShowPanel));}};
 }
 private string VersionText
{
    get
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.1.01";
        var suffixIndex = version.IndexOf('+');
        return suffixIndex >= 0 ? version[..suffixIndex] : version;
    }
}
 private void ShowPanel(){_switchTrayWindow?.Close();Show();WindowState=WindowState.Normal;Activate();}
 private void ShowSwitchTray()
 {
  if(_switchTrayWindow is { IsVisible:true }){_switchTrayWindow.Close();_switchTrayWindow=null;return;}
  var window=new SwitchTrayWindow(async(d,on)=>await SetPowerFromTray(d,on));
  window.RefreshRequested+=()=>RefreshNow();
  window.SetDevices(GetMiniPanelDevices());
  window.Closed+=(_,__)=>{_miniPanelRefreshTimer?.Stop();if(ReferenceEquals(_switchTrayWindow,window))_switchTrayWindow=null;};
  window.WindowStartupLocation=WindowStartupLocation.Manual;
  // 初期表示は一瞬でも画面外に置き、実サイズ取得後にDPI補正済み座標で再配置する。
  window.Left=-32000;
  window.Top=-32000;

  _switchTrayWindow=window;
  StartMiniPanelRefreshTimer();
  window.Show();

  void place()
  {
   if(!window.IsVisible)return;

   var cursorPx=Forms.Cursor.Position;
   var areaPx=Forms.Screen.FromPoint(cursorPx).WorkingArea;
   var source=PresentationSource.FromVisual(window);
   var fromDevice=source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;

   System.Windows.Point ToDip(double x,double y)=>fromDevice.Transform(new System.Windows.Point(x,y));
   var cursor=ToDip(cursorPx.X,cursorPx.Y);
   var areaTopLeft=ToDip(areaPx.Left,areaPx.Top);
   var areaBottomRight=ToDip(areaPx.Right,areaPx.Bottom);

   var width=double.IsNaN(window.ActualWidth)||window.ActualWidth<=0?window.Width:window.ActualWidth;
   var height=double.IsNaN(window.ActualHeight)||window.ActualHeight<=0?window.Height:window.ActualHeight;
   if(width<=0)width=340;
   if(height<=0)height=560;

   var leftLimit=areaTopLeft.X+8;
   var topLimit=areaTopLeft.Y+8;
   var rightLimit=areaBottomRight.X-8;
   var bottomLimit=areaBottomRight.Y-8;

   // マウス位置を右下原点として表示する。
   var desiredLeft=cursor.X-width;
   var desiredTop=cursor.Y-height;

   window.Left=Math.Max(leftLimit,Math.Min(desiredLeft,rightLimit-width));
   window.Top=Math.Max(topLimit,Math.Min(desiredTop,bottomLimit-height));
  }

  window.Dispatcher.BeginInvoke(new Action(()=>{try{if(ReferenceEquals(_switchTrayWindow,window)&&window.IsVisible){place();window.Activate();window.Focus();}}catch{}}),System.Windows.Threading.DispatcherPriority.Loaded);
  window.Dispatcher.BeginInvoke(new Action(()=>{try{if(ReferenceEquals(_switchTrayWindow,window)&&window.IsVisible)place();}catch{}}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
 }
 private IReadOnlyList<DeviceSnapshot> GetMiniPanelDevices()
 {
  var stable=_settings.Devices.Where(d=>d.Kind!=DeviceKind.Hub&&!d.IsPowerSummary).ToDictionary(d=>d.Id,StringComparer.OrdinalIgnoreCase);
  var result=new List<DeviceSnapshot>();
  foreach(var d in _devices.Devices.Where(d=>d.Kind!=DeviceKind.Hub&&!d.IsPowerSummary))
  {
   if(stable.TryGetValue(d.Id,out var old))
   {
    StabilizeSnapshot(d,old);
   }
   result.Add(d);
  }
  foreach(var old in stable.Values.Where(d=>result.All(x=>!x.Id.Equals(d.Id,StringComparison.OrdinalIgnoreCase))))
  {
   // An omitted refresh result is not an individual offline result. Preserve last state/time.
   result.Add(old);
  }
  return result;
 }
 private void StartMiniPanelRefreshTimer()
 {
  _miniPanelRefreshTimer?.Stop();
  _miniPanelRefreshTimer ??= new Forms.Timer{Interval=60000};
  _miniPanelRefreshTimer.Tick-=MiniPanelRefreshTick;
  _miniPanelRefreshTimer.Tick+=MiniPanelRefreshTick;
  _miniPanelRefreshTimer.Start();
 }
 private async void MiniPanelRefreshTick(object? sender,EventArgs e)
 {
  if(_switchTrayWindow is not { IsVisible:true }){_miniPanelRefreshTimer?.Stop();return;}
  try
  {
   await _devices.RefreshValuesAsync();
   if(_switchTrayWindow is { IsVisible:true })_switchTrayWindow.SetDevices(GetMiniPanelDevices());
  }
  catch{}
 }
 private async Task SetPowerFromTray(DeviceSnapshot device,bool on)
 {
  try
  {
   await _transport.SetPowerOneShotAsync(device,on);
   var current=_devices.Devices.FirstOrDefault(x=>x.Id.Equals(device.Id,StringComparison.OrdinalIgnoreCase)||(!string.IsNullOrWhiteSpace(device.Ip)&&x.Ip.Equals(device.Ip,StringComparison.OrdinalIgnoreCase)));
   if(current is not null){current.IsOn=on;if(!on)current.PowerWatts=0;current.Timestamp=DateTime.Now;current.Online=true;}
   Dispatcher.Invoke(()=>
   {
    foreach(var panel in _panels.Where(x=>ReferenceEquals(x.Device,device)||x.Device.Id.Equals(device.Id,StringComparison.OrdinalIgnoreCase)))panel.UpdateDevice(current??device);
    if(_switchTrayWindow is { IsVisible:true })_switchTrayWindow.SetDevices(GetMiniPanelDevices());
    SetStatus($"{device.Name} を {(on?"ON":"OFF")} にしました（独立コマンド）。",false);
   });
  }
  catch(Exception ex)
  {
   Dispatcher.Invoke(()=>SetStatus($"{device.Name} の電源操作に失敗しました: {ex.Message}",false,true));
  }
 }
 private void StabilizeSnapshot(DeviceSnapshot current,DeviceSnapshot previous)
 {
  if(string.IsNullOrWhiteSpace(current.Name)||IsIpLike(current.Name))current.Name=previous.Name;
  if(string.IsNullOrWhiteSpace(current.Ip))current.Ip=previous.Ip;
  if(string.IsNullOrWhiteSpace(current.Hub))current.Hub=previous.Hub;
  if(string.IsNullOrWhiteSpace(current.Model))current.Model=previous.Model;
  if(current.Kind==DeviceKind.Unknown || (current.Kind==DeviceKind.Switch && previous.Kind is DeviceKind.Power or DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity))
   current.Kind=previous.Kind;

  if(IsInvalidPowerWatts(current.PowerWatts))
  {
   current.PowerWatts=0;
   current.Online=false;
  }
  else if(IsSuspiciousPowerSpike(current,previous))
  {
   var v=current.PowerWatts!.Value;
   if(_pendingPowerSpikes.TryGetValue(current.Id,out var pending) && Math.Abs(pending.Value-v)<=150)
    _pendingPowerSpikes[current.Id]=(v,pending.Count+1);
   else
    _pendingPowerSpikes[current.Id]=(v,1);

   if(_pendingPowerSpikes[current.Id].Count<2)
   {
    current.PowerWatts=0;
    current.Online=false;
   }
   else
   {
    _pendingPowerSpikes.Remove(current.Id);
   }
  }
  else
  {
   _pendingPowerSpikes.Remove(current.Id);
  }

  if(current.PowerWatts is null && current.Kind==DeviceKind.Power){current.PowerWatts=0;current.Online=false;}
  else if(current.PowerWatts is null)current.PowerWatts=previous.PowerWatts;
  if(current.TodayWh is null)current.TodayWh=previous.TodayWh;
  if(current.MonthWh is null)current.MonthWh=previous.MonthWh;
  if(current.TemperatureC is null)current.TemperatureC=previous.TemperatureC;
  if(current.HumidityPercent is null)current.HumidityPercent=previous.HumidityPercent;
  if(current.IsOn is null)current.IsOn=previous.IsOn;
 }
 private static bool IsInvalidPowerWatts(double? watts)=>watts is not null && (watts<0 || watts>3000);
 private static bool IsIpLike(string? value)=>System.Net.IPAddress.TryParse(value??string.Empty,out _);
 private static bool IsSuspiciousPowerSpike(DeviceSnapshot current,DeviceSnapshot previous)
 {
  if(current.Kind!=DeviceKind.Power)return false;
  if(current.PowerWatts is null)return false;
  if(previous.PowerWatts is null)return false;
  var now=current.PowerWatts.Value;
  var old=previous.PowerWatts.Value;
  // 低負荷だった機器が一瞬だけ高負荷になる誤読を抑止。
  // 実際に高負荷が継続する場合は次回同程度の値で採用する。
  return old<=100 && now>=600 && (now-old)>=500;
 }

 private void Render(IReadOnlyList<DeviceSnapshot> source)
 {
  var incoming=source.Where(d=>d.Kind!=DeviceKind.Hub&&d.Id!="__power_summary__").ToList();
  var previous=_settings.Devices.Where(d=>d.Kind!=DeviceKind.Hub&&d.Id!="__power_summary__").ToDictionary(d=>d.Id,StringComparer.OrdinalIgnoreCase);
  var incomingById=incoming.ToDictionary(d=>d.Id,StringComparer.OrdinalIgnoreCase);
  var realList=new List<DeviceSnapshot>();
  foreach(var d in incoming)
  {
   if(previous.TryGetValue(d.Id,out var old))
   {
    StabilizeSnapshot(d,old);
   }
   else if(IsInvalidPowerWatts(d.PowerWatts))
   {
    d.PowerWatts=null;
    d.Online=false;
   }
   realList.Add(d);
  }
  foreach(var old in previous.Values.Where(d=>!incomingById.ContainsKey(d.Id)))
  {
   // A partial/global refresh omission is not proof of an individual device failure.
   // Preserve last state/value/time; the panel moves to stale after StaleDeviceMinutes.
   realList.Add(old);
  }
  _settings.Devices=realList;
  var allPowerDevices=realList.Where(d=>d.Kind==DeviceKind.Power).ToList();
  var powerDevices=allPowerDevices.Where(d=>d.Online).ToList();
  // Keep last normal readings visible during a temporary outage; Offline state is shown separately.
  var totalWatts=allPowerDevices.Sum(d=>d.PowerWatts??0);
  var totalTodayWh=allPowerDevices.Sum(d=>d.TodayWh??0);
  var totalMonthWh=allPowerDevices.Sum(d=>d.MonthWh??0);
  var estimatedYen=totalTodayWh/1000.0*_settings.ElectricityRateYenPerKwh;
  var monthEstimatedYen=totalMonthWh/1000.0*_settings.ElectricityRateYenPerKwh;
  var summary=new DeviceSnapshot{Id="__power_summary__",Name="使用電力",Kind=DeviceKind.Power,PowerWatts=totalWatts,TodayWh=totalTodayWh,MonthWh=totalMonthWh,TodayCostYen=estimatedYen,MonthCostYen=monthEstimatedYen,Online=true,Timestamp=DateTime.Now};
  var list=new List<DeviceSnapshot>{summary};list.AddRange(realList);
  if(Tabs.Items.Count==0)BuildTabs();
  if(Tabs.SelectedIndex<0)Tabs.SelectedIndex=0;
  var layout=_settings.Tabs[0];
  var canvas=(Canvas)((TabItem)Tabs.Items[0]).Content;
  EnsureDefaultGroups(layout,canvas);
  if(layout.Panels.All(x=>x.DeviceId!="__power_summary__"))
  {
   var powerIds=allPowerDevices.Select(x=>x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
   foreach(var existingGeometry in layout.Panels.Where(x=>powerIds.Contains(x.DeviceId)))existingGeometry.Y+=175;
  }
  // 一時的な取得漏れでパネルを消さない。未取得のデバイスは前回スナップショットをOfflineとして保持する。
  foreach(var d in list)
  {
   var p=_panels.FirstOrDefault(x=>x.Device.Id==d.Id);
   if(p!=null){p.UpdateDevice(d);continue;}
   var g=layout.Panels.FirstOrDefault(x=>x.DeviceId==d.Id);
   if(g is null)
   {
    g=new PanelGeometry{DeviceId=d.Id,Width=d.IsPowerSummary?250:d.Kind==DeviceKind.Switch?210:240,Height=d.IsPowerSummary?165:d.Kind==DeviceKind.Switch?72:125,Visible=true};
    if(d.IsPowerSummary){g.X=5;g.Y=5;}
    layout.Panels.Add(g);
   }
   if(d.IsPowerSummary)g.X=5;
   if(d.Kind==DeviceKind.Switch&&Math.Abs(g.Height-125)<0.1)g.Height=72;
   p=new DevicePanel(d,g,_settings.StaleDeviceMinutes);
   p.GeometryChanged+=_=>{RefreshGroupExtents();SaveSettings();};p.OpenGraphRequested+=OpenGraph;p.DetailsRequested+=ShowDeviceDetails;p.PowerRequested+=async(x,on)=>await SetPower(x,on);p.SelectionRequested+=SelectPanel;p.MoveRequested+=MoveSelectedPanels;p.ResizeRequested+=ResizeSelectedPanels;p.LayoutCommandRequested+=ApplySelectedPanelLayout;p.SelectedPanelCountProvider=GetSelectedPanelCountForSource;
   _panels.Add(p);
  }
  foreach(var group in _groups)
   group.SetChildren(_panels.Where(p=>p.Device.GroupKind==group.Kind&&p.Geometry.Visible));
  foreach(var group in _groups)group.ApplyGeometry();
  if(list.Count>0)
  {
   // Refresh only updates the view model. It must never restore, show, or activate a hidden/minimized window.
   SetStatus($"最終更新 {DateTime.Now:HH:mm:ss} / {realList.Count} devices",false,false);
  }
  else SetStatus("探索は完了しましたが、表示できるデバイスがありません。設定またはネットワークを確認してください。",false,true);
  if(_switchTrayWindow is { IsVisible:true })_switchTrayWindow.SetDevices(GetMiniPanelDevices());
  SaveSettings();
 }
 private void EnsureDefaultGroups(TabLayout layout,Canvas canvas)
 {
  if(layout.Groups.Count==0)
  {
   layout.Groups.Add(new GroupGeometry{Kind=DeviceGroupKind.Power,Title="電力系",X=18,Y=18,Width=Math.Max(520,canvas.ActualWidth-36),Height=180});
   layout.Groups.Add(new GroupGeometry{Kind=DeviceGroupKind.Environment,Title="温度・湿度系",X=18,Y=212,Width=Math.Max(520,canvas.ActualWidth-36),Height=180});
   layout.Groups.Add(new GroupGeometry{Kind=DeviceGroupKind.Switch,Title="スイッチ系",X=18,Y=406,Width=Math.Max(520,canvas.ActualWidth-36),Height=115});
  }
  foreach(var saved in layout.Groups.Where(g=>g.Kind==DeviceGroupKind.Switch&&Math.Abs(g.Height-180)<0.1))saved.Height=115;
  foreach(var gg in layout.Groups.OrderBy(g=>g.Kind))
  {
   var group=_groups.FirstOrDefault(g=>g.Kind==gg.Kind);
   if(group==null)
   {
    group=new DeviceGroupPanel(gg);group.GeometryChanged+=_=>SaveSettings();group.SeriesGraphRequested+=g=>_ = OpenSeriesGraphSafeAsync(g,DateOnly.FromDateTime(DateTime.Now));_groups.Add(group);canvas.Children.Add(group);
   }
  }
 }

 private void SelectPanel(DevicePanel panel,bool ctrl)
 {
  if(!ctrl)
  {
   // 複数選択済みのパネルをドラッグ開始したときは選択を維持し、まとめて移動できるようにする。
   if(_selectedPanels.Count>1&&_selectedPanels.Contains(panel))return;
   foreach(var p in _selectedPanels.ToList())p.SetSelected(false);
   _selectedPanels.Clear();
   _selectedPanels.Add(panel);panel.SetSelected(true);
   return;
  }
  if(_selectedPanels.Contains(panel)){_selectedPanels.Remove(panel);panel.SetSelected(false);}
  else{_selectedPanels.Add(panel);panel.SetSelected(true);}
 }

 private List<DevicePanel> SelectedPanelsForSource(DevicePanel source)
 {
  if(!_selectedPanels.Contains(source))return [source];
  // パネル座標は系列枠内のローカル座標なので、整列対象は同じ系列枠内に限定する。
  return _selectedPanels.Where(p=>ReferenceEquals(p.Parent,source.Parent)&&p.Visibility==Visibility.Visible&&!p.Device.IsPowerSummary).ToList();
 }
 private int GetSelectedPanelCountForSource(DevicePanel source)=>SelectedPanelsForSource(source).Count;
 private void ApplySelectedPanelLayout(DevicePanel source,PanelLayoutCommand command)
 {
  var targets=SelectedPanelsForSource(source).Where(p=>!p.Device.IsPowerSummary).ToList();
  if(targets.Count<2)return;
  switch(command)
  {
   case PanelLayoutCommand.AlignTop:
    var top=targets.Min(p=>p.Geometry.Y);
    foreach(var p in targets){p.Geometry.Y=top;p.ApplyGeometry();}
    break;
   case PanelLayoutCommand.AlignLeft:
    var left=targets.Min(p=>p.Geometry.X);
    foreach(var p in targets){p.Geometry.X=left;p.ApplyGeometry();}
    break;
   case PanelLayoutCommand.AlignRight:
    var right=targets.Max(p=>p.Geometry.X+p.Geometry.Width);
    foreach(var p in targets){p.Geometry.X=Math.Max(0,right-p.Geometry.Width);p.ApplyGeometry();}
    break;
   case PanelLayoutCommand.AlignBottom:
    var bottom=targets.Max(p=>p.Geometry.Y+p.Geometry.Height);
    foreach(var p in targets){p.Geometry.Y=Math.Max(0,bottom-p.Geometry.Height);p.ApplyGeometry();}
    break;
   case PanelLayoutCommand.MatchHeight:
    // 右クリックした基準パネルの現在表示高さに、選択パネルの縦幅を揃える。
    var height=source.Height;
    foreach(var p in targets){p.Geometry.Height=height;p.ApplyGeometry();}
    break;
   case PanelLayoutCommand.MatchWidth:
    // 右クリックした基準パネルの現在表示幅に、選択パネルの横幅を揃える。
    var width=source.Width;
    foreach(var p in targets){p.Geometry.Width=width;p.ApplyGeometry();}
    break;
   case PanelLayoutCommand.DistributeEvenly:
    DistributeSelectedPanels(targets);
    break;
  }
  RefreshGroupExtents();SaveSettings();
 }
 private static void DistributeSelectedPanels(List<DevicePanel> targets)
 {
  if(targets.Count<3)return;
  var minX=targets.Min(p=>p.Geometry.X);var maxRight=targets.Max(p=>p.Geometry.X+p.Geometry.Width);
  var minY=targets.Min(p=>p.Geometry.Y);var maxBottom=targets.Max(p=>p.Geometry.Y+p.Geometry.Height);
  if(maxRight-minX>=maxBottom-minY)
  {
   var ordered=targets.OrderBy(p=>p.Geometry.X).ToList();
   var totalWidth=ordered.Sum(p=>p.Geometry.Width);
   var gap=Math.Max(0,(maxRight-minX-totalWidth)/(ordered.Count-1));
   var x=minX;foreach(var p in ordered){p.Geometry.X=x;p.ApplyGeometry();x+=p.Geometry.Width+gap;}
  }
  else
  {
   var ordered=targets.OrderBy(p=>p.Geometry.Y).ToList();
   var totalHeight=ordered.Sum(p=>p.Geometry.Height);
   var gap=Math.Max(0,(maxBottom-minY-totalHeight)/(ordered.Count-1));
   var y=minY;foreach(var p in ordered){p.Geometry.Y=y;p.ApplyGeometry();y+=p.Geometry.Height+gap;}
  }
 }
 private void MoveSelectedPanels(DevicePanel source,double dx,double dy)
 {
  if(source.Device.IsPowerSummary)
  {
   // 使用電力計は電力系枠の左端へ固定したまま、上下方向だけ移動できる。
   source.Geometry.X=5;
   source.Geometry.Y=Math.Max(0,source.Geometry.Y+dy);
   source.ApplyGeometry();
   RefreshGroupExtents();SaveSettings();
   return;
  }
  var targets=SelectedPanelsForSource(source);
  if(targets.Count==0)return;
  // 端へ到達したパネルだけが止まって選択形状が崩れないよう、移動量をグループ全体で制限する。
  var appliedDx=Math.Max(dx,-targets.Min(p=>p.Geometry.X));
  var appliedDy=Math.Max(dy,-targets.Min(p=>p.Geometry.Y));
  foreach(var p in targets)
  {
   p.Geometry.X+=appliedDx;p.Geometry.Y+=appliedDy;p.ApplyGeometry();
  }
  RefreshGroupExtents();SaveSettings();
 }
 private void ResizeSelectedPanels(DevicePanel source,double width,double height)
 {
  if(!_selectedPanels.Contains(source)||_selectedPanels.Count<2)return;
  foreach(var p in _selectedPanels.Where(x=>!ReferenceEquals(x,source)&&!x.Device.IsPowerSummary))
  {
   p.Geometry.Width=width;p.Geometry.Height=Math.Max(p.Device.Kind==DeviceKind.Switch?28:34,height);p.ApplyGeometry();
  }
  RefreshGroupExtents();SaveSettings();
 }
 private async Task OpenSeriesGraphSafeAsync(DeviceGroupPanel group,DateOnly date)
 {
  try{await OpenSeriesGraphAsync(group,date);}catch(Exception ex){AppLog.Error("系列グラフを開けませんでした",ex);SetStatus("系列グラフを開けませんでした: "+ex.Message,false,true);System.Windows.MessageBox.Show(this,"系列グラフを開けませんでした: "+ex.Message,"TapoCtrl",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private async Task OpenSeriesGraphAsync(DeviceGroupPanel group,DateOnly date)
 {
  if(group.Kind==DeviceGroupKind.Switch)return;
  var key=$"series:{group.Kind}:{date:yyyy-MM-dd}";
  if(_seriesGraphWindows.TryGetValue(key,out var existing)){if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;existing.Activate();return;}
  if(group.Kind==DeviceGroupKind.Power)
  {
   var powerDevices=_devices.Devices.Where(d=>d.Kind==DeviceKind.Power&&!d.IsPowerSummary).OrderBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).ToList();
   var ids=powerDevices.Select(d=>d.Id).ToList();var byId=await _history.ReadPowerSeriesForDateAsync(ids,date);var aggregate=HistoryService.AggregatePowerSeries(byId);var powerAvailableDates=await _history.GetAvailableAggregateDatesAsync(ids);
   var series=new List<GraphSeries>{new(){Name="合計",Points=aggregate,IsTotal=true}};foreach(var d in powerDevices)series.Add(new GraphSeries{Name=d.Name,Points=byId.GetValueOrDefault(d.Id,[])});
   async Task<(IReadOnlyList<GraphSeries> First,IReadOnlyList<GraphSeries>? Second)> ReloadPower(){var fresh=await _history.ReadPowerSeriesForDateAsync(ids,date);var rows=new List<GraphSeries>{new(){Name="合計",Points=HistoryService.AggregatePowerSeries(fresh),IsTotal=true}};foreach(var d in powerDevices)rows.Add(new GraphSeries{Name=d.Name,Points=fresh.GetValueOrDefault(d.Id,[])});return(rows,null);}
   PowerEnergyStatistics GetSeriesEnergyStatistics()=>CreatePowerEnergyStatistics(powerDevices);
   var w=new SeriesGraphWindow(key,"電力系 - 全デバイス",series,"W",date,powerAvailableDates,d=>OpenSeriesGraphSafeAsync(group,d),reload:date==DateOnly.FromDateTime(DateTime.Now)?ReloadPower:null,energyProvider:date==DateOnly.FromDateTime(DateTime.Now)?GetSeriesEnergyStatistics:null);PlaceOwnedWindowNearMain(w,1120,760);w.Closed+=(_,__)=>_seriesGraphWindows.Remove(key);_seriesGraphWindows[key]=w;w.Show();return;
  }
  var envDevices=_devices.Devices.Where(d=>d.Kind is DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity).OrderBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).ToList();
  var temp=new List<GraphSeries>();var hum=new List<GraphSeries>();
  foreach(var d in envDevices){if(d.Kind!=DeviceKind.Humidity)temp.Add(new GraphSeries{Name=d.Name,Points=await _history.ReadMetricForDateAsync(d.Id,"temperature",date)});if(d.Kind!=DeviceKind.Temperature)hum.Add(new GraphSeries{Name=d.Name,Points=await _history.ReadMetricForDateAsync(d.Id,"humidity",date)});}
  var availableLists=await Task.WhenAll(envDevices.Select(d=>_history.GetAvailableDatesAsync(d.Id,d.Kind==DeviceKind.Humidity?"humidity":"temperature")));var environmentAvailableDates=availableLists.SelectMany(x=>x).Distinct().OrderByDescending(x=>x).ToList();
  async Task<(IReadOnlyList<GraphSeries> First,IReadOnlyList<GraphSeries>? Second)> ReloadEnvironment(){var nt=new List<GraphSeries>();var nh=new List<GraphSeries>();foreach(var d in envDevices){if(d.Kind!=DeviceKind.Humidity)nt.Add(new GraphSeries{Name=d.Name,Points=await _history.ReadMetricForDateAsync(d.Id,"temperature",date)});if(d.Kind!=DeviceKind.Temperature)nh.Add(new GraphSeries{Name=d.Name,Points=await _history.ReadMetricForDateAsync(d.Id,"humidity",date)});}return(nt,nh);}
  var ew=new SeriesGraphWindow(key,"温度・湿度系 - 全デバイス",temp,"℃",date,environmentAvailableDates,d=>OpenSeriesGraphSafeAsync(group,d),hum,"%",date==DateOnly.FromDateTime(DateTime.Now)?ReloadEnvironment:null);PlaceOwnedWindowNearMain(ew,1120,760);ew.Closed+=(_,__)=>_seriesGraphWindows.Remove(key);_seriesGraphWindows[key]=ew;ew.Show();
 }
 private PowerEnergyStatistics CreatePowerEnergyStatistics(IEnumerable<DeviceSnapshot> source)
 {
  var list=source.Where(x=>x.Kind==DeviceKind.Power&&!x.IsPowerSummary).ToList();
  var todayWh=list.Sum(x=>x.TodayWh??0);
  var monthWh=list.Sum(x=>x.MonthWh??0);
  var rate=_settings.ElectricityRateYenPerKwh;
  return new PowerEnergyStatistics{TodayWh=todayWh,TodayCostYen=todayWh/1000.0*rate,MonthWh=monthWh,MonthCostYen=monthWh/1000.0*rate,MonthAverageWh=monthWh/Math.Max(1,DateTime.Now.Day)};
 }
 private void PlaceOwnedWindowNearMain(Window window,double fallbackWidth,double fallbackHeight)
 {
  window.Owner=this;
  window.Topmost=false;
  window.WindowStartupLocation=WindowStartupLocation.Manual;

  var width=double.IsNaN(window.Width)||window.Width<=0?fallbackWidth:window.Width;
  var height=double.IsNaN(window.Height)||window.Height<=0?fallbackHeight:window.Height;

  var mainLeft=double.IsNaN(Left)?0:Left;
  var mainTop=double.IsNaN(Top)?0:Top;
  var mainWidth=double.IsNaN(ActualWidth)||ActualWidth<=0?Width:ActualWidth;
  var mainHeight=double.IsNaN(ActualHeight)||ActualHeight<=0?Height:ActualHeight;

  var centerX=(int)Math.Max(0,mainLeft+mainWidth/2);
  var centerY=(int)Math.Max(0,mainTop+mainHeight/2);
  var screen=Forms.Screen.FromPoint(new System.Drawing.Point(centerX,centerY)).WorkingArea;

  var left=mainLeft+36;
  var top=mainTop+36;

  if(left+width>screen.Right-8)left=Math.Max(screen.Left+8,screen.Right-width-8);
  if(top+height>screen.Bottom-8)top=Math.Max(screen.Top+8,screen.Bottom-height-8);
  if(left<screen.Left+8)left=screen.Left+8;
  if(top<screen.Top+8)top=screen.Top+8;

  window.Left=left;
  window.Top=top;
 }
 private void ShowDeviceDetails(DeviceSnapshot d)
 {
  var window=new DeviceDetailsWindow(d);
  PlaceOwnedWindowNearMain(window,440,520);
  // 詳細は複数表示可能にするため、辞書で再利用しない。
  window.Show();
 }
 private void OpenGraph(DeviceSnapshot d)=>_ = OpenGraphSafeAsync(d,DateOnly.FromDateTime(DateTime.Now));
 private async Task OpenGraphSafeAsync(DeviceSnapshot d,DateOnly date)
 {
  try{await OpenGraphAsync(d,date);}catch(Exception ex){AppLog.Error("グラフを開けませんでした",ex);SetStatus("グラフを開けませんでした: "+ex.Message,false,true);System.Windows.MessageBox.Show(this,"グラフを開けませんでした: "+ex.Message,"TapoCtrl",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private async Task OpenGraphAsync(DeviceSnapshot d,DateOnly date)
 {
  var id=d.IsPowerSummary?"__power_summary__":d.Id;var key=$"graph:{id}:{date:yyyy-MM-dd}";
  if(_graphWindows.TryGetValue(key,out var existing)){if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;existing.Activate();return;}
  var powerIds=_devices.Devices.Where(x=>x.Kind==DeviceKind.Power&&!x.IsPowerSummary).Select(x=>x.Id).ToList();
  var metric=d.Kind==DeviceKind.Power?"power":d.Kind==DeviceKind.Humidity?"humidity":"temperature";
  var points=d.IsPowerSummary?await _history.ReadAggregateForDateAsync(powerIds,date):await _history.ReadMetricForDateAsync(d.Id,metric,date);
  IReadOnlyList<HistoryPoint> secondary=[];if(d.Kind==DeviceKind.Environment)secondary=await _history.ReadMetricForDateAsync(d.Id,"humidity",date);
  var available=d.IsPowerSummary?await _history.GetAvailableAggregateDatesAsync(powerIds):await _history.GetAvailableDatesAsync(d.Id,metric);
  var unit=d.Kind==DeviceKind.Power?"W":d.Kind is DeviceKind.Environment or DeviceKind.Temperature?"℃":d.Kind==DeviceKind.Humidity?"%":"";var secondaryUnit=d.Kind==DeviceKind.Environment?"%":"";var isToday=date==DateOnly.FromDateTime(DateTime.Now);
  async Task<(IReadOnlyList<HistoryPoint> Primary,IReadOnlyList<HistoryPoint> Secondary)> ReloadHistory(){var primary=d.IsPowerSummary?await _history.ReadAggregateForDateAsync(powerIds,date):await _history.ReadMetricForDateAsync(d.Id,metric,date);IReadOnlyList<HistoryPoint> second=[];if(d.Kind==DeviceKind.Environment)second=await _history.ReadMetricForDateAsync(d.Id,"humidity",date);return(primary,second);}
  PowerEnergyStatistics GetPowerEnergyStatistics(){var list=(d.IsPowerSummary?_devices.Devices.Where(x=>x.Kind==DeviceKind.Power&&!x.IsPowerSummary):_devices.Devices.Where(x=>x.Id.Equals(d.Id,StringComparison.OrdinalIgnoreCase))).ToList();return CreatePowerEnergyStatistics(list); }
  var window=new GraphWindow(key,d.Name,unit,points,date,available,chosen=>OpenGraphSafeAsync(d,chosen),secondary,secondaryUnit,isToday?ReloadHistory:null,d.Kind==DeviceKind.Power&&isToday?GetPowerEnergyStatistics:null);PlaceOwnedWindowNearMain(window,1050,620);window.Closed+=(_,__)=>_graphWindows.Remove(key);_graphWindows[key]=window;window.Show();
 }
 private void OpenGraphById(string id)
 {
  Dispatcher.BeginInvoke(new Action(()=>
  {
   var list=GetHttpDevices();
   var d=list.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase)||x.Name.Equals(id,StringComparison.OrdinalIgnoreCase));
   if(d is not null)OpenGraph(d);
  }));
 }
 private async Task SetPower(DeviceSnapshot d,bool on){await _devices.SetPowerAsync(d,on);await _devices.RefreshValuesAsync();}
 private async Task<bool> SetPowerById(string id,bool on)
 {
  var decoded=(id??string.Empty).Trim();
  var d=_devices.Devices.FirstOrDefault(x=>x.Id.Equals(decoded,StringComparison.OrdinalIgnoreCase)||x.Name.Equals(decoded,StringComparison.OrdinalIgnoreCase)||x.Ip.Equals(decoded,StringComparison.OrdinalIgnoreCase));
  if(d is null)return false;
  try
  {
   await _transport.SetPowerOneShotAsync(d,on);
   d.IsOn=on;
   if(!on)d.PowerWatts=0;
   d.Timestamp=DateTime.Now;
   d.Online=true;
   await Dispatcher.InvokeAsync(()=>
   {
    foreach(var panel in _panels.Where(x=>ReferenceEquals(x.Device,d)||x.Device.Id.Equals(d.Id,StringComparison.OrdinalIgnoreCase)))panel.UpdateDevice(d);
    if(_switchTrayWindow is { IsVisible:true })_switchTrayWindow.SetDevices(GetMiniPanelDevices());
    SetStatus($"HTTP API: {d.Name} を {(on?"ON":"OFF")} にしました（独立コマンド）。",false);
   });
   return true;
  }
  catch(Exception ex)
  {
   await Dispatcher.InvokeAsync(()=>SetStatus($"HTTP API: {d.Name} の電源操作に失敗しました: {ex.Message}",false,true));
   return false;
  }
 }
 private async void RefreshNow()
 {
  try
  {
   SetStatus("F5: 現在値だけを更新しています…",true);
   await _devices.RefreshValuesAsync();
   if(_switchTrayWindow is { IsVisible:true })_switchTrayWindow.SetDevices(GetMiniPanelDevices());
   SetStatus($"F5値更新完了 {DateTime.Now:HH:mm:ss} / {_devices.Devices.Count} devices",false,false);
  }
  catch(Exception ex)
  {
   SetStatus("F5値更新失敗: "+ex.Message,false,true);
  }
 }
 private async void RediscoverNow()
 {
  try
  {
   SetStatus("Ctrl+F5: デバイスを再検索しています…",true);
   await _devices.RefreshMetadataAsync();
   await _devices.RefreshValuesAsync();
   if(_switchTrayWindow is { IsVisible:true })_switchTrayWindow.SetDevices(GetMiniPanelDevices());
   SetStatus($"Ctrl+F5再検索完了 {DateTime.Now:HH:mm:ss} / {_devices.Devices.Count} devices",false,false);
  }
  catch(Exception ex)
  {
   SetStatus("Ctrl+F5再検索失敗: "+ex.Message,false,true);
  }
 }
 private void Arrange()
 {
  if(_groups.Count==0)return;
  var y=18d;var width=Math.Max(520,CurrentCanvas.ActualWidth-36);
  foreach(var group in _groups.OrderBy(g=>g.Kind))
  {
   group.Geometry.X=18;group.Geometry.Y=y;group.Geometry.Width=width;
   group.Geometry.Height=Math.Max(group.Kind==DeviceGroupKind.Switch?95:145,group.Geometry.Height);group.ApplyGeometry();
   y+=group.Geometry.Height+14;
  }
  SaveSettings();
 }
 private void ShowAll()
 {
  foreach(var p in _panels){p.Geometry.Visible=true;p.ApplyGeometry();}
  foreach(var g in _groups){g.Geometry.Visible=true;g.ApplyGeometry();g.SetChildren(_panels.Where(p=>p.Device.GroupKind==g.Kind&&p.Geometry.Visible));}
  SaveSettings();
 }
 private void AddTab(){var t=new TabLayout{Name=$"パネル {_settings.Tabs.Count+1}"};_settings.Tabs.Add(t);var c=new Canvas{Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23,26,31)),ClipToBounds=true,Tag=t};c.MouseRightButtonUp+=CanvasRightClick;Tabs.Items.Add(new TabItem{Header=t.Name,Content=c});UpdateTabHeaderVisibility();Tabs.SelectedIndex=Tabs.Items.Count-1;SaveSettings();}
 private void CanvasRightClick(object sender,MouseButtonEventArgs e)
 {
  var m=new ContextMenu();
  void add(string h,Action a){var x=new MenuItem{Header=h};x.Click+=(_,__)=>a();m.Items.Add(x);}
  var clickedGroup=FindGroupAt(e.GetPosition(CurrentCanvas));
  add("更新",()=>RefreshNow());
  if(clickedGroup is not null)add("この系列枠内のデバイスパネルを整列",()=>clickedGroup.ArrangeChildren());
  add("系列枠を整列",Arrange);
  add("全てのデバイスを表示",ShowAll);
  m.Items.Add(new Separator());
  add("ジオメトリ保存",()=>{SaveSettings();SetStatus("ジオメトリを保存しました",false);});
  add("ジオメトリ再現",()=>{foreach(var p in _panels)p.ApplyGeometry();});
  add("タブ追加",AddTab);
  m.IsOpen=true;e.Handled=true;
 }
 private DeviceGroupPanel? FindGroupAt(System.Windows.Point p)
 {
  return _groups.LastOrDefault(g=>g.Visibility==Visibility.Visible&&p.X>=g.Geometry.X&&p.X<=g.Geometry.X+g.Geometry.Width&&p.Y>=g.Geometry.Y&&p.Y<=g.Geometry.Y+g.Geometry.Height);
 }

 private void RefreshGroupExtents()
 {
  foreach(var group in _groups)group.RefreshExtent();
 }
 private void ShowElectricitySettings()
 {
  var w=new ElectricitySettingsWindow(_settings){Owner=this};if(w.ShowDialog()==true){SaveSettings();Render(_devices.Devices);SetStatus("電気料金設定を保存しました。",false);}
 }
 private void ShowWebServerSettings()
 {
  var w=new WebServerSettingsWindow(_settings){Owner=this};if(w.ShowDialog()==true){SaveSettings();RestartHttpService();}
 }

 private IReadOnlyList<DeviceSnapshot> GetHttpDevices()
 {
  var real=_devices.Devices.Where(d=>d.Kind!=DeviceKind.Hub).ToList();
  var power=real.Where(d=>d.Kind==DeviceKind.Power).ToList();
  var today=power.Sum(d=>d.TodayWh??0);
  var month=power.Sum(d=>d.MonthWh??0);
  real.Insert(0,new DeviceSnapshot{Id="__power_summary__",Name="使用電力",Kind=DeviceKind.Power,PowerWatts=power.Sum(d=>d.PowerWatts??0),TodayWh=today,MonthWh=month,TodayCostYen=today/1000.0*_settings.ElectricityRateYenPerKwh,MonthCostYen=month/1000.0*_settings.ElectricityRateYenPerKwh,Online=true,Timestamp=DateTime.Now});
  return real;
 }
 private async Task<List<HistoryPoint>> ReadWebHistoryAsync(string id)
 {
  if(id=="__power_summary__")
  {
   var powerIds=_devices.Devices.Where(d=>d.Kind==DeviceKind.Power).Select(d=>d.Id).ToList();
   return await _history.ReadAggregate24hAsync(powerIds);
  }
  var suffixIndex=id.LastIndexOf(":metric:",StringComparison.OrdinalIgnoreCase);
  if(suffixIndex>0)
  {
   var deviceId=id[..suffixIndex];
   var metric=id[(suffixIndex+8)..];
   var device=_devices.Devices.FirstOrDefault(d=>d.Id.Equals(deviceId,StringComparison.OrdinalIgnoreCase));
   var current=metric.Equals("power",StringComparison.OrdinalIgnoreCase)?device?.PowerWatts:metric.Equals("temperature",StringComparison.OrdinalIgnoreCase)?device?.TemperatureC:device?.HumidityPercent;
   return await _history.ReadMetric24hAsync(deviceId,metric,current);
  }
  var d=_devices.Devices.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
  if(d is not null)
  {
   if(d.Kind==DeviceKind.Power)return await _history.ReadMetric24hAsync(d.Id,"power",d.PowerWatts);
   if(d.Kind is DeviceKind.Environment or DeviceKind.Temperature)return await _history.ReadMetric24hAsync(d.Id,"temperature",d.TemperatureC);
   if(d.Kind==DeviceKind.Humidity)return await _history.ReadMetric24hAsync(d.Id,"humidity",d.HumidityPercent);
  }
  return await _history.Read24hAsync(id);
 }
 private void RestartHttpService()
 {
  try{_http?.Dispose();_http=null;if(_settings.HttpEnabled){_http=new(GetHttpDevices,SetPowerById,OpenGraphById,()=>_settings.ElectricityRateYenPerKwh,ReadWebHistoryAsync,_history,()=>_settings.WebViewGraphDeviceIds,()=>_settings.StaleDeviceMinutes);_http.Start(LocalHttpService.NormalizeBind(_settings.HttpBind),_settings.HttpPort);SetStatus(BuildHttpStatus(),false);}else SetStatus("Webサーバーを停止しました。",false);}
  catch(Exception ex){SetStatus("HTTPサービス開始失敗: "+ex.Message,false,true);}
 }
 private string BuildHttpStatus()
 {
  if(_http is null) return "Webサーバーを停止しました。";
  var urls=_http.ActiveUrls.Count>0?string.Join(" / ",_http.ActiveUrls):$"http://localhost:{_settings.HttpPort}/";
  return string.IsNullOrWhiteSpace(_http.LastWarning)?$"Webサーバーを開始しました: {urls}":$"Webサーバーを開始しました: {urls}  ※ {_http.LastWarning}";
 }

 private async void ShowSettings(){var w=new SettingsWindow(_settings,_settingsService){Owner=this};if(w.ShowDialog()==true){SetStatus("設定を保存しました。再探索しています…",true);try{await _devices.RefreshMetadataAsync();}catch{}}}
 private void UpdateClock(){ClockText.Text=$"現在 {DateTime.Now:HH:mm:ss}";}
 private static bool IsErrorStatus(string text)=>text.Contains("失敗",StringComparison.Ordinal)||text.Contains("エラー",StringComparison.Ordinal)||text.Contains("タイムアウト",StringComparison.Ordinal);
 private void SetStatus(string text,bool busy,bool showRetry=false)
 {
  StatusText.Text=text;BusyProgress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;RetryButton.Visibility=showRetry?Visibility.Visible:Visibility.Collapsed;
  if(IsErrorStatus(text)) ErrorTimeText.Text=$"最終エラー {DateTime.Now:HH:mm:ss}";
 }
 private async void RetryButton_Click(object sender,RoutedEventArgs e){RetryButton.Visibility=Visibility.Collapsed;try{SetStatus("再試行しています…",true);await _devices.RefreshMetadataAsync();}catch{}}
 private void TabChanged(object s,SelectionChangedEventArgs e){}
 private void SaveSettings(){AppLog.Debug("Saving settings");_settings.Left=Left;_settings.Top=Top;_settings.Width=Width;_settings.Height=Height;_settingsService.Save(_settings);}
 private void OnClosing(object? s,System.ComponentModel.CancelEventArgs e){AppLog.Info("Main window closing");SaveSettings();if(!_exit){e.Cancel=true;Hide();}}
 private void OpenHelp(){var p=System.IO.Path.Combine(AppContext.BaseDirectory,"Help_ja.md");if(System.IO.File.Exists(p))Process.Start(new ProcessStartInfo(p){UseShellExecute=true});}
 private void Exit(){_exit=true;foreach(var w in _graphWindows.Values.ToList())w.Close();_graphWindows.Clear();_switchTrayWindow?.Close();_trayClickTimer?.Stop();_trayClickTimer?.Dispose();_clockTimer.Stop();_http?.Dispose();_devices.Dispose();_transport.Dispose();if(_tray!=null)_tray.Visible=false;Close();System.Windows.Application.Current.Shutdown();}
 private sealed class DarkMenuRenderer:Forms.ToolStripProfessionalRenderer
 {
  public DarkMenuRenderer():base(new DarkColorTable()){}
  protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e){e.TextColor=System.Drawing.Color.White;base.OnRenderItemText(e);}
  protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e){using var p=new System.Drawing.Pen(System.Drawing.Color.FromArgb(70,78,90));e.Graphics.DrawRectangle(p,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);}
 }
 private sealed class DarkColorTable:Forms.ProfessionalColorTable
 {
  public override System.Drawing.Color ToolStripDropDownBackground=>System.Drawing.Color.FromArgb(32,37,45);
  public override System.Drawing.Color ImageMarginGradientBegin=>ToolStripDropDownBackground;
  public override System.Drawing.Color ImageMarginGradientMiddle=>ToolStripDropDownBackground;
  public override System.Drawing.Color ImageMarginGradientEnd=>ToolStripDropDownBackground;
  public override System.Drawing.Color MenuItemSelected=>System.Drawing.Color.FromArgb(56,67,82);
  public override System.Drawing.Color MenuItemBorder=>System.Drawing.Color.FromArgb(83,98,119);
  public override System.Drawing.Color MenuBorder=>System.Drawing.Color.FromArgb(70,78,90);
  public override System.Drawing.Color SeparatorDark=>System.Drawing.Color.FromArgb(70,78,90);
  public override System.Drawing.Color SeparatorLight=>System.Drawing.Color.FromArgb(70,78,90);
 }

}
