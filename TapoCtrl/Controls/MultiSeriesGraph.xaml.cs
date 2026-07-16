using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TapoCtrl.Models;
using WpfBrushes=System.Windows.Media.Brushes;
using WpfColor=System.Windows.Media.Color;
using WpfPoint=System.Windows.Point;
namespace TapoCtrl.Controls;
public sealed class GraphSeries
{
 public string Name { get; set; }="";
 public IReadOnlyList<HistoryPoint> Points { get; set; }=[];
}
public partial class MultiSeriesGraph:System.Windows.Controls.UserControl
{
 private IReadOnlyList<GraphSeries> _series=[];
 private string _unit="";
 private static readonly System.Windows.Media.Brush[] Palette=[WpfBrushes.DeepPink,WpfBrushes.DeepSkyBlue,WpfBrushes.Gold,WpfBrushes.LightGreen,WpfBrushes.Orange,WpfBrushes.MediumPurple,WpfBrushes.Cyan,WpfBrushes.LightCoral,WpfBrushes.Lime,WpfBrushes.White];
 public MultiSeriesGraph(){InitializeComponent();SizeChanged+=(_,__)=>Redraw();}
 public void SetSeries(IReadOnlyList<GraphSeries> series,string unit){_series=series;_unit=unit;Redraw();}
 private void Redraw()
 {
  Plot.Children.Clear();if(ActualWidth<180||ActualHeight<140)return;
  const double left=74,right=24,top=30,bottom=50;var w=Math.Max(10,ActualWidth-left-right);var h=Math.Max(10,ActualHeight-top-bottom);
  var all=_series.SelectMany(s=>s.Points).OrderBy(p=>p.Time).ToList();
  if(all.Count==0){AddText("履歴データがありません",left+20,top+20,16,WpfBrushes.LightGray);return;}
  var minValue=Math.Min(0,all.Min(x=>x.Value));var maxValue=all.Max(x=>x.Value);if(Math.Abs(maxValue-minValue)<.001)maxValue=minValue+1;
  var padding=(maxValue-minValue)*.08;maxValue+=padding;minValue=Math.Max(0,minValue-padding);
  var start=all[0].Time;var end=all[^1].Time;var span=Math.Max(1,(end-start).TotalSeconds);
  for(var i=0;i<=5;i++)
  {
   var y=top+h*i/5;AddLine(left,y,left+w,y,WpfColor.FromArgb(70,150,155,165),1);
   var value=maxValue-(maxValue-minValue)*i/5;AddText($"{value:0.#}",5,y-10,12,WpfBrushes.LightGray,64,TextAlignment.Right);
  }
  for(var i=0;i<=6;i++){var x=left+w*i/6;AddLine(x,top,x,top+h,WpfColor.FromArgb(45,150,155,165),1);var t=start+TimeSpan.FromSeconds(span*i/6);AddText(t.ToString("HH:mm",CultureInfo.InvariantCulture),x-28,top+h+10,12,WpfBrushes.LightGray,56,TextAlignment.Center);}
  AddLine(left,top,left,top+h,Colors.Gray,1.3);AddLine(left,top+h,left+w,top+h,Colors.Gray,1.3);
  AddText(string.IsNullOrWhiteSpace(_unit)?"値":_unit,8,4,14,WpfBrushes.White);
  double X(DateTime t)=>left+(t-start).TotalSeconds/span*w;
  double Y(double v)=>top+(maxValue-v)/(maxValue-minValue)*h;
  for(var i=0;i<_series.Count;i++)
  {
   var points=_series[i].Points.OrderBy(p=>p.Time).ToList();if(points.Count==0)continue;
   var brush=Palette[i%Palette.Length];var line=new Polyline{Stroke=brush,StrokeThickness=2.4};
   foreach(var p in points)line.Points.Add(new WpfPoint(X(p.Time),Y(p.Value)));Plot.Children.Add(line);
   var lx=left+12+(i%4)*170;var ly=top+8+(i/4)*22;AddLine(lx,ly+8,lx+26,ly+8,((SolidColorBrush)brush).Color,3);AddText(_series[i].Name,lx+32,ly,12,brush,135,TextAlignment.Left);
  }
 }
 private void AddLine(double x1,double y1,double x2,double y2,WpfColor color,double thickness){Plot.Children.Add(new Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=new SolidColorBrush(color),StrokeThickness=thickness});}
 private void AddLine(double x1,double y1,double x2,double y2,WpfColor color,double thickness,double opacity){var l=new Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=new SolidColorBrush(color),StrokeThickness=thickness,Opacity=opacity};Plot.Children.Add(l);}
 private void AddText(string text,double x,double y,double size,System.Windows.Media.Brush brush,double width=260,TextAlignment align=TextAlignment.Left)
 {
  var tb=new TextBlock{Text=text,FontSize=size,Foreground=brush,Width=width,TextAlignment=align,TextTrimming=TextTrimming.CharacterEllipsis};
  Canvas.SetLeft(tb,x);Canvas.SetTop(tb,y);Plot.Children.Add(tb);
 }
}
