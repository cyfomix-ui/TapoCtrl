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
        async Task appendValue(string id,double? value)
        {
            if(value is null)return;
            var path=Path.Combine(_dir,$"{Sanitize(id)}_{DateTime.Now:yyyyMMdd}.jsonl");
            await File.AppendAllTextAsync(path,JsonSerializer.Serialize(new HistoryPoint{Time=d.Timestamp,Value=value.Value})+Environment.NewLine);
        }
        if(d.Kind==DeviceKind.Environment)
        {
            await appendValue(d.Id,d.TemperatureC);
            await appendValue(d.Id+":humidity",d.HumidityPercent);
            return;
        }
        double? v=d.Kind switch { DeviceKind.Power=>d.PowerWatts is >=0 and <=3000 ? d.PowerWatts : null,DeviceKind.Temperature=>d.TemperatureC,DeviceKind.Humidity=>d.HumidityPercent,_=>null};
        await appendValue(d.Id,v);
    }
    public async Task<List<HistoryPoint>> Read24hAsync(string id)
    {
        var result=new List<HistoryPoint>(); if(!Directory.Exists(_dir))return result;
        foreach(var f in Directory.GetFiles(_dir,$"{Sanitize(id)}_*.jsonl").OrderByDescending(x=>x).Take(2)) foreach(var line in await File.ReadAllLinesAsync(f)) try { var p=JsonSerializer.Deserialize<HistoryPoint>(line); if(p?.Time>=DateTime.Now.AddHours(-24))result.Add(p); } catch{}
        return result.OrderBy(x=>x.Time).ToList();
    }
    public async Task<List<HistoryPoint>> ReadAggregate24hAsync(IEnumerable<string> ids)
    {
        var all=new List<HistoryPoint>();
        foreach(var id in ids)all.AddRange(await Read24hAsync(id));
        return all.GroupBy(p=>new DateTime(p.Time.Year,p.Time.Month,p.Time.Day,p.Time.Hour,p.Time.Minute,0))
            .Select(g=>new HistoryPoint{Time=g.Key,Value=g.Sum(x=>x.Value)}).OrderBy(x=>x.Time).ToList();
    }
    private static string Sanitize(string s)=>string.Concat(s.Select(c=>char.IsLetterOrDigit(c)?c:'_'));
}
