using System.Windows;
using TapoCtrl.Models;
namespace TapoCtrl.Windows;
public partial class GraphWindow:Window
{
 private IReadOnlyList<HistoryPoint> _points;
 private IReadOnlyList<HistoryPoint> _secondaryPoints;
 private readonly Func<Task<(IReadOnlyList<HistoryPoint> Primary,IReadOnlyList<HistoryPoint> Secondary)>>? _reload;
 private readonly System.Windows.Threading.DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromMinutes(1)};
 private readonly string _unit,_secondaryUnit;
 public string GraphKey{get;}
 public GraphWindow(string key,string name,string unit,IReadOnlyList<HistoryPoint> points,IReadOnlyList<HistoryPoint>? secondaryPoints=null,string secondaryUnit="",Func<Task<(IReadOnlyList<HistoryPoint> Primary,IReadOnlyList<HistoryPoint> Secondary)>>? reload=null)
 {
  InitializeComponent();GraphKey=key;Title=$"TapoCtrl - {name} - 24時間";DeviceNameText.Text=name;_unit=unit;_points=points;_secondaryPoints=secondaryPoints??[];_secondaryUnit=secondaryUnit;_reload=reload;
  Loaded+=(_,__)=>{RefreshGraph();_refreshTimer.Start();};
  Closed+=(_,__)=>_refreshTimer.Stop();
  _refreshTimer.Tick+=async (_,__)=>await ReloadGraphAsync();
 }
 private async Task ReloadGraphAsync()
 {
  if(_reload is null){RefreshGraph();return;}
  try{var updated=await _reload();_points=updated.Primary;_secondaryPoints=updated.Secondary;RefreshGraph();}
  catch{ /* 次回の1分更新で再試行する */ }
 }
 private string F(double v,string u)=>u=="W"?$"{v:0} W":u=="%"?$"{v:0} %":$"{v:0.0} {u}";
 private void RefreshGraph()
 {
  if(_secondaryPoints.Count>0)Graph.SetDualPoints(_points,_unit,_secondaryPoints,_secondaryUnit,true,true,true,true);
  else Graph.SetPoints(_points,_unit,true,true,true,true);
  if(_points.Count==0&&_secondaryPoints.Count==0){StatsCards.Visibility=Visibility.Collapsed;LegendPanel.Visibility=Visibility.Collapsed;HeaderCurrentText.Text="";HeaderTimeText.Text="";StatsText.Text="履歴データがありません。監視開始後、1分ごとに記録されます。";return;}
  StatsCards.Visibility=Visibility.Visible;
  LegendPanel.Visibility=_secondaryPoints.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(_points.Count>0)
  {
   var latest=_points[^1];
   HeaderCurrentText.Text=$"現在 {F(latest.Value,_unit)}";
   if(_secondaryPoints.Count>0) HeaderCurrentText.Text+=$" / {F(_secondaryPoints[^1].Value,_secondaryUnit)}";
   HeaderTimeText.Text=latest.Time.ToString("HH:mm");
   CurrentText.Text=F(latest.Value,_unit);
   MinimumText.Text=F(_points.Min(x=>x.Value),_unit);
   MaximumText.Text=F(_points.Max(x=>x.Value),_unit);
   AverageText.Text=F(_points.Average(x=>x.Value),_unit);
  }
  var dual=_secondaryPoints.Count>0;
  HumidityCurrentCard.Visibility=dual?Visibility.Visible:Visibility.Collapsed;
  HumidityAverageCard.Visibility=dual?Visibility.Visible:Visibility.Collapsed;
  StatsCards.Columns=dual?6:4;
  if(dual)
  {
   HumidityCurrentText.Text=F(_secondaryPoints[^1].Value,_secondaryUnit);
   HumidityAverageText.Text=F(_secondaryPoints.Average(x=>x.Value),_secondaryUnit);
  }
  var all=_points.Concat(_secondaryPoints).OrderBy(x=>x.Time).ToList();
  StatsText.Text=$"表示 {all[0].Time:MM/dd HH:mm}～{all[^1].Time:MM/dd HH:mm}";
 }
}
