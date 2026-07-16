using System.Windows;using TapoCtrl.Models;namespace TapoCtrl.Windows;
public partial class GraphWindow:Window
{
 private readonly IReadOnlyList<HistoryPoint> _points;private readonly string _unit;
 public string GraphKey{get;}
 public GraphWindow(string key,string name,string unit,IReadOnlyList<HistoryPoint> points){InitializeComponent();GraphKey=key;Title=$"TapoCtrl - {name} - 24時間";_unit=unit;_points=points;Loaded+=(_,__)=>RefreshGraph();}
 private void Toggle(object s,RoutedEventArgs e){if(IsLoaded)RefreshGraph();}
 private void RefreshGraph(){Graph.SetPoints(_points,_unit,ValueCheck.IsChecked==true,MinCheck.IsChecked==true,MaxCheck.IsChecked==true,AverageCheck.IsChecked==true);if(_points.Count==0){StatsText.Text="履歴データがありません。監視開始後、1分ごとに記録されます。";return;}string F(double v)=>_unit=="W"?$"{v:0}":$"{v:0.0}";StatsText.Text=$"現在 {F(_points[^1].Value)}{_unit}　最小 {F(_points.Min(x=>x.Value))}{_unit}　最大 {F(_points.Max(x=>x.Value))}{_unit}　平均 {F(_points.Average(x=>x.Value))}{_unit}　表示 {_points[0].Time:MM/dd HH:mm}～{_points[^1].Time:MM/dd HH:mm}";}
}
