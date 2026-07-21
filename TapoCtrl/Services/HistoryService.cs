using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TapoCtrl.Models;
namespace TapoCtrl.Services;

public sealed class HistoryService
{
    private readonly string _dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TapoCtrl","history");
    private readonly ConcurrentDictionary<string,(DateTime Expires,IReadOnlyList<DateOnly> Dates)> _dateCache=new(StringComparer.OrdinalIgnoreCase);

    public async Task AppendAsync(DeviceSnapshot d)
    {
        if(!d.Online)return;
        Directory.CreateDirectory(_dir);
        async Task appendValue(string key,double? value)
        {
            if(value is null || !double.IsFinite(value.Value))return;
            var stamp=d.Timestamp==default?DateTime.Now:d.Timestamp.ToLocalTime();
            var path=Path.Combine(_dir,$"{Sanitize(key)}_{stamp:yyyyMMdd}.jsonl");
            await File.AppendAllTextAsync(path,JsonSerializer.Serialize(new HistoryPoint{Time=stamp,Value=value.Value})+Environment.NewLine);
            _dateCache.TryRemove(key,out _);
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
            case DeviceKind.Temperature: await appendValue(MetricKey(d.Id,"temperature"),d.TemperatureC); break;
            case DeviceKind.Humidity: await appendValue(MetricKey(d.Id,"humidity"),d.HumidityPercent); break;
        }
    }

    public Task<List<HistoryPoint>> Read24hAsync(string key)=>ReadKeyRangeAsync(key,DateTime.Now.AddHours(-24),DateTime.Now.AddMinutes(1),2);

    public async Task<List<HistoryPoint>> ReadMetric24hAsync(string id,string metric,double? currentValue=null)
    {
        var from=DateTime.Now.AddHours(-24); var to=DateTime.Now.AddMinutes(1);
        return await ReadMetricRangeAsync(id,metric,from,to,currentValue);
    }

    public async Task<List<HistoryPoint>> ReadMetricForDateAsync(string id,string metric,DateOnly date,double? currentValue=null)
    {
        var from=date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Local);
        return await ReadMetricRangeAsync(id,metric,from,from.AddDays(1),currentValue);
    }

    private async Task<List<HistoryPoint>> ReadMetricRangeAsync(string id,string metric,DateTime from,DateTime to,double? currentValue)
    {
        var points=await ReadKeyRangeAsync(MetricKey(id,metric),from,to,null);
        var legacyKey=metric.Equals("humidity",StringComparison.OrdinalIgnoreCase)?id+":humidity":id;
        var legacy=await ReadKeyRangeAsync(legacyKey,from,to,null);
        var compatible=metric.ToLowerInvariant() switch
        {
            "power" => legacy.Where(p=>p.Value is >=0 and <=3000).ToList(),
            "humidity" => FilterLegacyEnvironment(legacy,currentValue,true),
            "temperature" => FilterLegacyEnvironment(legacy,currentValue,false),
            _ => legacy
        };
        return compatible.Concat(points).Where(p=>p.Time>=from&&p.Time<to)
            .GroupBy(p=>p.Time).Select(g=>g.Last()).OrderBy(p=>p.Time).ToList();
    }

    public async Task<List<HistoryPoint>> ReadAggregate24hAsync(IEnumerable<string> ids)
        => await ReadAggregateRangeAsync(ids,DateTime.Now.AddHours(-24),DateTime.Now.AddMinutes(1));

    public async Task<List<HistoryPoint>> ReadAggregateForDateAsync(IEnumerable<string> ids,DateOnly date)
    {
        var from=date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Local);
        return await ReadAggregateRangeAsync(ids,from,from.AddDays(1));
    }

    private async Task<List<HistoryPoint>> ReadAggregateRangeAsync(IEnumerable<string> ids,DateTime from,DateTime to)
    {
        var perDevice=await ReadPowerSeriesForRangeAsync(ids,from,to);
        return AggregatePowerSeries(perDevice);
    }

    public static List<HistoryPoint> AggregatePowerSeries(IReadOnlyDictionary<string,List<HistoryPoint>> perDevice)
    {
        var buckets=new SortedDictionary<DateTime,List<double>>();
        foreach(var points in perDevice.Values)
        foreach(var p in points)
        {
            var key=new DateTime(p.Time.Year,p.Time.Month,p.Time.Day,p.Time.Hour,p.Time.Minute,0,DateTimeKind.Local);
            if(!buckets.TryGetValue(key,out var values))buckets[key]=values=[];
            values.Add(p.Value);
        }
        return buckets.Select(x=>new HistoryPoint{Time=x.Key,Value=x.Value.Sum()}).ToList();
    }

    public async Task<Dictionary<string,List<HistoryPoint>>> ReadPowerSeriesForDateAsync(IEnumerable<string> ids,DateOnly date)
    {
        var from=date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Local);
        return await ReadPowerSeriesForRangeAsync(ids,from,from.AddDays(1));
    }

    private async Task<Dictionary<string,List<HistoryPoint>>> ReadPowerSeriesForRangeAsync(IEnumerable<string> ids,DateTime from,DateTime to)
    {
        var result=new Dictionary<string,List<HistoryPoint>>(StringComparer.OrdinalIgnoreCase);
        foreach(var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            result[id]=await ReadMetricRangeAsync(id,"power",from,to,null);
        return result;
    }

    public Task<IReadOnlyList<DateOnly>> GetAvailableDatesAsync(string id,string metric)
        => GetAvailableDatesForKeysAsync($"{id}|{metric}",[MetricKey(id,metric),metric.Equals("humidity",StringComparison.OrdinalIgnoreCase)?id+":humidity":id]);

    public Task<IReadOnlyList<DateOnly>> GetAvailableAggregateDatesAsync(IEnumerable<string> ids)
        => GetAvailableDatesForKeysAsync("aggregate:"+string.Join('|',ids.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)),ids.Select(x=>MetricKey(x,"power")).Concat(ids));

    private Task<IReadOnlyList<DateOnly>> GetAvailableDatesForKeysAsync(string cacheKey,IEnumerable<string> keys)
    {
        if(_dateCache.TryGetValue(cacheKey,out var cached)&&cached.Expires>DateTime.UtcNow)return Task.FromResult(cached.Dates);
        var set=new SortedSet<DateOnly>();
        if(Directory.Exists(_dir))
        foreach(var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach(var file in Directory.EnumerateFiles(_dir,$"{Sanitize(key)}_????????.jsonl"))
        {
            var name=Path.GetFileNameWithoutExtension(file); var suffix=name.Length>=8?name[^8..]:"";
            if(DateOnly.TryParseExact(suffix,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))set.Add(date);
        }
        IReadOnlyList<DateOnly> dates=set.Reverse().ToList();
        _dateCache[cacheKey]=(DateTime.UtcNow.AddSeconds(30),dates);
        return Task.FromResult(dates);
    }

    private async Task<List<HistoryPoint>> ReadKeyRangeAsync(string key,DateTime from,DateTime to,int? maxNewestFiles)
    {
        var result=new List<HistoryPoint>(); if(!Directory.Exists(_dir))return result;
        IEnumerable<string> files=Directory.EnumerateFiles(_dir,$"{Sanitize(key)}_*.jsonl").OrderByDescending(x=>x);
        if(maxNewestFiles is not null)files=files.Take(maxNewestFiles.Value);
        foreach(var f in files)
        {
            var dateText=Path.GetFileNameWithoutExtension(f); dateText=dateText.Length>=8?dateText[^8..]:"";
            if(DateOnly.TryParseExact(dateText,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var fd))
            {
                var day=fd.ToDateTime(TimeOnly.MinValue,DateTimeKind.Local);
                if(day>=to || day.AddDays(1)<=from)continue;
            }
            try
            {
                using var fs=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(fs);
                while(await reader.ReadLineAsync() is { } line)
                {
                    try{var p=JsonSerializer.Deserialize<HistoryPoint>(line);if(p is not null&&p.Time>=from&&p.Time<to&&double.IsFinite(p.Value))result.Add(p);}catch(JsonException){ }
                }
            }
            catch(IOException){ }
            catch(UnauthorizedAccessException){ }
        }
        return result.OrderBy(x=>x.Time).ToList();
    }

    private static List<HistoryPoint> FilterLegacyEnvironment(List<HistoryPoint> legacy,double? current,bool humidity)
    {
        IEnumerable<HistoryPoint> candidates=humidity?legacy.Where(p=>p.Value is >=0 and <=100):legacy.Where(p=>p.Value is >=-30 and <=65);
        var list=candidates.ToList(); if(current is null||list.Count==0)return list;
        var tolerance=humidity?25.0:18.0; var near=list.Where(p=>Math.Abs(p.Value-current.Value)<=tolerance).ToList(); return near.Count>0?near:[];
    }
    public static string MetricKey(string id,string metric)=>$"{id}:metric:{metric}";
    private static string Sanitize(string s)=>string.Concat(s.Select(c=>char.IsLetterOrDigit(c)?c:'_'));
}
