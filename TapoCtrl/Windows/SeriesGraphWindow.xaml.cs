using System.Windows;
using System.Windows.Controls;
using TapoCtrl.Controls;
using TapoCtrl.Services;
using TapoCtrl.Models;
namespace TapoCtrl.Windows;
public partial class SeriesGraphWindow:Window
{
 public string GraphKey{get;}
 private readonly DateOnly _date;
 private readonly Func<DateOnly,Task>? _openDate;
 private readonly Func<Task<(IReadOnlyList<GraphSeries> First,IReadOnlyList<GraphSeries>? Second)>>? _reload;
 private readonly string _firstUnit,_secondUnit;
 private readonly Func<PowerEnergyStatistics>? _energyProvider;
 private readonly MultiSeriesGraph _firstGraph=new();
 private readonly MultiSeriesGraph? _secondGraph;
 private readonly System.Windows.Threading.DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromMinutes(1)};
 private bool _calendarReady,_refreshing;
 public SeriesGraphWindow(string key,string title,IReadOnlyList<GraphSeries> first,string firstUnit,DateOnly date,IReadOnlyList<DateOnly> availableDates,Func<DateOnly,Task>? openDate=null,IReadOnlyList<GraphSeries>? second=null,string secondUnit="",Func<Task<(IReadOnlyList<GraphSeries> First,IReadOnlyList<GraphSeries>? Second)>>? reload=null,Func<PowerEnergyStatistics>? energyProvider=null)
 {
  InitializeComponent();GraphKey=key;_date=date;_openDate=openDate;_reload=reload;_energyProvider=energyProvider;_firstUnit=firstUnit;_secondUnit=secondUnit;Title=$"TapoCtrl - {title} - {date:yyyy-MM-dd}";HeaderText.Text=$"{title}  {date:yyyy-MM-dd}";ConfigureCalendar(availableDates);CalendarButton.Click+=(_,__)=>CalendarPopup.IsOpen=!CalendarPopup.IsOpen;HistoryCalendar.SelectedDatesChanged+=CalendarSelected;
  if(second is null){GraphGrid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});GraphGrid.Children.Add(_firstGraph);_firstGraph.SetSeries(first,firstUnit,date);}
  else
  {
   _secondGraph=new MultiSeriesGraph();GraphGrid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});GraphGrid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   var tempTitle=new TextBlock{Text="温度",Foreground=System.Windows.Media.Brushes.LightGray,Margin=new Thickness(4,0,0,4)};var humTitle=new TextBlock{Text="湿度",Foreground=System.Windows.Media.Brushes.LightGray,Margin=new Thickness(4,8,0,4)};
   var tempGrid=new Grid();tempGrid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});tempGrid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});var humGrid=new Grid();humGrid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});humGrid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   tempGrid.Children.Add(tempTitle);Grid.SetRow(_firstGraph,1);tempGrid.Children.Add(_firstGraph);humGrid.Children.Add(humTitle);Grid.SetRow(_secondGraph,1);humGrid.Children.Add(_secondGraph);GraphGrid.Children.Add(tempGrid);Grid.SetRow(humGrid,1);GraphGrid.Children.Add(humGrid);_firstGraph.SetSeries(first,firstUnit,date);_secondGraph.SetSeries(second,secondUnit,date);
  }
  RefreshEnergyCards();
  _refreshTimer.Tick+=RefreshTimerTick;Loaded+=(_,__)=>{if(IsToday&&_reload is not null)_refreshTimer.Start();};Closed+=(_,__)=>{_refreshTimer.Stop();_refreshTimer.Tick-=RefreshTimerTick;HistoryCalendar.SelectedDatesChanged-=CalendarSelected;};
 }
 private bool IsToday=>_date==DateOnly.FromDateTime(DateTime.Now);
 private async void RefreshTimerTick(object? sender,EventArgs e){if(_refreshing||!IsToday||_reload is null)return;_refreshing=true;try{var data=await _reload();_firstGraph.SetSeries(data.First,_firstUnit,_date);if(_secondGraph is not null)_secondGraph.SetSeries(data.Second??[],_secondUnit,_date);RefreshEnergyCards();}catch(Exception ex){AppLog.Warn($"系列グラフ更新失敗: {ex.Message}");}finally{_refreshing=false;}}
 private void RefreshEnergyCards()
 {
  var isPower=_firstUnit=="W";
  PowerEnergyCards.Visibility=isPower&&IsToday&&_energyProvider is not null?Visibility.Visible:Visibility.Collapsed;
  PastEnergyNote.Visibility=isPower&&!IsToday?Visibility.Visible:Visibility.Collapsed;
  if(PowerEnergyCards.Visibility!=Visibility.Visible)return;
  var energy=_energyProvider!();
  TodayTotalText.Text=$"{energy.TodayWh/1000.0:0.000} kWh";
  TodayCostText.Text=$"{energy.TodayCostYen:N1} 円";
  MonthTotalText.Text=$"{energy.MonthWh/1000.0:0.000} kWh";
  MonthCostText.Text=$"{energy.MonthCostYen:N1} 円";
 }
 private void ConfigureCalendar(IReadOnlyList<DateOnly> dates){var available=dates.Distinct().OrderBy(x=>x).ToList();if(available.Count>0){HistoryCalendar.DisplayDateStart=available[0].ToDateTime(TimeOnly.MinValue);HistoryCalendar.DisplayDateEnd=available[^1].ToDateTime(TimeOnly.MinValue);HistoryCalendar.DisplayDate=(available.Contains(_date)?_date:available[^1]).ToDateTime(TimeOnly.MinValue);var set=available.ToHashSet();for(var d=available[0];d<=available[^1];d=d.AddDays(1))if(!set.Contains(d))HistoryCalendar.BlackoutDates.Add(new CalendarDateRange(d.ToDateTime(TimeOnly.MinValue)));if(set.Contains(_date))HistoryCalendar.SelectedDate=_date.ToDateTime(TimeOnly.MinValue);}else{HistoryCalendar.IsEnabled=false;CalendarButton.IsEnabled=false;HistoryCalendar.DisplayDate=_date.ToDateTime(TimeOnly.MinValue);}_calendarReady=true;}
 private async void CalendarSelected(object? sender,SelectionChangedEventArgs e){if(!_calendarReady||HistoryCalendar.SelectedDate is not DateTime selected)return;var date=DateOnly.FromDateTime(selected);CalendarPopup.IsOpen=false;if(date==_date)return;try{if(_openDate is not null)await _openDate(date);}catch(Exception ex){AppLog.Error("系列履歴日付グラフを開けませんでした",ex);System.Windows.MessageBox.Show(this,$"系列グラフを開けませんでした: {ex.Message}","TapoCtrl",MessageBoxButton.OK,MessageBoxImage.Error);}finally{if(!HistoryCalendar.BlackoutDates.Contains(_date.ToDateTime(TimeOnly.MinValue)))HistoryCalendar.SelectedDate=_date.ToDateTime(TimeOnly.MinValue);else HistoryCalendar.SelectedDate=null;}}
}
