using System.IO;
using System.Text.Json;
using TapoCtrl.Models;
namespace TapoCtrl.Services;
public sealed class HistoryService
{
    private readonly string _dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TapoCtrl","history");

    public async Task AppendAsync(DeviceSnapshot d)
    {
        if(!d.Online)return;
        Directory.CreateDirectory(_dir);
        async Task appendValue(string key,double? value)
        {
            if(value is null || !double.IsFinite(value.Value))return;
            var path=Path.Combine(_dir,$"{Sanitize(key)}_{DateTime.Now:yyyyMMdd}.jsonl");
            await File.AppendAllTextAsync(path,JsonSerializer.Serialize(new HistoryPoint{Time=d.Timestamp,Value=value.Value})+Environment.NewLine);
        }

        switch(d.Kind)
        {
            case DeviceKind.Power:
                if(d.PowerWatts is >=0 and <=3000) await appendValue(MetricKey(d.Id,"power"),d.PowerWatts);
                break;
            case DeviceKind.Environment:
                await appendValue(MetricKey(d.Id,"temperature"),d.TemperatureC);
                await appendValue(MetricKey(d.Id,"humidity"),d.HumidityPercent);
                break;
            case DeviceKind.Temperature:
                await appendValue(MetricKey(d.Id,"temperature"),d.TemperatureC);
                break;
            case DeviceKind.Humidity:
                await appendValue(MetricKey(d.Id,"humidity"),d.HumidityPercent);
                break;
        }
    }

    public Task<List<HistoryPoint>> Read24hAsync(string key)=>ReadKey24hAsync(key);

    public async Task<List<HistoryPoint>> ReadMetric24hAsync(string id,string metric,double? currentValue=null)
    {
        var points=await ReadKey24hAsync(MetricKey(id,metric));

        // v0.0.75以前との互換読込。湿度は旧 :humidity、電力・温度は旧 deviceId 本体を参照する。
        // 温湿度の混在履歴は現在値と妥当範囲で選別し、70℃等の誤表示を防ぐ。
        var legacyKey=metric.Equals("humidity",StringComparison.OrdinalIgnoreCase)?id+":humidity":id;
        var legacy=await ReadKey24hAsync(legacyKey);
        var compatible=metric.ToLowerInvariant() switch
        {
            "power" => legacy.Where(p=>p.Value is >=0 and <=3000).ToList(),
            "humidity" => FilterLegacyEnvironment(legacy,currentValue,true),
            "temperature" => FilterLegacyEnvironment(legacy,currentValue,false),
            _ => legacy
        };
        return compatible.Concat(points)
            .GroupBy(p=>p.Time)
            .Select(g=>g.Last())
            .OrderBy(p=>p.Time)
            .ToList();
    }

    public async Task<List<HistoryPoint>> ReadAggregate24hAsync(IEnumerable<string> ids)
    {
        var all=new List<HistoryPoint>();
        foreach(var id in ids)all.AddRange(await ReadMetric24hAsync(id,"power"));
        return all.GroupBy(p=>new DateTime(p.Time.Year,p.Time.Month,p.Time.Day,p.Time.Hour,p.Time.Minute,0))
            .Select(g=>new HistoryPoint{Time=g.Key,Value=g.Sum(x=>x.Value)}).OrderBy(x=>x.Time).ToList();
    }

    private async Task<List<HistoryPoint>> ReadKey24hAsync(string key)
    {
        var result=new List<HistoryPoint>();
        if(!Directory.Exists(_dir))return result;
        foreach(var f in Directory.GetFiles(_dir,$"{Sanitize(key)}_*.jsonl").OrderByDescending(x=>x).Take(2))
        {
            foreach(var line in await File.ReadAllLinesAsync(f))
            {
                try
                {
                    var p=JsonSerializer.Deserialize<HistoryPoint>(line);
                    if(p?.Time>=DateTime.Now.AddHours(-24) && double.IsFinite(p.Value))result.Add(p);
                }
                catch{}
            }
        }
        return result.OrderBy(x=>x.Time).ToList();
    }

    private static List<HistoryPoint> FilterLegacyEnvironment(List<HistoryPoint> legacy,double? current,bool humidity)
    {
        IEnumerable<HistoryPoint> candidates=humidity
            ? legacy.Where(p=>p.Value is >=0 and <=100)
            : legacy.Where(p=>p.Value is >=-30 and <=65);
        var list=candidates.ToList();
        if(current is null || list.Count==0)return list;

        // 現在値から大きく離れた別系列を除外。旧ファイルが湿度だけの場合に70℃等を出さないための互換処理。
        var tolerance=humidity?25.0:18.0;
        var near=list.Where(p=>Math.Abs(p.Value-current.Value)<=tolerance).ToList();
        return near.Count>0?near:[];
    }

    private static string MetricKey(string id,string metric)=>$"{id}:metric:{metric}";
    private static string Sanitize(string s)=>string.Concat(s.Select(c=>char.IsLetterOrDigit(c)?c:'_'));
}
