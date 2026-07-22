using System.Text.Json.Serialization;
namespace TapoCtrl.Models;
public enum DeviceKind { Power, Temperature, Humidity, Environment, Switch, Hub, Unknown }
public enum DeviceGroupKind { Power, Environment, Switch }
public sealed class DeviceSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Device";
    public string Ip { get; set; } = "";
    public string Hub { get; set; } = "";
    public string Model { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public DeviceKind Kind { get; set; }
    public double? PowerWatts { get; set; }
    public double? TodayWh { get; set; }
    public double? MonthWh { get; set; }
    public double? TodayCostYen { get; set; }
    public double? MonthCostYen { get; set; }
    public double? TemperatureC { get; set; }
    public double? HumidityPercent { get; set; }
    public bool? IsOn { get; set; }
    public bool Online { get; set; } = true;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    [JsonIgnore] public bool IsPowerSummary => Id == "__power_summary__";
    public DeviceGroupKind GroupKind => Kind switch
    {
        DeviceKind.Power => DeviceGroupKind.Power,
        DeviceKind.Temperature or DeviceKind.Humidity or DeviceKind.Environment => DeviceGroupKind.Environment,
        _ => DeviceGroupKind.Switch
    };
    public string CurrentValue => Kind switch
    {
        DeviceKind.Power => $"{PowerWatts ?? 0:0} W",
        DeviceKind.Temperature => $"{TemperatureC ?? 0:0.0} ℃",
        DeviceKind.Humidity => $"{HumidityPercent ?? 0:0} %",
        DeviceKind.Environment => $"{TemperatureC ?? 0:0.0} ℃   {HumidityPercent ?? 0:0} %",
        DeviceKind.Switch => IsOn == true ? "ON" : "OFF",
        _ => "--"
    };
}

public static class ValueColorRules
{
    public const string Neutral = "neutral";
    public static string Temperature(double? value)
    {
        if(value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return Neutral;
        var v=value.Value;
        if(v<=0) return "white";
        if(v>=35) return "deepmagenta";
        if(v>=30) return "red";
        if(v>=25) return "yellow";
        if(v>=20) return "green";
        if(v>=15) return "lightblue";
        return "darkblue";
    }
    public static string Humidity(double? value)
    {
        if(value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return Neutral;
        var v=value.Value;
        if(v>=80) return "red";
        if(v>=60) return "yellow";
        if(v>=40) return "green";
        if(v>=20) return "lightblue";
        return "darkblue";
    }
    public static string Power(double? value)
    {
        if(value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return Neutral;
        var v=value.Value;
        if(v>=1000) return "purple";
        if(v>=800) return "red";
        if(v>=400) return "yellow";
        if(v>=100) return "lime";
        return "green";
    }
}

public sealed class HistoryPoint { public DateTime Time { get; set; } public double Value { get; set; } }

public sealed class PowerEnergyStatistics
{
    public double TodayWh { get; set; }
    public double TodayCostYen { get; set; }
    public double MonthWh { get; set; }
    public double MonthCostYen { get; set; }
    // Kept for source/backward compatibility. UI no longer uses this value.
    public double MonthAverageWh { get; set; }
}
public sealed class GraphStatistics
{
    public double Current { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double Average { get; set; }
}
public sealed class PanelGeometry
{
    public string DeviceId { get; set; } = ""; public double X { get; set; } = 20; public double Y { get; set; } = 20;
    public double Width { get; set; } = 250; public double Height { get; set; } = 130; public string Background { get; set; } = "#242932";
    public string Foreground { get; set; } = "#F2F5F7"; public string TitleForeground { get; set; } = "#B8C1CC"; public string FontFamily { get; set; } = "Yu Gothic UI"; public double FontSize { get; set; } = 32; public double SecondaryFontSize { get; set; } = 25; public double TitleFontSize { get; set; } = 13; public bool Visible { get; set; } = true;
    public bool Collapsed { get; set; } = false;
    public double ExpandedHeight { get; set; } = 180;
}
public sealed class GroupGeometry
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public DeviceGroupKind Kind { get; set; }
    public string Title { get; set; } = "";
    public double X { get; set; } = 18;
    public double Y { get; set; } = 18;
    public double Width { get; set; } = 900;
    public double Height { get; set; } = 180;
    public bool Visible { get; set; } = true;
    public bool Collapsed { get; set; } = false;
    public double ExpandedHeight { get; set; } = 180;
}
public sealed class TabLayout
{
    public string Name { get; set; } = "メイン";
    public List<PanelGeometry> Panels { get; set; } = [];
    public List<GroupGeometry> Groups { get; set; } = [];
}
public sealed class AppSettings
{
    public double Left { get; set; } = 100; public double Top { get; set; } = 100; public double Width { get; set; } = 1100; public double Height { get; set; } = 720;
    public string WindowState { get; set; } = "Normal"; public int ValuePollSeconds { get; set; } = 60; public int MetadataPollMinutes { get; set; } = 15;
    public int HttpPort { get; set; } = 8080; public string HttpBind { get; set; } = "0.0.0.0"; public bool HttpEnabled { get; set; } = true;
    public string TunnelCommand { get; set; } = ""; public string PythonPath { get; set; } = "python"; public List<string> HubIps { get; set; } = []; public List<TabLayout> Tabs { get; set; } = [new()]; public List<DeviceSnapshot> Devices { get; set; } = [];
    public string ElectricityRegion { get; set; } = "関東";
    public string ElectricityCompany { get; set; } = "東京電力";
    public string ContractCapacity { get; set; } = "30A";
    public double ElectricityRateYenPerKwh { get; set; } = 30.0;
    public bool LoggingEnabled { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
    public bool VerboseFunctionEntryLogging { get; set; } = false;
    public int StaleDeviceMinutes { get; set; } = 5;
    public List<string> WebViewGraphDeviceIds { get; set; } = [];
    [JsonIgnore]
    [Obsolete("Web管理認証は廃止されました。旧設定とのソース互換性のためだけに残しています。")]
    public string WebAdminUser { get; set; } = "admin";
}
