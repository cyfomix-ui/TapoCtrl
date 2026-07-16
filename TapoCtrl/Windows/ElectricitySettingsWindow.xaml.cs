using System.Globalization;using System.Windows;using TapoCtrl.Models;namespace TapoCtrl.Windows;
public partial class ElectricitySettingsWindow:Window
{
 private readonly AppSettings _settings;
 public ElectricitySettingsWindow(AppSettings settings){InitializeComponent();_settings=settings;RegionBox.Text=settings.ElectricityRegion;CompanyBox.Text=settings.ElectricityCompany;CapacityBox.Text=settings.ContractCapacity;RateBox.Text=settings.ElectricityRateYenPerKwh.ToString("0.00",CultureInfo.InvariantCulture);}
 private void Save_Click(object sender,RoutedEventArgs e){if(!double.TryParse(RateBox.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var rate)&&!double.TryParse(RateBox.Text,out rate)){System.Windows.MessageBox.Show("目安単価を数値で入力してください。","TapoCtrl");return;}_settings.ElectricityRegion=RegionBox.Text.Trim();_settings.ElectricityCompany=CompanyBox.Text.Trim();_settings.ContractCapacity=CapacityBox.Text.Trim();_settings.ElectricityRateYenPerKwh=Math.Max(0,rate);DialogResult=true;}
 private void Cancel_Click(object sender,RoutedEventArgs e)=>DialogResult=false;
}
