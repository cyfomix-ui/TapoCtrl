using System.Windows;
using TapoCtrl.Controls;
namespace TapoCtrl.Windows;
public partial class SeriesGraphWindow:Window
{
 public string GraphKey{get;}
 public SeriesGraphWindow(string key,string title,IReadOnlyList<GraphSeries> first,string firstUnit,IReadOnlyList<GraphSeries>? second=null,string secondUnit="")
 {
  InitializeComponent();GraphKey=key;Title=$"TapoCtrl - {title} - 系列グラフ";HeaderText.Text=title;
  if(second is null)
  {
   GraphGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   var graph=new MultiSeriesGraph();
   GraphGrid.Children.Add(graph);
   graph.SetSeries(first,firstUnit);
  }
  else
  {
   GraphGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   GraphGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=new GridLength(1,GridUnitType.Star)});

   var tempTitle=new System.Windows.Controls.TextBlock{Text="温度",Foreground=System.Windows.Media.Brushes.LightGray,Margin=new Thickness(4,0,0,4)};
   var humTitle=new System.Windows.Controls.TextBlock{Text="湿度",Foreground=System.Windows.Media.Brushes.LightGray,Margin=new Thickness(4,8,0,4)};

   var tempGrid=new System.Windows.Controls.Grid();
   tempGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=GridLength.Auto});
   tempGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=new GridLength(1,GridUnitType.Star)});

   var humGrid=new System.Windows.Controls.Grid();
   humGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=GridLength.Auto});
   humGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition{Height=new GridLength(1,GridUnitType.Star)});

   var g1=new MultiSeriesGraph();
   var g2=new MultiSeriesGraph();

   tempGrid.Children.Add(tempTitle);
   System.Windows.Controls.Grid.SetRow(g1,1);
   tempGrid.Children.Add(g1);

   humGrid.Children.Add(humTitle);
   System.Windows.Controls.Grid.SetRow(g2,1);
   humGrid.Children.Add(g2);

   GraphGrid.Children.Add(tempGrid);
   System.Windows.Controls.Grid.SetRow(humGrid,1);
   GraphGrid.Children.Add(humGrid);

   g1.SetSeries(first,firstUnit);
   g2.SetSeries(second,secondUnit);
  }
 }
}
