using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TapoCtrl.Models;
namespace TapoCtrl.Services;

public sealed class LocalHttpService : IDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cts;
    private readonly Func<IReadOnlyList<DeviceSnapshot>> _get;
    private readonly Func<string,bool,Task<bool>> _power;
    private readonly Action<string>? _openGraph;
    private readonly Func<double> _rate;
    private readonly Func<string,Task<List<HistoryPoint>>>? _history;
    private readonly HistoryService _historyService;
    private readonly Func<IReadOnlyList<string>> _viewGraphIds;
    private readonly Func<int> _staleDeviceMinutes;
    private readonly string _controlToken=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public LocalHttpService(Func<IReadOnlyList<DeviceSnapshot>> get,Func<string,bool,Task<bool>> power,Action<string>? openGraph=null,Func<double>? rate=null,Func<string,Task<List<HistoryPoint>>>? history=null,HistoryService? historyService=null,Func<IReadOnlyList<string>>? viewGraphIds=null,Func<int>? staleDeviceMinutes=null)
    {
        _get=get;_power=power;_openGraph=openGraph;_rate=rate??(()=>30.0);_history=history;_historyService=historyService??new HistoryService();_viewGraphIds=viewGraphIds??(()=>Array.Empty<string>());_staleDeviceMinutes=staleDeviceMinutes??(()=>5);
    }
    public List<string> ActiveUrls { get; } = [];
    public string LastWarning { get; private set; } = string.Empty;

    public void Start(string bind,int port)
    {
        bind = NormalizeBind(bind);
        ActiveUrls.Clear();
        LastWarning = string.Empty;
        _cts = new CancellationTokenSource();

        // HttpListener/HTTP.sys は Host 名検査や URLACL で Bad Request - Invalid Hostname を返すことがあるため使わない。
        // TcpListener で直接 HTTP を処理し、localhost / LAN IP の Host ヘッダー差を無視する。
        var endpoints = BuildEndpoints(bind, port).DistinctBy(x => x.Address + ":" + x.Port).ToList();
        var failures = new List<string>();
        foreach(var ep in endpoints)
        {
            try
            {
                var listener = new TcpListener(ep);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start(64);
                _listeners.Add(listener);
                _ = AcceptLoop(listener, _cts.Token);
            }
            catch(Exception ex)
            {
                failures.Add($"{ep.Address}:{ep.Port} : {ex.Message}");
            }
        }

        if(_listeners.Count == 0)
            throw new InvalidOperationException("Webサーバーを開始できませんでした。Portが他プロセスで使用中か、権限で拒否されています。" + (failures.Count>0 ? " " + string.Join(" / ", failures) : ""));

        ActiveUrls.Add($"http://localhost:{port}/Ctrl/"); ActiveUrls.Add($"http://localhost:{port}/View/");
        ActiveUrls.Add($"http://127.0.0.1:{port}/Ctrl/"); ActiveUrls.Add($"http://127.0.0.1:{port}/View/");
        foreach(var ip in GetLocalIPv4()){ActiveUrls.Add($"http://{ip}:{port}/Ctrl/");ActiveUrls.Add($"http://{ip}:{port}/View/");}

        if(failures.Count > 0)
            LastWarning = "一部の待ち受けアドレスを開始できませんでした。LAN側から見えない場合はWindows Defender FirewallでPortの受信許可を確認してください。";
    }

    private static IEnumerable<IPEndPoint> BuildEndpoints(string bind, int port)
    {
        if(bind == "0.0.0.0")
        {
            yield return new IPEndPoint(IPAddress.Any, port);
            yield break;
        }
        if(bind == "127.0.0.1")
        {
            yield return new IPEndPoint(IPAddress.Loopback, port);
            yield break;
        }
        if(IPAddress.TryParse(bind, out var ip)) yield return new IPEndPoint(ip, port);
        else yield return new IPEndPoint(IPAddress.Loopback, port);
    }

    public static IEnumerable<string> GetLocalIPv4()
    {
        foreach(var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up))
        {
            foreach(var a in ni.GetIPProperties().UnicastAddresses.Where(x=>x.Address.AddressFamily==AddressFamily.InterNetwork))
            {
                var ip=a.Address.ToString();
                if(ip.StartsWith("169.254.")) continue;
                if(ip == "127.0.0.1") continue;
                yield return ip;
            }
        }
    }

    public static string NormalizeBind(string? bind)
    {
        bind = (bind ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(bind) || bind == "*" || bind == "+") return "0.0.0.0";
        if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return "127.0.0.1";
        return bind;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => HandleClient(client, ct), ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using var _client = client;
        try
        {
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen:true);
            var requestLine = await reader.ReadLineAsync(ct) ?? string.Empty;
            if(string.IsNullOrWhiteSpace(requestLine)) return;

            var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while(!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
            {
                var colon=line.IndexOf(':');
                if(colon>0)headers[line[..colon].Trim()]=line[(colon+1)..].Trim();
            }

            string[] parts = requestLine.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length < 2)
            {
                await Send(stream, "Method Not Allowed", "text/plain; charset=utf-8", 405, ct);
                return;
            }

            var preliminaryUrl = parts[1];
            var preliminaryQueryIndex = preliminaryUrl.IndexOf('?');
            var preliminaryPath = preliminaryQueryIndex >= 0 ? preliminaryUrl[..preliminaryQueryIndex] : preliminaryUrl;
            if(!(parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("POST", StringComparison.OrdinalIgnoreCase)))
            {
                if(preliminaryPath.Equals("/api/power", StringComparison.OrdinalIgnoreCase))
                {
                    if(!IsLoopbackClient(client)) await SendJson(stream,new { ok=false,error="local_access_only" },403,ct);
                    else await SendJson(stream,new { ok=false,error="method_not_allowed" },405,ct);
                }
                else
                    await Send(stream, "Method Not Allowed", "text/plain; charset=utf-8", 405, ct);
                return;
            }

            var url = parts[1];
            var qIndex = url.IndexOf('?');
            var path = qIndex >= 0 ? url[..qIndex] : url;
            var query = qIndex >= 0 ? ParseQuery(url[(qIndex+1)..]) : new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

            var method=parts[0].ToUpperInvariant();
            if(path.Equals("/Ctrl",StringComparison.OrdinalIgnoreCase)){await Redirect(stream,"/Ctrl/",ct);return;}
            if(path.Equals("/View",StringComparison.OrdinalIgnoreCase)){await Redirect(stream,"/View/",ct);return;}
            if(path=="/") await Send(stream,"NoService","text/plain; charset=utf-8",404,ct);
            else if(path.Equals("/Ctrl/",StringComparison.OrdinalIgnoreCase)) await Send(stream,await BuildHtmlAsync(_get(),true),"text/html; charset=utf-8",200,ct);
            else if(path.Equals("/View/",StringComparison.OrdinalIgnoreCase)) await Send(stream,await BuildHtmlAsync(_get(),false),"text/html; charset=utf-8",200,ct);
            else if(path.Equals("/Ctrl/api/devices",StringComparison.OrdinalIgnoreCase)) await Send(stream,JsonSerializer.Serialize(GetDashboardDevices(_get(),true)),"application/json; charset=utf-8",200,ct);
            else if(path.Equals("/View/api/devices",StringComparison.OrdinalIgnoreCase)||path.Equals("/View/api/dashboard",StringComparison.OrdinalIgnoreCase)) await Send(stream,JsonSerializer.Serialize(GetDashboardDevices(_get(),false)),"application/json; charset=utf-8",200,ct);
            else if(path.Equals("/Ctrl/api/history",StringComparison.OrdinalIgnoreCase)||path.Equals("/Ctrl/api/dashboard-history",StringComparison.OrdinalIgnoreCase)||path.Equals("/View/api/history",StringComparison.OrdinalIgnoreCase)||path.Equals("/View/api/dashboard-history",StringComparison.OrdinalIgnoreCase))
            {
                query.TryGetValue("id",out var historyId); historyId=DecodeQueryValue(historyId);
                var viewRequest=path.StartsWith("/View/",StringComparison.OrdinalIgnoreCase);
                var allowed=GetDashboardDevices(_get(),!viewRequest).FirstOrDefault(d=>d.Id.Equals(historyId,StringComparison.OrdinalIgnoreCase));
                if(allowed is null||allowed.IsPowerSummary||!IsDashboardSelectable(allowed)){await Send(stream,"Not Found","text/plain; charset=utf-8",404,ct);return;}
                await Send(stream,await BuildDashboardHistoryJsonAsync(allowed),"application/json; charset=utf-8",200,ct);
            }
            else if(path.Equals("/Ctrl/api/toggle",StringComparison.OrdinalIgnoreCase))
            {
                if(method!="POST"){await Send(stream,"Method Not Allowed","text/plain; charset=utf-8",405,ct);return;}
                if(!headers.TryGetValue("X-TapoCtrl-Request",out var marker)||marker!="1"||!headers.TryGetValue("X-TapoCtrl-Control",out var token)||!FixedEquals(token,_controlToken)){await Send(stream,"Forbidden","text/plain; charset=utf-8",403,ct);return;}
                query.TryGetValue("id",out var id);var target=_get().FirstOrDefault(d=>d.Id.Equals(id??"",StringComparison.OrdinalIgnoreCase));
                var ok=target is not null&&target.Kind==DeviceKind.Switch&&await _power(target.Id,target.IsOn!=true);
                await Send(stream,$"{{\"ok\":{ok.ToString().ToLowerInvariant()}}}","application/json; charset=utf-8",ok?200:404,ct);
            }
            else if(path.Equals("/Ctrl/api/power",StringComparison.OrdinalIgnoreCase))
            {
                if(method!="POST"){await Send(stream,"Method Not Allowed","text/plain; charset=utf-8",405,ct);return;}
                if(!headers.TryGetValue("X-TapoCtrl-Request",out var marker)||marker!="1"||!headers.TryGetValue("X-TapoCtrl-Control",out var token)||!FixedEquals(token,_controlToken)){await Send(stream,"Forbidden","text/plain; charset=utf-8",403,ct);return;}
                query.TryGetValue("id",out var id);query.TryGetValue("state",out var stateText);id=DecodeQueryValue(id);var isOn=(stateText??"").Equals("on",StringComparison.OrdinalIgnoreCase);var isOff=(stateText??"").Equals("off",StringComparison.OrdinalIgnoreCase);
                if(string.IsNullOrWhiteSpace(id)||(!isOn&&!isOff)){await Send(stream,"{\"ok\":false}","application/json; charset=utf-8",400,ct);return;}
                var ok=await _power(id,isOn);await Send(stream,$"{{\"ok\":{ok.ToString().ToLowerInvariant()}}}","application/json; charset=utf-8",ok?200:404,ct);
            }
            // Legacy local integration endpoint. GET is accepted only for compatibility; POST is recommended.
            else if(path.Equals("/api/power",StringComparison.OrdinalIgnoreCase))
            {
                if(!IsLoopbackClient(client))
                {
                    await SendJson(stream,new { ok=false,error="local_access_only" },403,ct);
                    return;
                }
                if(method is not ("GET" or "POST"))
                {
                    await SendJson(stream,new { ok=false,error="method_not_allowed" },405,ct);
                    return;
                }

                query.TryGetValue("id",out var idText);
                query.TryGetValue("ip",out var ipText);
                query.TryGetValue("state",out var stateText);
                var id=DecodeQueryValue(idText);
                var ip=DecodeQueryValue(ipText);
                var hasId=!string.IsNullOrWhiteSpace(id);
                var hasIp=!string.IsNullOrWhiteSpace(ip);
                if(!hasId&&!hasIp)
                {
                    await SendJson(stream,new { ok=false,error="target_required" },400,ct);
                    return;
                }
                if(hasId&&hasIp)
                {
                    await SendJson(stream,new { ok=false,error="ambiguous_target" },400,ct);
                    return;
                }

                var isOn=(stateText??string.Empty).Equals("on",StringComparison.OrdinalIgnoreCase);
                var isOff=(stateText??string.Empty).Equals("off",StringComparison.OrdinalIgnoreCase);
                if(!isOn&&!isOff)
                {
                    await SendJson(stream,new { ok=false,error="invalid_state" },400,ct);
                    return;
                }

                DeviceSnapshot? target=null;
                var devices=_get().Where(d=>!d.IsPowerSummary&&(d.Kind==DeviceKind.Power||d.Kind==DeviceKind.Switch)).ToList();
                if(hasId)
                {
                    target=devices.FirstOrDefault(d=>d.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
                    target??=devices.FirstOrDefault(d=>d.Name.Equals(id,StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    if(!IPAddress.TryParse(ip,out var parsedIp)||parsedIp.AddressFamily!=AddressFamily.InterNetwork)
                    {
                        await SendJson(stream,new { ok=false,error="device_not_found" },404,ct);
                        return;
                    }
                    target=devices.FirstOrDefault(d=>d.Ip.Equals(ip,StringComparison.OrdinalIgnoreCase));
                }

                if(target is null)
                {
                    await SendJson(stream,new { ok=false,error="device_not_found" },404,ct);
                    return;
                }

                bool ok;
                try { ok=await _power(target.Id,isOn); }
                catch { ok=false; }
                if(!ok)
                {
                    await SendJson(stream,new { ok=false,error="power_command_failed" },500,ct);
                    return;
                }
                await SendJson(stream,new { ok=true,id=target.Id,state=isOn?"on":"off" },200,ct);
            }
            else if(path.StartsWith("/api/",StringComparison.OrdinalIgnoreCase)||path.Equals("/json",StringComparison.OrdinalIgnoreCase)) await Send(stream,"Forbidden","text/plain; charset=utf-8",403,ct);
            else if(path.Equals("/graph",StringComparison.OrdinalIgnoreCase)||path.Equals("/View/graph",StringComparison.OrdinalIgnoreCase)||path.Equals("/Ctrl/graph",StringComparison.OrdinalIgnoreCase))
            {
                query.TryGetValue("id",out var id);query.TryGetValue("date",out var dateText);if(!TryParseLocalDate(dateText,out var date)){await Send(stream,"Invalid date","text/plain; charset=utf-8",400,ct);return;}
                var decodedId=DecodeQueryValue(id);var graphDevice=_get().FirstOrDefault(d=>d.Id.Equals(decodedId,StringComparison.OrdinalIgnoreCase)||d.Name.Equals(decodedId,StringComparison.OrdinalIgnoreCase));if(graphDevice is null||(path.StartsWith("/View/",StringComparison.OrdinalIgnoreCase)&&!IsViewVisible(graphDevice))){await Send(stream,"Device not found","text/plain; charset=utf-8",404,ct);return;}
                await Send(stream,await BuildGraphHtml(decodedId,date),"text/html; charset=utf-8",200,ct);
            }
            else if(path.Equals("/series",StringComparison.OrdinalIgnoreCase)||path.Equals("/View/series",StringComparison.OrdinalIgnoreCase)||path.Equals("/Ctrl/series",StringComparison.OrdinalIgnoreCase))
            {
                query.TryGetValue("group",out var group);query.TryGetValue("date",out var dateText);if(!TryParseLocalDate(dateText,out var date)){await Send(stream,"Invalid date","text/plain; charset=utf-8",400,ct);return;}
                var normalized=(group??"").ToLowerInvariant();if(normalized is not ("power" or "env" or "environment")){await Send(stream,"Invalid group","text/plain; charset=utf-8",400,ct);return;}
                await Send(stream,await BuildSeriesHtml(normalized,date),"text/html; charset=utf-8",200,ct);
            }
            else if(path.Equals("/health",StringComparison.OrdinalIgnoreCase)) await Send(stream,"ok","text/plain; charset=utf-8",200,ct);
            else await Send(stream,"NoService","text/plain; charset=utf-8",404,ct);
        }
        catch { }
    }

    private static bool FixedEquals(string left,string right){var a=Encoding.UTF8.GetBytes(left);var b=Encoding.UTF8.GetBytes(right);var diff=a.Length^b.Length;for(var i=0;i<Math.Max(a.Length,b.Length);i++)diff|=(i<a.Length?a[i]:0)^(i<b.Length?b[i]:0);return diff==0;}

    private static bool IsLoopbackClient(TcpClient client)
    {
        if(client.Client.RemoteEndPoint is not IPEndPoint remote) return false;
        var address=remote.Address;
        if(IPAddress.IsLoopback(address)) return true;
        return address.IsIPv4MappedToIPv6&&IPAddress.IsLoopback(address.MapToIPv4());
    }

    private static Task SendJson(NetworkStream stream,object payload,int status,CancellationToken ct)
        =>Send(stream,JsonSerializer.Serialize(payload),"application/json; charset=utf-8",status,ct);
    private static Task Redirect(NetworkStream stream,string location,CancellationToken ct)=>Send(stream,"","text/plain; charset=utf-8",302,ct,new Dictionary<string,string>{{"Location",location}});

    private static bool TryParseLocalDate(string? text,out DateOnly? date)
    {
        date=null;
        if(string.IsNullOrWhiteSpace(text))return true;
        if(!DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))return false;
        date=parsed;return true;
    }

    private static Dictionary<string,string> ParseQuery(string query)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var key = idx >= 0 ? part[..idx] : part;
            var val = idx >= 0 ? part[(idx+1)..] : string.Empty;
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(val);
        }
        return result;
    }

    private static string DecodeQueryValue(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decoded = WebUtility.UrlDecode(value).Trim();
        // 標準的な%エンコードに加え、ブラウザ等が送るUTF-8の生文字もそのまま扱う。
        return decoded;
    }

    private static async Task Send(NetworkStream stream,string body,string type,int status,CancellationToken ct,IReadOnlyDictionary<string,string>? extraHeaders=null)
    {
        var bodyBytes=Encoding.UTF8.GetBytes(body);
        var reason=status switch{200=>"OK",302=>"Found",400=>"Bad Request",403=>"Forbidden",404=>"Not Found",405=>"Method Not Allowed",500=>"Internal Server Error",_=>"OK"};
        var extra=extraHeaders is null?string.Empty:string.Concat(extraHeaders.Select(x=>$"{x.Key}: {x.Value}\r\n"));
        var header=$"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bodyBytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'\r\n{extra}Connection: close\r\n\r\n";
        var headerBytes=Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes,ct);
        await stream.WriteAsync(bodyBytes,ct);
        await stream.FlushAsync(ct);
    }

    private async Task<string> BuildSeriesHtml(string group,DateOnly? requestedDate)
    {
        var isEnv=group.Equals("env",StringComparison.OrdinalIgnoreCase)||group.Equals("environment",StringComparison.OrdinalIgnoreCase);
        var isPower=group.Equals("power",StringComparison.OrdinalIgnoreCase);
        if(!isEnv&&!isPower)return "<!doctype html><meta charset='utf-8'><body style='background:#0f1319;color:white;font-family:sans-serif'><h1>対象外です</h1></body>";
        var all=_get().Where(d=>d.Kind!=DeviceKind.Hub&&!d.IsPowerSummary).ToList();
        var date=requestedDate??DateOnly.FromDateTime(DateTime.Now);
        var today=date==DateOnly.FromDateTime(DateTime.Now);
        var devices=(isPower?all.Where(d=>d.Kind==DeviceKind.Power):all.Where(d=>d.Kind is DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity))
            .OrderBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).ToList();
        var ids=devices.Where(d=>d.Kind==DeviceKind.Power).Select(d=>d.Id).ToList();
        var available=isPower?await _historyService.GetAvailableAggregateDatesAsync(ids):
            (await Task.WhenAll(devices.Select(d=>_historyService.GetAvailableDatesAsync(d.Id,d.Kind==DeviceKind.Humidity?"humidity":"temperature")))).SelectMany(x=>x).Distinct().OrderByDescending(x=>x).ToList();
        async Task<object> row(DeviceSnapshot d,string metric)
        {
            var points=await _historyService.ReadMetricForDateAsync(d.Id,metric,date,metric=="power"?d.PowerWatts:metric=="humidity"?d.HumidityPercent:d.TemperatureC);
            return new{name=d.Name,id=d.Id,color=DeviceSeriesColor(d.Id,d.Name),metric,points=points.Select(p=>new{t=p.Time.ToString("O"),v=p.Value})};
        }
        var primary=new List<object>(); var secondary=new List<object>();
        if(isPower)
        {
            var byId=await _historyService.ReadPowerSeriesForDateAsync(ids,date);
            var aggregate=HistoryService.AggregatePowerSeries(byId);
            primary.Add(new{name="合計",id="__total__",color="#ffd61f",metric="power",isTotal=true,points=aggregate.Select(p=>new{t=p.Time.ToString("O"),v=p.Value})});
            foreach(var d in devices)primary.Add(new{name=d.Name,id=d.Id,color=DeviceSeriesColor(d.Id,d.Name),metric="power",isTotal=false,points=byId.GetValueOrDefault(d.Id,[]).Select(p=>new{t=p.Time.ToString("O"),v=p.Value})});
        }
        else
        {
            foreach(var d in devices)
            {
                if(d.Kind!=DeviceKind.Humidity)primary.Add(await row(d,"temperature"));
                if(d.Kind!=DeviceKind.Temperature)secondary.Add(await row(d,"humidity"));
            }
        }
        var powerStatsDevices=isPower?devices.Where(d=>d.Kind==DeviceKind.Power&&!d.IsPowerSummary).ToList():new List<DeviceSnapshot>();
        var seriesTodayWh=powerStatsDevices.Sum(d=>d.TodayWh??0);
        var seriesMonthWh=powerStatsDevices.Sum(d=>d.MonthWh??0);
        var seriesRate=_rate();
        var seriesEnergyHtml=isPower&&today?$"<div class='energy-stats'><div class='energy-stat'>本日合計<br><b>{seriesTodayWh/1000.0:0.000} kWh</b></div><div class='energy-stat'>本日概算<br><b>{seriesTodayWh/1000.0*seriesRate:N1} 円</b></div><div class='energy-stat'>月間計<br><b>{seriesMonthWh/1000.0:0.000} kWh</b></div><div class='energy-stat'>月間概算<br><b>{seriesMonthWh/1000.0*seriesRate:N1} 円</b></div></div>":isPower?"<div class='energy-note'>この日の電力量データは保存されていません</div>":"";
        var canonicalGroup=isPower?"power":"environment";
        var dateLinks=string.Join("",available.Select(d=>$"<a class=\"date-button{(d==date?" selected":"")}\" href=\"/series?group={WebUtility.UrlEncode(canonicalGroup)}&amp;date={d:yyyy-MM-dd}\" target=\"_blank\" rel=\"noopener noreferrer\" aria-label=\"{d:yyyy-MM-dd} の履歴を開く\">{d:yyyy-MM-dd}</a>"));
        string pjson=JsonSerializer.Serialize(primary),sjson=JsonSerializer.Serialize(secondary);
        var title=isPower?"電力系 - 系列グラフ":"観測系 - 系列グラフ";
        return $@"<!doctype html><html><head><meta charset='utf-8'><meta name=viewport content='width=device-width,initial-scale=1'><title>{WebUtility.HtmlEncode(title)} {date:yyyy-MM-dd}</title><style>
:root{{color-scheme:dark}}body{{margin:0;background:#0f1319;color:#f4f6fb;font-family:'Yu Gothic UI',Meiryo,Segoe UI,sans-serif;padding:22px}}.head{{display:flex;gap:12px;align-items:center;flex-wrap:wrap}}h1{{margin:0;font-size:30px}}button,.date-button{{background:#252d39;color:white;border:1px solid #798392;border-radius:9px;padding:8px 12px;text-decoration:none}}.date-button.selected{{outline:2px solid #b58cff}}.calendar{{display:none;position:absolute;background:#202733;border:1px solid #77808d;padding:10px;border-radius:10px;z-index:3;max-height:260px;overflow:auto}}.calendar.open{{display:grid;grid-template-columns:repeat(4,auto);gap:6px}}.panel{{background:#181d25;border:1px solid #626b78;border-radius:16px;padding:16px;margin-top:14px}}canvas{{width:100%;height:58vh;background:#15191f;border-radius:10px;display:block}}.legend{{display:flex;gap:12px;flex-wrap:wrap;max-height:110px;overflow:auto;margin-top:10px}}.legend span{{padding:3px 7px;border:1px solid #4d5664;border-radius:6px}}.energy-stats{{display:grid;grid-template-columns:repeat(4,minmax(150px,1fr));gap:10px;margin-top:12px}}.energy-stat{{background:#232a34;border-radius:10px;padding:10px;text-align:center}}.energy-stat b{{font-family:Impact,'Arial Narrow','Roboto Condensed','Yu Gothic UI',Meiryo,sans-serif;font-weight:normal;font-variant-numeric:tabular-nums;font-size:22px;color:#88f69d}}.energy-note,.note{{color:#c8ced8;margin-top:10px}}@media(max-width:720px){{.energy-stats{{grid-template-columns:repeat(2,minmax(130px,1fr))}}}}@media(max-width:420px){{.energy-stats{{grid-template-columns:1fr}}}}</style></head><body>
<div class='head'><h1>{WebUtility.HtmlEncode(title)} <small>{date:yyyy-MM-dd}</small></h1><button id='calendarButton' aria-label='履歴日付を選択' type='button'>📅</button><div id='cal' class='calendar'>{dateLinks}</div></div>
<div class='panel'><canvas id='chart' width='1400' height='700'></canvas><div id='legend' class='legend'></div>{seriesEnergyHtml}<div class='note'>欠測区間は線を接続しません。0Wの実測値とは区別されます。</div></div><script>
const series={pjson},second={sjson},today={(today?"true":"false")};
calendarButton.addEventListener('click',e=>{{e.stopPropagation();cal.classList.toggle('open')}});document.addEventListener('click',e=>{{if(!cal.contains(e.target)&&e.target!==calendarButton)cal.classList.remove('open')}});document.addEventListener('keydown',e=>{{if(e.key==='Escape')cal.classList.remove('open')}});
function finiteValues(groups){{return groups.flatMap(s=>s.points.map(p=>+p.v)).filter(Number.isFinite)}}
function niceNumber(value,round){{if(!Number.isFinite(value)||value<=0)return 1;const exponent=Math.floor(Math.log10(value)),fraction=value/Math.pow(10,exponent);let niceFraction;if(round)niceFraction=fraction<1.5?1:fraction<2.25?2:fraction<3.75?2.5:fraction<7.5?5:10;else niceFraction=fraction<=1?1:fraction<=2?2:fraction<=2.5?2.5:fraction<=5?5:10;return niceFraction*Math.pow(10,exponent)}}
function makeAxis(values,unit){{const a=values.filter(Number.isFinite);if(!a.length)return null;let dataMin=Math.min(...a),dataMax=Math.max(...a);const containsZero=a.some(v=>v===0),nonNegative=unit==='W'||unit==='%';if(nonNegative){{dataMin=Math.max(0,dataMin);dataMax=Math.max(0,dataMax)}}let span=dataMax-dataMin;if(span<=0)span=Math.max(Math.abs(dataMax)*.1,unit==='℃'?.5:unit==='%'?2:1);let paddedMin=dataMin-span*.08,paddedMax=dataMax+span*.08;if(nonNegative)paddedMin=Math.max(0,paddedMin);if(containsZero)paddedMin=0;if(unit==='%'){{paddedMin=Math.max(0,paddedMin);paddedMax=Math.min(100,Math.max(paddedMax,paddedMin+1))}}let step=niceNumber((paddedMax-paddedMin)/5,true),min=Math.floor(paddedMin/step)*step;if(nonNegative)min=Math.max(0,min);if(containsZero)min=0;let max=min+step*5;while(max<paddedMax-step*.001){{step=niceNumber(step*1.01,false);min=Math.floor(paddedMin/step)*step;if(nonNegative)min=Math.max(0,min);if(containsZero)min=0;max=min+step*5}}if(unit==='%'){{if(max>100){{max=100;min=Math.max(0,max-step*5)}}if(min<0)min=0}}if(max<=min)max=min+step*5;const ticks=Array.from({{length:6}},(_,i)=>{{const v=min+i*(max-min)/5;return Math.abs(v)<step*1e-9?0:v}});return{{min,max,step:(max-min)/5,ticks}}}}
function tickText(value,unit,step){{if(!Number.isFinite(value))return'';const v=Math.abs(value)<1e-10?0:value;const decimals=step<1?1:0;return v.toLocaleString('ja-JP',{{minimumFractionDigits:decimals,maximumFractionDigits:decimals,useGrouping:false}})}}
function draw(){{const c=chart,rect=c.getBoundingClientRect(),dpr=Math.max(1,window.devicePixelRatio||1);c.width=Math.max(1,Math.round(rect.width*dpr));c.height=Math.max(1,Math.round(rect.height*dpr));const ctx=c.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);const w=rect.width,h=rect.height,mobile=w<620,dual=second.length>0,L=mobile?68:96,R=dual?(mobile?58:92):30,T=42,B=72,start=new Date('{date:yyyy-MM-dd}T00:00:00').getTime(),end=start+86400000,plotW=w-L-R,plotH=h-T-B;ctx.fillStyle='#15191f';ctx.fillRect(0,0,w,h);const primaryUnit={(isPower?"'W'":"'℃'")},secondaryUnit='%';const primaryAxis=makeAxis(finiteValues(series),primaryUnit),secondaryAxis=dual?makeAxis(finiteValues(second),secondaryUnit):null;const X=t=>L+plotW*(t-start)/(end-start),Y=(v,a)=>T+plotH*(1-(v-a.min)/(a.max-a.min));ctx.font=(mobile?'12px':'13px')+' sans-serif';ctx.lineWidth=1;ctx.strokeStyle='rgba(255,255,255,.16)';ctx.fillStyle='#ccd2dc';for(let i=0;i<=6;i++){{const xx=L+plotW*i/6;ctx.beginPath();ctx.moveTo(xx,T);ctx.lineTo(xx,h-B);ctx.stroke();ctx.textAlign='center';ctx.textBaseline='top';ctx.fillText(String(i*4).padStart(2,'0')+':00',xx,h-B+12)}}const gridAxis=primaryAxis||secondaryAxis;if(gridAxis){{const rows=Math.max(1,gridAxis.ticks.length-1);for(let i=0;i<=rows;i++){{const yy=T+plotH*i/rows;ctx.beginPath();ctx.moveTo(L,yy);ctx.lineTo(w-R,yy);ctx.stroke()}}}}function drawAxis(a,unit,right,color){{if(!a)return;ctx.fillStyle=color;ctx.textBaseline='middle';const rows=Math.max(1,a.ticks.length-1);a.ticks.forEach((v,i)=>{{const yy=T+plotH*(1-i/rows);ctx.textAlign=right?'left':'right';ctx.fillText(tickText(v,unit,a.step),right?w-R+8:L-8,yy)}});ctx.textBaseline='top';ctx.font='bold '+(mobile?'13px':'14px')+' sans-serif';ctx.textAlign=right?'right':'left';ctx.fillText(unit,right?w-R:L,T-28);ctx.font=(mobile?'12px':'13px')+' sans-serif'}}drawAxis(primaryAxis,primaryUnit,false,{(isPower?"'#d8e3f0'":"'#00eaff'")});drawAxis(secondaryAxis,secondaryUnit,true,'#ff42c8');ctx.strokeStyle='rgba(255,255,255,.30)';ctx.strokeRect(L+.5,T+.5,plotW-1,plotH-1);function plot(groups,a){{if(!a)return;groups.forEach(s=>{{const pts=s.points.map(p=>({{t:new Date(p.t).getTime(),v:+p.v}})).filter(p=>Number.isFinite(p.t)&&Number.isFinite(p.v)).sort((x,y)=>x.t-y.t);if(!pts.length)return;ctx.strokeStyle=s.color||'#ffffff';ctx.lineWidth=s.isTotal?5:2.4;ctx.setLineDash(s.metric==='humidity'?[7,4]:[]);ctx.beginPath();let last=null;pts.forEach(p=>{{const xx=X(p.t),yy=Y(p.v,a);if(last===null||p.t-last>150000)ctx.moveTo(xx,yy);else ctx.lineTo(xx,yy);last=p.t}});ctx.stroke();ctx.setLineDash([])}})}}plot(series,primaryAxis);plot(second,secondaryAxis);if(!primaryAxis&&!secondaryAxis){{ctx.fillStyle='#ddd';ctx.font='28px sans-serif';ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText('データなし',L+plotW/2,T+plotH/2)}}function legendItem(s){{const span=document.createElement('span');span.style.color=s.color||'#ffffff';span.textContent=(s.isTotal?'━ ':'')+s.name+(s.metric==='temperature'?' 温度':s.metric==='humidity'?' 湿度':'')+(s.points.length?'':'（データなし）');return span}}legend.replaceChildren(...series.map(legendItem),...second.map(legendItem))}}draw();window.addEventListener('resize',draw);if(today)setTimeout(()=>location.reload(),60000);
</script></body></html>";
    }

    private async Task<string> BuildGraphHtml(string id,DateOnly? requestedDate)
    {
        var device=_get().FirstOrDefault(d=>d.Id.Equals(id,StringComparison.OrdinalIgnoreCase)||d.Name.Equals(id,StringComparison.OrdinalIgnoreCase));
        if(device is null)return "<!doctype html><meta charset='utf-8'><body style='background:#0f1319;color:white;font-family:sans-serif'><h1>Device not found</h1></body>";
        var date=requestedDate??DateOnly.FromDateTime(DateTime.Now); var today=date==DateOnly.FromDateTime(DateTime.Now);
        var metric=device.Kind==DeviceKind.Power?"power":device.Kind==DeviceKind.Humidity?"humidity":"temperature";
        var primary=device.IsPowerSummary?await _historyService.ReadAggregateForDateAsync(_get().Where(x=>x.Kind==DeviceKind.Power&&!x.IsPowerSummary).Select(x=>x.Id),date):await _historyService.ReadMetricForDateAsync(device.Id,metric,date);
        var secondary=device.Kind==DeviceKind.Environment?await _historyService.ReadMetricForDateAsync(device.Id,"humidity",date):[];
        var dates=device.IsPowerSummary?await _historyService.GetAvailableAggregateDatesAsync(_get().Where(x=>x.Kind==DeviceKind.Power&&!x.IsPowerSummary).Select(x=>x.Id)):await _historyService.GetAvailableDatesAsync(device.Id,metric);
        var pj=JsonSerializer.Serialize(primary.Select(p=>new{t=p.Time.ToString("O"),v=p.Value})); var sj=JsonSerializer.Serialize(secondary.Select(p=>new{t=p.Time.ToString("O"),v=p.Value}));
        var unit=device.Kind==DeviceKind.Power?"W":device.Kind==DeviceKind.Humidity?"%":"℃"; var dual=device.Kind==DeviceKind.Environment;
        var title=WebUtility.HtmlEncode(device.Name);
        var graphPowerDevices=(device.IsPowerSummary?_get().Where(x=>x.Kind==DeviceKind.Power&&!x.IsPowerSummary):_get().Where(x=>x.Id.Equals(device.Id,StringComparison.OrdinalIgnoreCase)&&x.Kind==DeviceKind.Power)).ToList();
        var graphTodayWh=graphPowerDevices.Sum(x=>x.TodayWh??0);var graphMonthWh=graphPowerDevices.Sum(x=>x.MonthWh??0);var graphRate=_rate();
        var graphPastWh=device.Kind==DeviceKind.Power&&!today?IntegratePowerWh(primary):null;
        var graphPastKwh=graphPastWh/1000.0;
        var graphPastCost=graphPastKwh*graphRate;
        var graphEnergyHtml=device.Kind==DeviceKind.Power&&today?$"<div class='energy-stats'><div class='energy-stat'>本日合計<br><b>{graphTodayWh/1000.0:0.000} kWh</b></div><div class='energy-stat'>本日概算<br><b>{graphTodayWh/1000.0*graphRate:N1} 円</b></div><div class='energy-stat'>月間計<br><b>{graphMonthWh/1000.0:0.000} kWh</b></div><div class='energy-stat'>月間概算<br><b>{graphMonthWh/1000.0*graphRate:N1} 円</b></div></div>":device.Kind==DeviceKind.Power&&!today&&graphPastWh is null?"<div class='energy-note'>指定日の電力量を計算できる連続データが不足しています</div>":"";
        var encodedId=WebUtility.UrlEncode(device.Id);var dateLinks=string.Join("",dates.Select(d=>$"<a class=\"date-button{(d==date?" selected":"")}\" href=\"/graph?id={encodedId}&amp;date={d:yyyy-MM-dd}\" target=\"_blank\" rel=\"noopener noreferrer\" aria-label=\"{d:yyyy-MM-dd} の履歴を開く\">{d:yyyy-MM-dd}</a>"));
        return $@"<!doctype html><html><head><meta charset='utf-8'><meta name=viewport content='width=device-width,initial-scale=1'><title>{title} {date:yyyy-MM-dd}</title><style>:root{{color-scheme:dark}}body{{margin:0;background:#0f1319;color:#f4f6fb;font-family:'Yu Gothic UI',Meiryo,Segoe UI,sans-serif;padding:22px}}.head{{display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap}}h1{{margin:0}}button,.date-button{{background:#252d39;color:white;border:1px solid #798392;border-radius:9px;padding:8px 12px;text-decoration:none}}.date-button.selected{{outline:2px solid #b58cff}}.calendar{{display:none;position:absolute;right:22px;background:#202733;border:1px solid #77808d;padding:10px;border-radius:10px;z-index:3;max-height:260px;overflow:auto}}.calendar.open{{display:grid;grid-template-columns:repeat(4,auto);gap:6px}}.panel{{background:#181d25;border:1px solid #636b78;border-radius:16px;padding:16px;margin-top:14px}}canvas{{width:100%;height:62vh;background:#15191f;border-radius:10px}}.legend{{display:flex;gap:18px;justify-content:center;margin:8px}}.stats{{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:10px}}.stat{{background:#232a34;border-radius:10px;padding:10px;text-align:center}}.stat b{{font-family:Impact,'Arial Narrow','Roboto Condensed','Yu Gothic UI',Meiryo,sans-serif;font-weight:normal;font-variant-numeric:tabular-nums;font-size:22px;color:#88f69d}}.energy-stats{{display:grid;grid-template-columns:repeat(4,minmax(140px,1fr));gap:10px;margin-top:10px}}.energy-stat{{background:#232a34;border-radius:10px;padding:10px;text-align:center}}.energy-stat b{{font-family:Impact,'Arial Narrow','Roboto Condensed','Yu Gothic UI',Meiryo,sans-serif;font-weight:normal;font-variant-numeric:tabular-nums;font-size:22px;color:#88f69d}}.energy-note{{color:#c8ced8;margin-top:10px}}@media(max-width:720px){{.energy-stats{{grid-template-columns:repeat(2,minmax(130px,1fr))}}}}@media(max-width:420px){{.energy-stats{{grid-template-columns:1fr}}}}</style></head><body><div class='head'><h1>{title} <small>{date:yyyy-MM-dd}</small></h1><div><span id='now'></span> <button id='calendarButton' aria-label='履歴日付を選択' type='button'>📅</button><div id='cal' class='calendar'>{dateLinks}</div></div></div><div class='panel'><canvas id='chart' width='1400' height='680'></canvas>{(dual?"<div class='legend'><span style='color:#00eaff'>■ 温度（左軸）</span><span style='color:#ff42c8'>■ 湿度（右軸）</span></div>":"")}<div id='stats' class='stats'></div>{graphEnergyHtml}</div><script>const primary={pj}.map(p=>({{t:new Date(p.t),v:+p.v}})),secondary={sj}.map(p=>({{t:new Date(p.t),v:+p.v}})),today={(today?"true":"false")},unit={JsonSerializer.Serialize(unit)},dual={(dual?"true":"false")},pastPowerKwh={(graphPastKwh is null?"null":graphPastKwh.Value.ToString("0.############",CultureInfo.InvariantCulture))},pastPowerCost={(graphPastCost is null?"null":graphPastCost.Value.ToString("0.############",CultureInfo.InvariantCulture))};calendarButton.addEventListener('click',e=>{{e.stopPropagation();cal.classList.toggle('open')}});document.addEventListener('click',e=>{{if(!cal.contains(e.target)&&e.target!==calendarButton)cal.classList.remove('open')}});document.addEventListener('keydown',e=>{{if(e.key==='Escape')cal.classList.remove('open')}});function niceNumber(value,round){{if(!Number.isFinite(value)||value<=0)return 1;const exponent=Math.floor(Math.log10(value)),fraction=value/Math.pow(10,exponent);let niceFraction;if(round)niceFraction=fraction<1.5?1:fraction<2.25?2:fraction<3.75?2.5:fraction<7.5?5:10;else niceFraction=fraction<=1?1:fraction<=2?2:fraction<=2.5?2.5:fraction<=5?5:10;return niceFraction*Math.pow(10,exponent)}}function makeAxis(values,u){{const a=values.filter(Number.isFinite);if(!a.length)return null;let dataMin=Math.min(...a),dataMax=Math.max(...a);const containsZero=a.some(v=>v===0),nonNegative=u==='W'||u==='%';if(nonNegative){{dataMin=Math.max(0,dataMin);dataMax=Math.max(0,dataMax)}}let span=dataMax-dataMin;if(span<=0)span=Math.max(Math.abs(dataMax)*.1,u==='℃'?.5:u==='%'?2:1);let paddedMin=dataMin-span*.08,paddedMax=dataMax+span*.08;if(nonNegative)paddedMin=Math.max(0,paddedMin);if(containsZero)paddedMin=0;if(u==='%'){{paddedMin=Math.max(0,paddedMin);paddedMax=Math.min(100,Math.max(paddedMax,paddedMin+1))}}let step=niceNumber((paddedMax-paddedMin)/5,true),min=Math.floor(paddedMin/step)*step;if(nonNegative)min=Math.max(0,min);if(containsZero)min=0;let max=min+step*5;while(max<paddedMax-step*.001){{step=niceNumber(step*1.01,false);min=Math.floor(paddedMin/step)*step;if(nonNegative)min=Math.max(0,min);if(containsZero)min=0;max=min+step*5}}if(u==='%'){{if(max>100){{max=100;min=Math.max(0,max-step*5)}}if(min<0)min=0}}if(max<=min)max=min+step*5;const ticks=Array.from({{length:6}},(_,i)=>{{const v=min+i*(max-min)/5;return Math.abs(v)<step*1e-9?0:v}});return{{min,max,step:(max-min)/5,ticks}}}}function tickText(value,u,step){{if(!Number.isFinite(value))return'';const v=Math.abs(value)<1e-10?0:value,decimals=step<1?1:0;return v.toLocaleString('ja-JP',{{minimumFractionDigits:decimals,maximumFractionDigits:decimals,useGrouping:false}})}}function draw(){{const c=chart,rect=c.getBoundingClientRect(),dpr=Math.max(1,window.devicePixelRatio||1);c.width=Math.max(1,Math.round(rect.width*dpr));c.height=Math.max(1,Math.round(rect.height*dpr));const ctx=c.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);const w=rect.width,h=rect.height,mobile=w<620,L=mobile?68:96,R=dual?(mobile?58:92):30,T=42,B=72,start=new Date('{date:yyyy-MM-dd}T00:00:00').getTime(),end=start+86400000,plotW=w-L-R,plotH=h-T-B,pb=makeAxis(primary.map(x=>x.v),unit),sb=dual?makeAxis(secondary.map(x=>x.v),'%'):null,X=t=>L+plotW*(t-start)/(end-start),Y=(v,z)=>T+plotH*(1-(v-z.min)/(z.max-z.min));ctx.fillStyle='#15191f';ctx.fillRect(0,0,w,h);ctx.font=(mobile?'12px':'13px')+' sans-serif';ctx.lineWidth=1;ctx.strokeStyle='rgba(255,255,255,.16)';ctx.fillStyle='#ccd2dc';for(let i=0;i<=6;i++){{const xx=L+plotW*i/6;ctx.beginPath();ctx.moveTo(xx,T);ctx.lineTo(xx,h-B);ctx.stroke();ctx.textAlign='center';ctx.textBaseline='top';ctx.fillText(String(i*4).padStart(2,'0')+':00',xx,h-B+12)}}const gridAxis=pb||sb;if(gridAxis){{const rows=Math.max(1,gridAxis.ticks.length-1);for(let i=0;i<=rows;i++){{const yy=T+plotH*i/rows;ctx.beginPath();ctx.moveTo(L,yy);ctx.lineTo(w-R,yy);ctx.stroke()}}}}function drawAxis(a,u,right,color){{if(!a)return;ctx.fillStyle=color;ctx.textBaseline='middle';const rows=Math.max(1,a.ticks.length-1);a.ticks.forEach((v,i)=>{{const yy=T+plotH*(1-i/rows);ctx.textAlign=right?'left':'right';ctx.fillText(tickText(v,u,a.step),right?w-R+8:L-8,yy)}});ctx.textBaseline='top';ctx.font='bold '+(mobile?'13px':'14px')+' sans-serif';ctx.textAlign=right?'right':'left';ctx.fillText(u,right?w-R:L,T-28);ctx.font=(mobile?'12px':'13px')+' sans-serif'}}drawAxis(pb,unit,false,dual?'#00eaff':'#d8e3f0');drawAxis(sb,'%',true,'#ff42c8');ctx.strokeStyle='rgba(255,255,255,.30)';ctx.strokeRect(L+.5,T+.5,plotW-1,plotH-1);function line(a,col,z){{if(!z)return;const pts=a.filter(p=>Number.isFinite(p.v)).sort((x,y)=>x.t-y.t);ctx.strokeStyle=col;ctx.lineWidth=4;ctx.beginPath();let last=null;pts.forEach(p=>{{const tm=p.t.getTime(),xx=X(tm),yy=Y(p.v,z);if(last===null||tm-last>150000)ctx.moveTo(xx,yy);else ctx.lineTo(xx,yy);last=tm}});ctx.stroke()}}line(secondary,'#ff42c8',sb);line(primary,'#00eaff',pb);if(!pb&&!sb){{ctx.fillStyle='#ddd';ctx.font='28px sans-serif';ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText('データなし',L+plotW/2,T+plotH/2)}}let v=primary.map(x=>x.v).filter(x=>Number.isFinite(x)&&(unit!=='W'||x>=0));if(unit==='W'&&!today){{const min=v.length?Math.min(...v):null,max=v.length?Math.max(...v):null,cards=[['最小',min===null?'--':min.toFixed(1)+' W'],['最大',max===null?'--':max.toFixed(1)+' W'],['合計使用',pastPowerKwh===null?'--':pastPowerKwh.toFixed(3)+' kWh'],['概算料金',pastPowerCost===null?'--':pastPowerCost.toFixed(1)+' 円']];stats.innerHTML=cards.map(x=>`<div class=stat>${{x[0]}}<br><b>${{x[1]}}</b></div>`).join('');now.textContent=''}}else{{stats.innerHTML=v.length?[['現在',v.at(-1)],['最小',Math.min(...v)],['最大',Math.max(...v)],['平均',v.reduce((a,b)=>a+b,0)/v.length]].map(x=>`<div class=stat>${{x[0]}}<br><b>${{x[1].toFixed(1)}} ${{unit}}</b></div>`).join(''):'<div class=stat>データなし</div>';now.textContent=primary.length?`現在 ${{primary.at(-1).v.toFixed(1)}} ${{unit}} ${{primary.at(-1).t.toLocaleTimeString('ja-JP',{{hour:'2-digit',minute:'2-digit'}})}}`:'データなし'}}}}draw();window.addEventListener('resize',draw);if(today)setTimeout(()=>location.reload(),60000);</script></body></html>";
    }

    private static bool IsViewVisible(DeviceSnapshot d)=>d.IsPowerSummary||(d.Kind is DeviceKind.Power or DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity);
    private static bool IsDashboardSelectable(DeviceSnapshot d)=>!d.IsPowerSummary&&(d.Kind is DeviceKind.Power or DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity);

    private static readonly string[] DeviceSeriesPalette =
    [
        "#00d8ff", "#ff2bd6", "#88ff9a", "#ff914d", "#b58cff",
        "#00ffff", "#ff777d", "#beff48", "#6ca8ff", "#f0a0ff"
    ];

    private static string DeviceSeriesColor(string? deviceId,string? fallbackName=null)
    {
        var key=string.IsNullOrWhiteSpace(deviceId)?fallbackName??string.Empty:deviceId;
        // Stable FNV-1a over UTF-8. Do not use string.GetHashCode(), which is process-dependent.
        uint hash=2166136261;
        foreach(var b in Encoding.UTF8.GetBytes(key)) { hash^=b; hash*=16777619; }
        return DeviceSeriesPalette[(int)(hash%(uint)DeviceSeriesPalette.Length)];
    }

    private static double? FiniteOrNull(double? value)
        => value is not null&&!double.IsNaN(value.Value)&&!double.IsInfinity(value.Value)?value:null;

    private static double? IntegratePowerWh(IEnumerable<HistoryPoint> source)
    {
        var points=source
            .Where(p=>double.IsFinite(p.Value)&&p.Value>=0)
            .OrderBy(p=>p.Time)
            .GroupBy(p=>p.Time)
            .Select(g=>g.Last())
            .ToList();
        if(points.Count<2)return null;
        double wh=0; var integrated=false;
        for(var i=1;i<points.Count;i++)
        {
            var seconds=(points[i].Time-points[i-1].Time).TotalSeconds;
            if(seconds<=0||seconds>150)continue;
            wh+=((points[i-1].Value+points[i].Value)/2.0)*seconds/3600.0;
            integrated=true;
        }
        return integrated&&double.IsFinite(wh)?wh:null;
    }
    private static List<DeviceSnapshot> GetDashboardDevices(IEnumerable<DeviceSnapshot> devices,bool management)
        => devices.Where(d=>d.Kind!=DeviceKind.Hub&&(management||IsViewVisible(d))).ToList();

    private async Task<string> BuildDashboardHistoryJsonAsync(DeviceSnapshot device)
    {
        List<HistoryPoint> primary=[]; List<HistoryPoint> secondary=[]; string unit="";
        try
        {
            switch(device.Kind)
            {
                case DeviceKind.Power: primary=await _historyService.ReadMetric24hAsync(device.Id,"power",device.PowerWatts); unit="W"; break;
                case DeviceKind.Temperature: primary=await _historyService.ReadMetric24hAsync(device.Id,"temperature",device.TemperatureC); unit="℃"; break;
                case DeviceKind.Humidity: primary=await _historyService.ReadMetric24hAsync(device.Id,"humidity",device.HumidityPercent); unit="%"; break;
                case DeviceKind.Environment:
                    primary=await _historyService.ReadMetric24hAsync(device.Id,"temperature",device.TemperatureC);
                    secondary=await _historyService.ReadMetric24hAsync(device.Id,"humidity",device.HumidityPercent); unit="℃/%"; break;
            }
        }
        catch { }
        var stale=device.Online&&(DateTime.Now-device.Timestamp)>TimeSpan.FromMinutes(Math.Max(1,_staleDeviceMinutes()));
        double? currentPrimary=device.Kind switch
        {
            DeviceKind.Power=>FiniteOrNull(device.PowerWatts),
            DeviceKind.Temperature=>FiniteOrNull(device.TemperatureC),
            DeviceKind.Humidity=>FiniteOrNull(device.HumidityPercent),
            DeviceKind.Environment=>FiniteOrNull(device.TemperatureC),
            _=>null
        };
        var currentSecondary=device.Kind==DeviceKind.Environment?FiniteOrNull(device.HumidityPercent):null;
        var primaryUnit=device.Kind switch { DeviceKind.Power=>"W",DeviceKind.Humidity=>"%",_=>"℃" };
        var secondaryUnit=device.Kind==DeviceKind.Environment?"%":null;
        return JsonSerializer.Serialize(new
        {
            id=device.Id,name=device.Name,kind=device.Kind.ToString(),unit,
            currentPrimary,currentSecondary,primaryUnit,secondaryUnit,
            primaryColorClass=device.Kind switch { DeviceKind.Power=>ValueColorRules.Power(currentPrimary),DeviceKind.Humidity=>ValueColorRules.Humidity(currentPrimary),_=>ValueColorRules.Temperature(currentPrimary) },
            secondaryColorClass=device.Kind==DeviceKind.Environment?ValueColorRules.Humidity(currentSecondary):ValueColorRules.Neutral,
            timestamp=device.Timestamp==default?null:device.Timestamp.ToString("O"),online=device.Online,stale,
            colorHex=DeviceSeriesColor(device.Id,device.Name),
            primary=primary.Select(p=>new{t=p.Time.ToString("O"),v=p.Value}),
            secondary=secondary.Select(p=>new{t=p.Time.ToString("O"),v=p.Value})
        });
    }

    private async Task<string> BuildMiniGraphsAsync(IReadOnlyList<DeviceSnapshot> devices)
    {
        if(_history is null)return string.Empty;var ids=(_viewGraphIds()??Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();if(ids.Count==0)return "<div class='mini-empty'>Webサーバー設定で小型グラフを最大4台選択できます。</div>";
        var items=new List<string>();foreach(var id in ids){var d=devices.FirstOrDefault(x=>x.Id.Equals(id,StringComparison.OrdinalIgnoreCase));if(d is null)continue;List<HistoryPoint> points;try{points=await _history(id);}catch{points=[];}var json=JsonSerializer.Serialize(points.Select(x=>new{t=x.Time.ToString("O"),v=x.Value}));items.Add($"<article class='mini'><h3>{WebUtility.HtmlEncode(d.Name)}</h3><canvas class='mini-chart' data-points='{WebUtility.HtmlEncode(json)}'></canvas></article>");}return items.Count>0?string.Join("",items):"<div class='mini-empty'>選択したデバイスの履歴がありません。</div>";
    }

    private Task<string> BuildHtmlAsync(IEnumerable<DeviceSnapshot> devices,bool management)
    {
        var ds=GetDashboardDevices(devices,management);
        var powerForTotals=ds.Where(d=>d.Kind==DeviceKind.Power&&!d.IsPowerSummary&&d.PowerWatts is >=0 and <=3000).ToList();
        var rate=_rate();
        string H(string? value)=>WebUtility.HtmlEncode(value??string.Empty);
        double totalW=powerForTotals.Sum(d=>d.PowerWatts??0),totalWh=powerForTotals.Sum(d=>d.TodayWh??0),yen=totalWh/1000.0*rate;
        string groupCss(DeviceGroupKind g)=>g switch{DeviceGroupKind.Power=>"power",DeviceGroupKind.Environment=>"env",_=>"sw"};
        static bool valid(double? v)=>v is not null&&!double.IsNaN(v.Value)&&!double.IsInfinity(v.Value);
        string number(double? v,string format)=>valid(v)?v!.Value.ToString(format,CultureInfo.InvariantCulture):"--";
        string colored(string semantic,string color,double? v,string format,string unit)=>$"<span class='value-{semantic} {color}'>{number(v,format)}<small>{unit}</small></span>";
        string valueHtml(DeviceSnapshot d)=>d.Kind switch
        {
            DeviceKind.Power=>colored("power",ValueColorRules.Power(d.PowerWatts),d.PowerWatts,"0","W"),
            DeviceKind.Environment=>colored("temperature",ValueColorRules.Temperature(d.TemperatureC),d.TemperatureC,"0.0","℃")+colored("humidity",ValueColorRules.Humidity(d.HumidityPercent),d.HumidityPercent,"0","%"),
            DeviceKind.Temperature=>colored("temperature",ValueColorRules.Temperature(d.TemperatureC),d.TemperatureC,"0.0","℃"),
            DeviceKind.Humidity=>colored("humidity",ValueColorRules.Humidity(d.HumidityPercent),d.HumidityPercent,"0","%"),
            DeviceKind.Switch=>$"<span class='{(d.IsOn==true?"green":"off")}'>{(d.IsOn==true?"ON":"OFF")}</span>",
            _=>"<span class='neutral'>--</span>"
        };
        string selector(DeviceSnapshot d)=>IsDashboardSelectable(d)?$"<label class='dashboard-selector' title='小型グラフへ表示'><input type='checkbox' class='dashboard-checkbox' data-device-id='{H(d.Id)}' aria-label='{H(d.Name)}を小型グラフへ表示'><span aria-hidden='true'></span></label>":"";
        string card(DeviceSnapshot d)
        {
            var graph=IsViewVisible(d); var click=graph?$" onclick=\"if(!event.target.closest('.dashboard-selector'))openGraph('{H(d.Id)}')\" title='クリックでグラフを開く'":"";
            if(d.Kind==DeviceKind.Switch)click=management?$" ondblclick=\"toggleSwitch('{H(d.Id)}')\" title='ダブルクリックでON/OFF'":"";
            var age=DateTime.Now-d.Timestamp;
            var stale=d.Online&&age>TimeSpan.FromMinutes(Math.Max(1,_staleDeviceMinutes()));
            var stateClass=!d.Online?"offline":stale?"stale":"";
            var stateText=!d.Online?$"Offline / 最終取得 {d.Timestamp:HH:mm:ss}":stale?$"取得遅延 / 最終取得 {d.Timestamp:HH:mm:ss}":$"online / {d.Timestamp:HH:mm:ss}";
            if(d.IsPowerSummary){var cost=(d.TodayWh??0)/1000.0*rate;return $"<article class='card power summary-card {stateClass}' data-id='{H(d.Id)}'{click}><div class='card-header'><div class='name'>使用電力</div></div><div class='summary-current-card'><span class='summary-card-value value-power {ValueColorRules.Power(d.PowerWatts)}'>{number(d.PowerWatts,"0")}</span><span class='summary-card-unit {ValueColorRules.Power(d.PowerWatts)}'>W</span></div><div class='summary-sub'><div>本日使用電力 <b>{d.TodayWh??0:0.0} Wh</b></div><div>本日概算金額 <b>{cost:0.0} 円</b></div></div></article>";}
            var ip=string.IsNullOrWhiteSpace(d.Ip)?d.Hub:d.Ip;var today=d.Kind==DeviceKind.Power&&d.TodayWh is not null?$"<span>TODAY <b>{d.TodayWh:0.0} Wh</b></span>":"";
            var nameStyle=IsDashboardSelectable(d)?$" style='color:{DeviceSeriesColor(d.Id,d.Name)}'":string.Empty;
            return $"<article class='card {groupCss(d.GroupKind)} {(d.IsOn==true?"on":d.IsOn==false?"off":"")} {stateClass} {(IsDashboardSelectable(d)?"selectable-card":"")}' data-id='{H(d.Id)}' data-kind='{d.Kind}'{click}><div class='card-header'><div class='name'{nameStyle}>{H(d.Name)}</div>{selector(d)}</div><div class='val'>{valueHtml(d)}</div><div class='meta'><span>{H(ip)}</span><span>{H(stateText)}</span>{today}</div></article>";
        }
        string section(string title,DeviceGroupKind group)
        {
            var list=ds.Where(d=>d.GroupKind==group).OrderBy(d=>group==DeviceGroupKind.Power&&!d.IsPowerSummary?1:0).ThenBy(d=>d.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).ToList();
            if(list.Count==0)return string.Empty;
            var graphButton=group==DeviceGroupKind.Switch?"":$"<button class='graph-button' aria-label='{H(title)}の系列グラフを開く' onclick=\"event.stopPropagation();openSeries('{groupCss(group)}')\">グラフ</button>";
            return $"<section class='section {groupCss(group)} collapsed' data-group='{groupCss(group)}'><div class='section-title'><button class='collapse-button' aria-label='{H(title)}を展開または折りたたむ' onclick='toggleSection(this)'>▶</button><span class='section-name'>{H(title)}</span><span>{list.Count} devices</span>{graphButton}</div><div class='cards'>{string.Join("",list.Select(card))}</div></section>";
        }
        var pageTitle=management?"TapoCtrl 管理":"TapoCtrl 閲覧";var basePath=management?"/Ctrl/":"/View/";var updated=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var sections=section("電力系",DeviceGroupKind.Power)+section("観測系",DeviceGroupKind.Environment)+(management?section("スイッチ系",DeviceGroupKind.Switch):string.Empty);
        var switchScript=management?"""async function toggleSwitch(id){try{const r=await fetch('/Ctrl/api/toggle?id='+encodeURIComponent(id),{method:'POST',headers:{'X-TapoCtrl-Request':'1','X-TapoCtrl-Control':'__CONTROL_TOKEN__'},cache:'no-store'});if(!r.ok)throw new Error('HTTP '+r.status);setTimeout(updateDashboard,500);}catch(e){alert('ON/OFFできません: '+e);}}""".Replace("__CONTROL_TOKEN__",_controlToken,StringComparison.Ordinal):"";
        var html="""
<!doctype html><html><head><meta charset='utf-8'><meta name=viewport content='width=device-width,initial-scale=1'><title>__PAGE_TITLE__</title><style>
:root{color-scheme:dark;--device-value-font-size:42px;--summary-value-font-size:63px}*{box-sizing:border-box}body{margin:0;background:#0f1319;color:#f4f6fb;font-family:'Yu Gothic UI',Meiryo,Segoe UI,sans-serif;padding:22px}h1{margin:0}.top{display:flex;gap:18px;align-items:center;flex-wrap:wrap}.updated{margin-left:auto}.summary{border:1px solid #6d7280;border-radius:14px;padding:14px;margin:14px 0;background:#141922;text-align:center}.summary-current{display:flex;justify-content:center;align-items:baseline;gap:.45em;flex-wrap:wrap}.summary-current-label{font-size:1rem}.summary-current-reading,.summary-card-current{display:inline-flex;align-items:baseline;gap:.12em;white-space:nowrap}.summary-current-value,.summary-card-value{font-family:Impact,'Arial Narrow','Roboto Condensed','Yu Gothic UI',Meiryo,sans-serif;font-size:clamp(52px,6vw,var(--summary-value-font-size));line-height:1}.summary-current-unit,.summary-card-unit{font-size:.52em;font-weight:800}.summary-energy{margin-top:6px}.summary-current-card{display:flex;align-items:baseline;gap:.12em;white-space:nowrap;min-width:0;overflow:hidden}.summary-current-card .summary-card-value{min-width:0}.summary-current-card .summary-card-unit{font-size:clamp(24px,3vw,34px)}.section{border:2px solid #5d6573;border-radius:16px;margin:16px 0;padding:10px;background:#181d25}.section-title{padding:10px;border-radius:12px;color:#10151b;display:flex;align-items:center;gap:10px;font-size:22px;font-weight:900}.section-name{flex:1}.power{border-color:#e9d878}.power .section-title{background:#e9d878}.env{border-color:#caa3ee}.env .section-title{background:#caa3ee}.sw{border-color:#8fd8a6}.sw .section-title{background:#8fd8a6}.section.collapsed .cards{display:none}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:14px;margin-top:14px}.card{background:#232a34;border:1.5px solid #737b89;border-radius:14px;padding:16px 18px;min-height:132px;cursor:pointer}.card.offline,.card.stale{border-color:#ff5a64!important;background:#3a2026}.card-header{display:flex;align-items:flex-start;gap:10px}.name{font-weight:800;font-size:22px;flex:1;min-width:0;overflow-wrap:anywhere}.val{font-size:var(--device-value-font-size);margin:10px 0;display:flex;gap:30px;align-items:baseline}.value-power,.value-temperature,.value-humidity,.summary-current-value,.summary-card-value,.summary-energy b,.summary-sub b,.meta b,.mini-current-number,.stat b,.energy-stat b{font-family:Impact,'Arial Narrow','Roboto Condensed','Yu Gothic UI',Meiryo,sans-serif;font-weight:normal;font-variant-numeric:tabular-nums}.val small{font-size:.48em;margin-left:2px}.purple{color:#6e2cff}.deepmagenta{color:#c2185b}.red{color:#ff4b3c}.yellow{color:#ffd61f}.lime{color:#beff48}.green{color:#24ff60}.lightblue{color:#5cd2ff}.darkblue{color:#2060ff}.white{color:#fff}.neutral{color:#aab0bc}.off{color:#ff777d}.meta{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:4px 14px;color:#d0d5dd}.dashboard-selector{margin-left:auto;display:grid;place-items:center;padding:2px;cursor:pointer}.dashboard-checkbox{width:22px;height:22px;accent-color:#b58cff}.dashboard-checkbox:disabled{opacity:.35}.mini-grid{display:none;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin:14px 0}.mini-grid.active{display:grid}.mini{background:#171d25;border:1px solid #626d7c;border-radius:14px;padding:0;overflow:hidden}.mini.offline,.mini.stale{border-color:#ff5a64}.mini-link{display:block;color:inherit;text-decoration:none;padding:10px;cursor:pointer}.mini-link:focus-visible{outline:3px solid #b58cff;outline-offset:-3px}.mini-header{display:flex;justify-content:space-between;align-items:flex-start;gap:12px;margin-bottom:6px}.mini-device-name{margin:0;font-size:17px;min-width:0;overflow-wrap:anywhere}.mini-current{text-align:right;display:flex;align-items:baseline;justify-content:flex-end;gap:.24em;flex-wrap:wrap}.mini-current-label,.mini-current-time,.mini-current-state{font-size:12px;color:#bfc7d2}.mini-current-number{font-size:25px;line-height:1}.mini-current-unit{font-size:14px;font-weight:700}.mini canvas{width:100%;height:150px;background:#11161d;border-radius:8px}.dashboard-note{text-align:center;color:#bfc7d2;margin:8px}.warning{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);background:#772c35;color:#fff;padding:12px 18px;border-radius:10px;display:none;z-index:20}button{background:#222a35;color:#fff;border:1px solid #7a8292;border-radius:10px;padding:7px 12px;font-weight:700}@media(max-width:700px){body{padding:10px}.mini-grid{grid-template-columns:1fr}.cards{grid-template-columns:1fr}.mini-header{flex-direction:column}.mini-current{text-align:left;justify-content:flex-start}}
</style></head><body><div class='top'><h1>__PAGE_TITLE__</h1><span>__MODE_TEXT__</span><span class='updated'>updated: <b>__UPDATED__</b></span></div><div class='summary'><div class='summary-current'><span class='summary-current-label'>現在使用電力</span><span class='summary-current-reading'><span class='summary-current-value value-power __TOTAL_W_CLASS__'>__TOTAL_W__</span><span class='summary-current-unit __TOTAL_W_CLASS__'>W</span></span></div><div class='summary-energy'>本日使用電力 <b>__TOTAL_WH__ Wh</b> ／ 本日概算金額 <b>__YEN__ 円</b></div></div><div class='dashboard-note'>各デバイス右上のチェックで、小型グラフを最大4台まで表示できます。</div><div id='miniGrid' class='mini-grid'></div>__SECTIONS__<div id='warning' class='warning'></div><script>
const BASE='__BASE_PATH__',STORE='tapoctrl.dashboard.selectedDevices.v1';function toggleSection(b){const s=b.closest('.section');s.classList.toggle('collapsed');b.textContent=s.classList.contains('collapsed')?'▶':'▼'}function openSeries(g){window.open(BASE+'series?group='+encodeURIComponent(g),'_blank')}function openGraph(id){window.open(BASE+'graph?id='+encodeURIComponent(id),'_blank')}__SWITCH_SCRIPT__
function eligible(){return [...document.querySelectorAll('.dashboard-checkbox')].map(x=>x.dataset.deviceId)}function loadSelected(){try{const x=JSON.parse(localStorage.getItem(STORE)||'{}'),valid=new Set(eligible()),ids=Array.isArray(x.ids)?x.ids:[];return [...new Set(ids)].filter(id=>valid.has(id)).slice(0,4)}catch{return[]}}function saveSelected(ids){try{localStorage.setItem(STORE,JSON.stringify({version:1,ids:ids.slice(0,4)}))}catch{}}function warn(){const w=document.getElementById('warning');w.textContent='小型グラフは最大4件まで表示できます';w.style.display='block';setTimeout(()=>w.style.display='none',2500)}function syncChecks(ids){const set=new Set(ids),full=ids.length>=4;document.querySelectorAll('.dashboard-checkbox').forEach(c=>{c.checked=set.has(c.dataset.deviceId);c.disabled=full&&!c.checked})}
function bindSelectorEvents(){document.querySelectorAll('.dashboard-selector').forEach(label=>{if(label.dataset.bound)return;label.dataset.bound='1';label.addEventListener('pointerdown',e=>e.stopPropagation());label.addEventListener('click',e=>{e.stopPropagation();const c=label.querySelector('.dashboard-checkbox');if(c?.disabled&&!c.checked){e.preventDefault();warn()}});label.addEventListener('change',e=>{e.stopPropagation();const c=e.target.closest('.dashboard-checkbox');if(!c)return;let ids=loadSelected(),id=c.dataset.deviceId;if(c.checked&&!ids.includes(id)){if(ids.length>=4){c.checked=false;warn();return}ids.push(id)}else if(!c.checked)ids=ids.filter(x=>x!==id);saveSelected(ids);syncChecks(ids);renderMini(ids)})})}
function safeSeriesColor(value){return /^#[0-9a-f]{6}$/i.test(value||'')?value:'#ffffff'}const VALUE_CLASSES=new Set(['white','darkblue','lightblue','green','yellow','red','deepmagenta','lime','purple','neutral']);function safeValueClass(value){return VALUE_CLASSES.has(value)?value:'neutral'}function formatCurrent(d){const primary=Number.isFinite(d.currentPrimary)?d.currentPrimary:null,secondary=Number.isFinite(d.currentSecondary)?d.currentSecondary:null;if(d.kind==='Power')return{primary:primary===null?'--':Math.round(primary).toString(),primaryUnit:'W',primaryClass:safeValueClass(d.primaryColorClass),secondary:null};if(d.kind==='Humidity')return{primary:primary===null?'--':Math.round(primary).toString(),primaryUnit:'%',primaryClass:safeValueClass(d.primaryColorClass),secondary:null};if(d.kind==='Environment')return{primary:primary===null?'--':primary.toFixed(1),primaryUnit:'℃',primaryClass:safeValueClass(d.primaryColorClass),secondary:secondary===null?'--':Math.round(secondary).toString(),secondaryUnit:'%',secondaryClass:safeValueClass(d.secondaryColorClass)};return{primary:primary===null?'--':primary.toFixed(1),primaryUnit:'℃',primaryClass:safeValueClass(d.primaryColorClass),secondary:null}}
async function renderMini(ids=loadSelected()){const grid=document.getElementById('miniGrid');grid.replaceChildren();grid.classList.toggle('active',ids.length>0);for(const id of ids){try{const r=await fetch(BASE+'api/dashboard-history?id='+encodeURIComponent(id),{cache:'no-store'});if(!r.ok)throw new Error('HTTP '+r.status);const d=await r.json(),article=document.createElement('article'),link=document.createElement('a'),header=document.createElement('div'),name=document.createElement('h3'),current=document.createElement('div'),label=document.createElement('span'),number=document.createElement('span'),unit=document.createElement('span'),time=document.createElement('time'),state=document.createElement('span'),canvas=document.createElement('canvas'),f=formatCurrent(d),color=safeSeriesColor(d.colorHex);article.className='mini'+(!d.online?' offline':d.stale?' stale':'');link.className='mini-link';link.href=BASE+'graph?id='+encodeURIComponent(d.id);link.target='_blank';link.rel='noopener noreferrer';link.setAttribute('aria-label',d.name+'の詳細グラフを開く');header.className='mini-header';name.className='mini-device-name';name.textContent=d.name;name.style.color=color;current.className='mini-current';label.className='mini-current-label';label.textContent=d.online&&!d.stale?'現在':'最終';number.className='mini-current-number '+f.primaryClass;number.textContent=f.primary;unit.className='mini-current-unit '+f.primaryClass;unit.textContent=f.primaryUnit;if(f.secondary!==null){const sep=document.createElement('span'),number2=document.createElement('span'),unit2=document.createElement('span');sep.textContent='/';number2.className='mini-current-number '+f.secondaryClass;number2.textContent=f.secondary;unit2.className='mini-current-unit '+f.secondaryClass;unit2.textContent=f.secondaryUnit;current.append(label,number,unit,sep,number2,unit2)}else current.append(label,number,unit);time.className='mini-current-time';const dt=d.timestamp?new Date(d.timestamp):null;time.textContent=!dt||Number.isNaN(dt.getTime())?'--:--':dt.toLocaleTimeString('ja-JP',{hour:'2-digit',minute:'2-digit'});state.className='mini-current-state';state.textContent=!d.online?'Offline':d.stale?'取得遅延':'';current.append(time,state);header.append(name,current);link.append(header,canvas);article.append(link);grid.append(article);drawMini(canvas,d)}catch(e){const article=document.createElement('article'),msg=document.createElement('div');article.className='mini offline';msg.textContent='取得失敗';article.append(msg);grid.append(article)}}}
function drawMini(c,d){const p=(d.primary||[]).map(x=>({t:new Date(x.t).getTime(),v:+x.v})).filter(x=>Number.isFinite(x.t)&&Number.isFinite(x.v)),s=(d.secondary||[]).map(x=>({t:new Date(x.t).getTime(),v:+x.v})).filter(x=>Number.isFinite(x.t)&&Number.isFinite(x.v)),r=c.getBoundingClientRect(),q=Math.max(1,devicePixelRatio||1);c.width=Math.round(r.width*q);c.height=Math.round(r.height*q);const x=c.getContext('2d');x.setTransform(q,0,0,q,0,0);x.fillStyle='#11161d';x.fillRect(0,0,r.width,r.height);if(!p.length&&!s.length){x.fillStyle='#bbb';x.textAlign='center';x.fillText('データなし',r.width/2,r.height/2);return}const now=new Date(),start=new Date(now.getFullYear(),now.getMonth(),now.getDate()).getTime(),end=start+86400000,L=38,T=8,B=18,W=r.width-L-6,H=r.height-T-B;x.strokeStyle='rgba(255,255,255,.14)';for(let i=0;i<=4;i++){let y=T+H*i/4;x.beginPath();x.moveTo(L,y);x.lineTo(r.width-6,y);x.stroke()}function axis(points,humidity){const values=points.map(z=>humidity?Math.max(0,Math.min(100,z.v)):z.v).filter(Number.isFinite);if(!values.length)return null;let min=Math.min(...values),max=Math.max(...values);if(max===min){const pad=humidity?2:Math.max(Math.abs(max)*.05,.5);min-=pad;max+=pad;if(humidity){min=Math.max(0,min);max=Math.min(100,max)}}return{min,max,humidity}}function line(points,color,dashed,a){if(!a)return;x.strokeStyle=color;x.lineWidth=2;x.setLineDash(dashed?[7,4]:[]);x.beginPath();let last=null;points.sort((a,b)=>a.t-b.t).forEach(z=>{const xx=L+W*(z.t-start)/(end-start),vv=a.humidity?Math.max(0,Math.min(100,z.v)):z.v,yy=T+H*(1-(vv-a.min)/(a.max-a.min));if(last===null||z.t-last>150000)x.moveTo(xx,yy);else x.lineTo(xx,yy);last=z.t});x.stroke();x.setLineDash([])}if(d.kind==='Environment'){line(p,'#00eaff',false,axis(p,false));line(s,'#ff42c8',true,axis(s,true))}else{const color=safeSeriesColor(d.colorHex);line(p,color,false,axis(p,d.kind==='Humidity'));line(s,color,true,axis(s,true))}}
async function updateDashboard(){try{const r=await fetch(BASE+'?partial='+Date.now(),{cache:'no-store'});if(!r.ok)return;const doc=new DOMParser().parseFromString(await r.text(),'text/html');document.querySelectorAll('.card[data-id]').forEach(old=>{const fresh=doc.querySelector('.card[data-id="'+CSS.escape(old.dataset.id)+'"]');if(!fresh)return;old.className=fresh.className;const oldName=old.querySelector('.name'),freshName=fresh.querySelector('.name');if(oldName&&freshName){oldName.textContent=freshName.textContent;oldName.style.color=freshName.style.color}['.val','.meta','.summary-current-card','.summary-sub'].forEach(sel=>{const a=old.querySelector(sel),b=fresh.querySelector(sel);if(a&&b)a.replaceWith(b.cloneNode(true))})});const u=doc.querySelector('.updated'),ou=document.querySelector('.updated');if(u&&ou)ou.innerHTML=u.innerHTML;['.summary-current','.summary-energy'].forEach(sel=>{const a=document.querySelector(sel),b=doc.querySelector(sel);if(a&&b)a.replaceWith(b.cloneNode(true))});const ids=loadSelected();syncChecks(ids);await renderMini(ids)}catch(e){console.debug(e)}}bindSelectorEvents();const initial=loadSelected();saveSelected(initial);syncChecks(initial);renderMini(initial);setInterval(updateDashboard,60000);
</script></body></html>
"""
            .Replace("__PAGE_TITLE__",pageTitle,StringComparison.Ordinal)
            .Replace("__MODE_TEXT__",management?"管理・操作可能":"閲覧専用",StringComparison.Ordinal)
            .Replace("__UPDATED__",updated,StringComparison.Ordinal)
            .Replace("__TOTAL_W__",totalW.ToString("0",System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal)
            .Replace("__TOTAL_W_CLASS__",ValueColorRules.Power(totalW),StringComparison.Ordinal)
            .Replace("__TOTAL_WH__",totalWh.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal)
            .Replace("__YEN__",yen.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal)
            .Replace("__SECTIONS__",sections,StringComparison.Ordinal)
            .Replace("__BASE_PATH__",basePath,StringComparison.Ordinal)
            .Replace("__SWITCH_SCRIPT__",switchScript,StringComparison.Ordinal);
        return Task.FromResult(html);
    }

    public void Dispose()
    {
        try{ _cts?.Cancel(); }catch{}
        foreach(var l in _listeners)
        {
            try{ l.Stop(); }catch{}
        }
        _listeners.Clear();
    }
}
