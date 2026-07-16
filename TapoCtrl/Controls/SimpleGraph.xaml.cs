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
 private bool _showCurrent=true,_showMin=true,_showMax=true,_showAverage=true;
 private string _unit="";
 public SimpleGraph(){InitializeComponent();SizeChanged+=(_,__)=>Redraw();}
 public void SetPoints(IReadOnlyList<HistoryPoint> p,string unit="",bool showCurrent=true,bool showMin=true,bool showMax=true,bool showAverage=true){_points=p;_unit=unit;_showCurrent=showCurrent;_showMin=showMin;_showMax=showMax;_showAverage=showAverage;Redraw();}
 private void Redraw()
 {
  Plot.Children.Clear();if(ActualWidth<150||ActualHeight<120)return;
  const double left=74,right=22,top=28,bottom=48;var w=Math.Max(10,ActualWidth-left-right);var h=Math.Max(10,ActualHeight-top-bottom);
  if(_points.Count==0){AddText("履歴データがありません",left+20,top+20,16,WpfBrushes.LightGray);return;}
  var minValue=Math.Min(0,_points.Min(x=>x.Value));var maxValue=_points.Max(x=>x.Value);if(Math.Abs(maxValue-minValue)<.001)maxValue=minValue+1;
  var padding=(maxValue-minValue)*.08;maxValue+=padding;minValue=Math.Max(0,minValue-padding);
  var start=_points[0].Time;var end=_points[^1].Time;var span=Math.Max(1,(end-start).TotalSeconds);
  for(var i=0;i<=5;i++)
  {
   var y=top+h*i/5;AddLine(left,y,left+w,y,WpfColor.FromArgb(75,150,155,165),1);
   var value=maxValue-(maxValue-minValue)*i/5;AddText($"{value:0.#}",5,y-10,12,WpfBrushes.LightGray,64,TextAlignment.Right);
  }
  var tickCount=6;
  for(var i=0;i<=tickCount;i++)
  {
   var x=left+w*i/tickCount;AddLine(x,top,x,top+h,WpfColor.FromArgb(55,150,155,165),1);
   var t=start+TimeSpan.FromSeconds(span*i/tickCount);AddText(t.ToString("HH:mm",CultureInfo.InvariantCulture),x-28,top+h+10,12,WpfBrushes.LightGray,56,TextAlignment.Center);
  }
  AddLine(left,top,left,top+h,WpfColors.Gray,1.3);AddLine(left,top+h,left+w,top+h,WpfColors.Gray,1.3);
  AddText(string.IsNullOrWhiteSpace(_unit)?"値":_unit,8,4,14,WpfBrushes.White);
  double X(DateTime t)=>left+(t-start).TotalSeconds/span*w;
  double Y(double v)=>top+(maxValue-v)/(maxValue-minValue)*h;
  var line=new Polyline{Stroke=WpfBrushes.DeepPink,StrokeThickness=2.6};foreach(var p in _points)line.Points.Add(new WpfPoint(X(p.Time),Y(p.Value)));Plot.Children.Add(line);
  var stats=new GraphStatistics{Current=_points[^1].Value,Minimum=_points.Min(x=>x.Value),Maximum=_points.Max(x=>x.Value),Average=_points.Average(x=>x.Value)};
  if(_showMin)AddReference("最小",stats.Minimum,WpfBrushes.DeepSkyBlue,Y,left,w);
  if(_showMax)AddReference("最大",stats.Maximum,WpfBrushes.Orange,Y,left,w);
  if(_showAverage)AddReference("平均",stats.Average,WpfBrushes.MediumPurple,Y,left,w);
  if(_showCurrent)AddReference("現在",stats.Current,WpfBrushes.LightGreen,Y,left,w);
 }
 private string FormatValue(double value)=>_unit=="W"?$"{value:0}":$"{value:0.0}";
 private void AddReference(string name,double value,WpfBrush brush,Func<double,double> yMap,double left,double width)
 {
  var y=yMap(value);var line=new Line{X1=left,X2=left+width,Y1=y,Y2=y,Stroke=brush,StrokeThickness=1,StrokeDashArray=new DoubleCollection{5,4},Opacity=.85};Plot.Children.Add(line);AddText($"{name} {FormatValue(value)}{_unit}",left+6,Math.Max(3,y-19),11,brush);
 }
 private void AddLine(double x1,double y1,double x2,double y2,WpfColor color,double thickness){Plot.Children.Add(new Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=new SolidColorBrush(color),StrokeThickness=thickness});}
 private void AddText(string text,double x,double y,double size,WpfBrush brush,double width=double.NaN,TextAlignment align=TextAlignment.Left){var t=new TextBlock{Text=text,FontSize=size,Foreground=brush,TextAlignment=align};if(!double.IsNaN(width))t.Width=width;Canvas.SetLeft(t,x);Canvas.SetTop(t,y);Plot.Children.Add(t);}
}
