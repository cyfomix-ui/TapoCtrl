using System.Windows;
using System.Windows.Controls;
using TapoCtrl.Models;
using TapoCtrl.Services;
namespace TapoCtrl.Windows;
public partial class GraphWindow:Window
{
 private IReadOnlyList<HistoryPoint> _points;
 private IReadOnlyList<HistoryPoint> _secondaryPoints;
 private readonly Func<Task<(IReadOnlyList<HistoryPoint> Primary,IReadOnlyList<HistoryPoint> Secondary)>>? _reload;
 private readonly System.Windows.Threading.DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromMinutes(1)};
 private readonly string _unit,_secondaryUnit;
 private readonly Func<PowerEnergyStatistics>? _energyProvider;
 private readonly DateOnly _date;
 private readonly Func<DateOnly,Task>? _openDate;
 private bool _calendarReady;
 public string GraphKey{get;}
 public GraphWindow(string key,string name,string unit,IReadOnlyList<HistoryPoint> points,DateOnly date,IReadOnlyList<DateOnly> availableDates,Func<DateOnly,Task>? openDate=null,IReadOnlyList<HistoryPoint>? secondaryPoints=null,string secondaryUnit="",Func<Task<(IReadOnlyList<HistoryPoint> Primary,IReadOnlyList<HistoryPoint> Secondary)>>? reload=null,Func<PowerEnergyStatistics>? energyProvider=null)
 {
  InitializeComponent();GraphKey=key;_date=date;_openDate=openDate;Title=$"TapoCtrl - {name} - {date:yyyy-MM-dd}";DeviceNameText.Text=$"{name}  {date:yyyy-MM-dd}";_unit=unit;_points=points;_secondaryPoints=secondaryPoints??[];_secondaryUnit=secondaryUnit;_reload=reload;_energyProvider=energyProvider;
  ConfigureCalendar(availableDates);
  CalendarButton.Click+=(_,__)=>CalendarPopup.IsOpen=!CalendarPopup.IsOpen;
  HistoryCalendar.SelectedDatesChanged+=CalendarSelected;
  Loaded+=(_,__)=>{Graph.SetDisplayDate(_date);RefreshGraph();if(IsToday&&_reload is not null)_refreshTimer.Start();};
  Closed+=(_,__)=>{_refreshTimer.Stop();_refreshTimer.Tick-=RefreshTimerTick;HistoryCalendar.SelectedDatesChanged-=CalendarSelected;};
  _refreshTimer.Tick+=RefreshTimerTick;
 }
 private bool IsToday=>_date==DateOnly.FromDateTime(DateTime.Now);
 private async void RefreshTimerTick(object? sender,EventArgs e)=>await ReloadGraphAsync();
 private void ConfigureCalendar(IReadOnlyList<DateOnly> dates)
 {
  var available=dates.Distinct().OrderBy(x=>x).ToList();
  if(available.Count>0)
  {
   HistoryCalendar.DisplayDateStart=available[0].ToDateTime(TimeOnly.MinValue);HistoryCalendar.DisplayDateEnd=available[^1].ToDateTime(TimeOnly.MinValue);HistoryCalendar.DisplayDate=(available.Contains(_date)?_date:available[^1]).ToDateTime(TimeOnly.MinValue);
   var set=available.ToHashSet();for(var d=available[0];d<=available[^1];d=d.AddDays(1))if(!set.Contains(d))HistoryCalendar.BlackoutDates.Add(new CalendarDateRange(d.ToDateTime(TimeOnly.MinValue)));if(set.Contains(_date))HistoryCalendar.SelectedDate=_date.ToDateTime(TimeOnly.MinValue);
  }
  else{HistoryCalendar.IsEnabled=false;CalendarButton.IsEnabled=false;HistoryCalendar.DisplayDate=_date.ToDateTime(TimeOnly.MinValue);}
  _calendarReady=true;
 }
 private async void CalendarSelected(object? sender,SelectionChangedEventArgs e)
 {
  if(!_calendarReady||HistoryCalendar.SelectedDate is not DateTime selected)return;
  var date=DateOnly.FromDateTime(selected);CalendarPopup.IsOpen=false;
  if(date==_date)return;
  try{if(_openDate is not null)await _openDate(date);}catch(Exception ex){AppLog.Error("履歴日付グラフを開けませんでした",ex);System.Windows.MessageBox.Show(this,$"グラフを開けませんでした: {ex.Message}","TapoCtrl",MessageBoxButton.OK,MessageBoxImage.Error);}
  finally{if(!HistoryCalendar.BlackoutDates.Contains(_date.ToDateTime(TimeOnly.MinValue)))HistoryCalendar.SelectedDate=_date.ToDateTime(TimeOnly.MinValue);else HistoryCalendar.SelectedDate=null;}
 }
 private async Task ReloadGraphAsync()
 {
  if(!IsToday||_reload is null)return;
  try{var updated=await _reload();_points=updated.Primary;_secondaryPoints=updated.Secondary;RefreshGraph();}
  catch(Exception ex){AppLog.Warn($"グラフ更新失敗: {ex.Message}");}
 }
 private string F(double v,string u)=>u=="W"?$"{v:0} W":u=="%"?$"{v:0} %":$"{v:0.0} {u}";
 private void RefreshGraph()
 {
  Graph.SetDisplayDate(_date);
  if(_secondaryPoints.Count>0)Graph.SetDualPoints(_points,_unit,_secondaryPoints,_secondaryUnit,true,true,true,true);else Graph.SetPoints(_points,_unit,true,true,true,true);
  if(_points.Count==0&&_secondaryPoints.Count==0){StatsCards.Visibility=Visibility.Collapsed;LegendPanel.Visibility=Visibility.Collapsed;PowerEnergyCards.Visibility=Visibility.Collapsed;HeaderCurrentText.Text="";HeaderTimeText.Text="";StatsText.Text="データなし";return;}
  StatsCards.Visibility=Visibility.Visible;LegendPanel.Visibility=_secondaryPoints.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(_points.Count>0){var latest=_points[^1];HeaderCurrentText.Text=$"現在 {F(latest.Value,_unit)}";if(_secondaryPoints.Count>0)HeaderCurrentText.Text+=$" / {F(_secondaryPoints[^1].Value,_secondaryUnit)}";HeaderTimeText.Text=latest.Time.ToString("HH:mm");CurrentText.Text=F(latest.Value,_unit);MinimumText.Text=F(_points.Min(x=>x.Value),_unit);MaximumText.Text=F(_points.Max(x=>x.Value),_unit);AverageText.Text=F(_points.Average(x=>x.Value),_unit);}
  var dual=_secondaryPoints.Count>0;PowerEnergyCards.Visibility=_unit=="W"&&IsToday?Visibility.Visible:Visibility.Collapsed;
  if(_unit=="W"&&IsToday){var energy=_energyProvider?.Invoke()??new PowerEnergyStatistics();TodayTotalText.Text=$"{energy.TodayWh/1000.0:0.000} kWh";TodayCostText.Text=$"{energy.TodayCostYen:N1} 円";MonthTotalText.Text=$"{energy.MonthWh/1000.0:0.000} kWh";MonthCostText.Text=$"{energy.MonthCostYen:N1} 円";}
  HumidityCurrentCard.Visibility=dual?Visibility.Visible:Visibility.Collapsed;HumidityAverageCard.Visibility=dual?Visibility.Visible:Visibility.Collapsed;StatsCards.Columns=dual?6:4;
  if(dual){HumidityCurrentText.Text=F(_secondaryPoints[^1].Value,_secondaryUnit);HumidityAverageText.Text=F(_secondaryPoints.Average(x=>x.Value),_secondaryUnit);}
  var all=_points.Concat(_secondaryPoints).OrderBy(x=>x.Time).ToList();StatsText.Text=all.Count==0?"データなし":$"表示 {_date:yyyy-MM-dd} 00:00～24:00　実データ {all[0].Time:HH:mm}～{all[^1].Time:HH:mm}";
 }
}
