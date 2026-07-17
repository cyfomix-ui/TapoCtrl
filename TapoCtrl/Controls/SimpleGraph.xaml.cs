using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColors = System.Windows.Media.Colors;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using System.Windows.Shapes;
using TapoCtrl.Models;
namespace TapoCtrl.Controls;
public partial class SimpleGraph:System.Windows.Controls.UserControl
{
 private IReadOnlyList<HistoryPoint> _points=[];
 private IReadOnlyList<HistoryPoint> _secondaryPoints=[];
 private bool _showCurrent=true,_showMin=true,_showMax=true,_showAverage=true;
 private string _unit="",_secondaryUnit="";
 public SimpleGraph(){InitializeComponent();SizeChanged+=(_,__)=>Redraw();}
 public void SetPoints(IReadOnlyList<HistoryPoint> p,string unit="",bool showCurrent=true,bool showMin=true,bool showMax=true,bool showAverage=true)
 {
  _points=p;_secondaryPoints=[];_unit=unit;_secondaryUnit="";_showCurrent=showCurrent;_showMin=showMin;_showMax=showMax;_showAverage=showAverage;Redraw();
 }
 public void SetDualPoints(IReadOnlyList<HistoryPoint> primary,string primaryUnit,IReadOnlyList<HistoryPoint> secondary,string secondaryUnit,bool showCurrent=true,bool showMin=true,bool showMax=true,bool showAverage=true)
 {
  _points=primary;_secondaryPoints=secondary;_unit=primaryUnit;_secondaryUnit=secondaryUnit;_showCurrent=showCurrent;_showMin=showMin;_showMax=showMax;_showAverage=showAverage;Redraw();
 }
 private static (double Min,double Max) GetBounds(IReadOnlyList<HistoryPoint> points,string unit)
 {
  if(points.Count==0)return (0,1);
  var min=points.Min(x=>x.Value);var max=points.Max(x=>x.Value);
  if(unit=="%")
  {
   min=Math.Max(0,Math.Floor((min-5)/5)*5);max=Math.Min(100,Math.Ceiling((max+5)/5)*5);
  }
  else if(unit=="℃")
  {
   // 温度は0℃固定にしない。小さな変化も読めるよう、実測範囲の上下に余白を置く。
   var range=Math.Max(1.0,max-min);var pad=Math.Max(0.5,range*.18);
   min=Math.Floor((min-pad)*2)/2;max=Math.Ceiling((max+pad)*2)/2;
  }
  else
  {
   var range=Math.Max(1.0,max-min);var pad=Math.Max(1.0,range*.08);
   min=Math.Max(0,Math.Floor(min-pad));max=Math.Ceiling(max+pad);
  }
  if(max<=min)max=min+1;return (min,max);
 }
 private void Redraw()
 {
  Plot.Children.Clear();if(ActualWidth<150||ActualHeight<120)return;
  var dual=_secondaryPoints.Count>0;const double left=74,top=28,bottom=58;var right=dual?74:22;var w=Math.Max(10,ActualWidth-left-right);var h=Math.Max(10,ActualHeight-top-bottom);
  if(_points.Count==0&&_secondaryPoints.Count==0){AddText("履歴データがありません",left+20,top+20,16,WpfBrushes.LightGray);return;}
  var primaryBounds=GetBounds(_points,_unit);var secondaryBounds=GetBounds(_secondaryPoints,_secondaryUnit);
  var allTimes=_points.Select(x=>x.Time).Concat(_secondaryPoints.Select(x=>x.Time)).OrderBy(x=>x).ToList();
  var start=allTimes[0];var end=allTimes[^1];var span=Math.Max(60,(end-start).TotalSeconds);
  if((end-start).TotalSeconds<60)end=start.AddSeconds(60);
  for(var i=0;i<=5;i++)
  {
   var y=top+h*i/5;AddLine(left,y,left+w,y,WpfColor.FromArgb(75,150,155,165),1);
   var value=primaryBounds.Max-(primaryBounds.Max-primaryBounds.Min)*i/5;AddText($"{value:0.#}",5,y-10,12,WpfBrushes.LightGray,64,TextAlignment.Right);
   if(dual){var sv=secondaryBounds.Max-(secondaryBounds.Max-secondaryBounds.Min)*i/5;AddText($"{sv:0}",left+w+8,y-10,12,WpfBrushes.HotPink,58,TextAlignment.Left);}
  }
  var tickCount=6;
  for(var i=0;i<=tickCount;i++)
  {
   var x=left+w*i/tickCount;AddLine(x,top,x,top+h,WpfColor.FromArgb(55,150,155,165),1);
   var t=start+TimeSpan.FromSeconds(span*i/tickCount);var fmt=span>=20*3600?"MM/dd HH:mm":"HH:mm";AddText(t.ToString(fmt,CultureInfo.InvariantCulture),x-40,top+h+10,12,WpfBrushes.LightGray,80,TextAlignment.Center);
  }
  AddLine(left,top,left,top+h,WpfColors.Gray,1.3);AddLine(left,top+h,left+w,top+h,WpfColors.Gray,1.3);AddLine(left+w,top,left+w,top+h,WpfColors.Gray,1.3);
  AddText(string.IsNullOrWhiteSpace(_unit)?"値":_unit,8,4,14,WpfBrushes.White);
  if(dual)AddText(_secondaryUnit,left+w+38,4,14,WpfBrushes.HotPink);
  double X(DateTime t)=>left+(t-start).TotalSeconds/span*w;
  double Y1(double v)=>top+(primaryBounds.Max-v)/(primaryBounds.Max-primaryBounds.Min)*h;
  double Y2(double v)=>top+(secondaryBounds.Max-v)/(secondaryBounds.Max-secondaryBounds.Min)*h;
  if(dual)AddPolyline(_secondaryPoints,WpfBrushes.DeepPink,X,Y2,true);
  AddPolyline(_points,WpfBrushes.DeepSkyBlue,X,Y1,true);
  if(_points.Count>0)
  {
   var stats=new GraphStatistics{Current=_points[^1].Value,Minimum=_points.Min(x=>x.Value),Maximum=_points.Max(x=>x.Value),Average=_points.Average(x=>x.Value)};
   if(_showMin)AddReference("最小",stats.Minimum,WpfBrushes.DeepSkyBlue,Y1,left,w);
   if(_showMax)AddReference("最大",stats.Maximum,WpfBrushes.Orange,Y1,left,w);
   if(_showAverage)AddReference("平均",stats.Average,WpfBrushes.MediumPurple,Y1,left,w);
   if(_showCurrent)AddReference("現在",stats.Current,WpfBrushes.LightGreen,Y1,left,w);
  }
 }
 private void AddPolyline(IReadOnlyList<HistoryPoint> points,WpfBrush brush,Func<DateTime,double> xMap,Func<double,double> yMap,bool add=true)
 {
  if(points.Count==0)return;var line=new Polyline{Stroke=brush,StrokeThickness=2.6};foreach(var p in points)line.Points.Add(new WpfPoint(xMap(p.Time),yMap(p.Value)));Plot.Children.Add(line);
 }
 private string FormatValue(double value)=>_unit=="W"?$"{value:0}":$"{value:0.0}";
 private void AddReference(string name,double value,WpfBrush brush,Func<double,double> yMap,double left,double width)
 {
  var y=yMap(value);var line=new Line{X1=left,X2=left+width,Y1=y,Y2=y,Stroke=brush,StrokeThickness=1,StrokeDashArray=new DoubleCollection{5,4},Opacity=.85};Plot.Children.Add(line);AddText($"{name} {FormatValue(value)}{_unit}",left+6,Math.Max(3,y-19),11,brush);
 }
 private void AddLine(double x1,double y1,double x2,double y2,WpfColor color,double thickness){Plot.Children.Add(new Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=new SolidColorBrush(color),StrokeThickness=thickness});}
 private void AddText(string text,double x,double y,double size,WpfBrush brush,double width=double.NaN,TextAlignment align=TextAlignment.Left){var t=new TextBlock{Text=text,FontSize=size,Foreground=brush,TextAlignment=align};if(!double.IsNaN(width))t.Width=width;Canvas.SetLeft(t,x);Canvas.SetTop(t,y);Plot.Children.Add(t);}
}
