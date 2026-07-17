using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
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

    public LocalHttpService(Func<IReadOnlyList<DeviceSnapshot>> get,Func<string,bool,Task<bool>> power,Action<string>? openGraph=null,Func<double>? rate=null,Func<string,Task<List<HistoryPoint>>>? history=null)
    {
        _get=get;_power=power;_openGraph=openGraph;_rate=rate??(()=>30.0);_history=history;
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

        ActiveUrls.Add($"http://localhost:{port}");
        ActiveUrls.Add($"http://127.0.0.1:{port}");
        foreach(var ip in GetLocalIPv4()) ActiveUrls.Add($"http://{ip}:{port}");

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

            // ヘッダーは読み捨てる。Hostは検査しない。
            string? line;
            while(!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct))) { }

            string[] parts = requestLine.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length < 2 || !(parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("POST", StringComparison.OrdinalIgnoreCase)))
            {
                await Send(stream, "Method Not Allowed", "text/plain; charset=utf-8", 405, ct);
                return;
            }

            var url = parts[1];
            var qIndex = url.IndexOf('?');
            var path = qIndex >= 0 ? url[..qIndex] : url;
            var query = qIndex >= 0 ? ParseQuery(url[(qIndex+1)..]) : new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

            if(path=="/api/devices"||path=="/json") await Send(stream,JsonSerializer.Serialize(_get()),"application/json; charset=utf-8",200,ct);
            else if(path=="/api/toggle")
            {
                query.TryGetValue("id", out var id);
                var target=_get().FirstOrDefault(d=>d.Id.Equals(id??"",StringComparison.OrdinalIgnoreCase) || d.Name.Equals(id??"",StringComparison.OrdinalIgnoreCase));
                var ok=target is not null && target.Kind==DeviceKind.Switch && await _power(target.Id,target.IsOn!=true);
                await Send(stream,$"{{\"ok\":{ok.ToString().ToLowerInvariant()}}}","application/json; charset=utf-8",ok?200:404,ct);
            }
            else if(path=="/api/power")
            {
                query.TryGetValue("id", out var id);
                if(string.IsNullOrWhiteSpace(id)) query.TryGetValue("name", out id);
                query.TryGetValue("ip", out var ip);
                query.TryGetValue("state", out var stateText);
                id = DecodeQueryValue(id);
                ip = DecodeQueryValue(ip);
                var target = !string.IsNullOrWhiteSpace(ip) ? ip : id;
                var targetKind = !string.IsNullOrWhiteSpace(ip) ? "ip" : "id";
                var isOn=(stateText??"").Equals("on",StringComparison.OrdinalIgnoreCase);
                var isOff=(stateText??"").Equals("off",StringComparison.OrdinalIgnoreCase);
                if(string.IsNullOrWhiteSpace(target) || (!isOn && !isOff))
                {
                    await Send(stream,"{\"ok\":false,\"error\":\"id/name or ip and state=on|off are required\"}","application/json; charset=utf-8",400,ct);
                    return;
                }
                if(!string.IsNullOrWhiteSpace(ip) && !IPAddress.TryParse(ip, out _))
                {
                    await Send(stream,"{\"ok\":false,\"error\":\"invalid ip address\"}","application/json; charset=utf-8",400,ct);
                    return;
                }
                var ok=await _power(target!,isOn);
                await Send(stream,$"{{\"ok\":{ok.ToString().ToLowerInvariant()},\"{targetKind}\":{JsonSerializer.Serialize(target)},\"state\":\"{(isOn?"on":"off")}\"}}","application/json; charset=utf-8",ok?200:404,ct);
            }
            else if(path=="/api/history")
            {
                query.TryGetValue("id", out var id);
                var points=!string.IsNullOrWhiteSpace(id)&&_history is not null ? await _history(id!) : [];
                await Send(stream,JsonSerializer.Serialize(points),"application/json; charset=utf-8",200,ct);
            }
            else if(path=="/graph")
            {
                query.TryGetValue("id", out var id);
                var html=await BuildGraphHtml(id??"");
                await Send(stream,html,"text/html; charset=utf-8",200,ct);
            }
            else if(path=="/series")
            {
                query.TryGetValue("group", out var group);
                var html=await BuildSeriesHtml(group??"");
                await Send(stream,html,"text/html; charset=utf-8",200,ct);
            }
            else if(path=="/api/open-graph")
            {
                query.TryGetValue("id", out var id);
                var ok=!string.IsNullOrWhiteSpace(id)&&_openGraph is not null;
                if(ok)_openGraph!(id!);
                await Send(stream,$"{{\"ok\":{ok.ToString().ToLowerInvariant()}}}","application/json; charset=utf-8",ok?200:404,ct);
            }
            else if(path=="/health") await Send(stream,"ok","text/plain; charset=utf-8",200,ct);
            else await Send(stream,BuildHtml(_get()),"text/html; charset=utf-8",200,ct);
        }
        catch { }
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

    private static async Task Send(NetworkStream stream,string body,string type,int status,CancellationToken ct)
    {
        var bodyBytes=Encoding.UTF8.GetBytes(body);
        var reason=status switch{200=>"OK",400=>"Bad Request",404=>"Not Found",405=>"Method Not Allowed",500=>"Internal Server Error",_=>"OK"};
        var header=$"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bodyBytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        var headerBytes=Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes,ct);
        await stream.WriteAsync(bodyBytes,ct);
        await stream.FlushAsync(ct);
    }

    private async Task<string> BuildSeriesHtml(string group)
    {
        var isEnv=group.Equals("env",StringComparison.OrdinalIgnoreCase) || group.Equals("environment",StringComparison.OrdinalIgnoreCase);
        var isPower=group.Equals("power",StringComparison.OrdinalIgnoreCase);
        if(!isEnv && !isPower) return "<!doctype html><meta charset='utf-8'><body style='background:#0f1319;color:white;font-family:sans-serif'><h1>対象外です</h1></body>";
        var devices=_get().Where(d=>d.Kind!=DeviceKind.Hub && !d.IsPowerSummary).ToList();
        async Task<string> makeSeriesJson(IEnumerable<DeviceSnapshot> list,bool humidity=false)
        {
            var rows=new List<object>();
            foreach(var d in list)
            {
                var id=humidity ? d.Id+":metric:humidity" : d.Kind==DeviceKind.Power ? d.Id+":metric:power" : d.Id+":metric:temperature";
                var points=_history is not null ? await _history(id) : [];
                rows.Add(new{name=d.Name,points=points.Select(p=>new{t=p.Time.ToString("yyyy-MM-dd HH:mm:ss"),v=p.Value})});
            }
            return JsonSerializer.Serialize(rows);
        }
        string title=isPower?"電力系 - 系列グラフ":"温度・湿度系 - 系列グラフ";
        string data1=isPower
            ? await makeSeriesJson(devices.Where(d=>d.Kind==DeviceKind.Power))
            : await makeSeriesJson(devices.Where(d=>d.Kind==DeviceKind.Environment || d.Kind==DeviceKind.Temperature));
        string data2=isEnv
            ? await makeSeriesJson(devices.Where(d=>d.Kind==DeviceKind.Environment || d.Kind==DeviceKind.Humidity), humidity:true)
            : "[]";
        string unit1=isPower?"W":"℃"; string unit2=isEnv?"%":"";
        string secondBlock=isEnv?"<h2>湿度</h2><canvas id='chart2' width='1200' height='470'></canvas>":"";
        string drawSecond=isEnv?"draw('chart2',data2,unit2);":"";
        return "<!doctype html><html><head><meta charset='utf-8'><meta name=viewport content='width=device-width,initial-scale=1'><title>"+WebUtility.HtmlEncode(title)+"</title><style>"+
        ":root{color-scheme:dark}body{margin:0;background:#0f1319;color:#f4f6fb;font-family:'Yu Gothic UI',Meiryo,Segoe UI,sans-serif;padding:22px}h1{margin:0 0 14px;font-size:32px}h2{margin:18px 0 8px;color:#d6ddeb}.panel{background:#181d25;border:1px solid #626b78;border-radius:16px;padding:16px}canvas{width:100%;height:48vh;background:#15191f;border-radius:10px;display:block}.note{color:#c8ced8;margin-top:10px}"+
        "</style></head><body><h1>"+WebUtility.HtmlEncode(title)+"</h1><div class='panel'><h2>"+(isPower?"電力":"温度")+"</h2><canvas id='chart1' width='1200' height='470'></canvas>"+secondBlock+"<div class='note'>系列タイトルのダブルクリックから開いたブラウザ内グラフです。履歴は過去24時間です。</div></div><script>"+
        "const data1="+data1+";const data2="+data2+";const unit1="+JsonSerializer.Serialize(unit1)+";const unit2="+JsonSerializer.Serialize(unit2)+";"+
        "const colors=['#ff2bd6','#00d8ff','#ffd61f','#88ff9a','#ff914d','#b58cff','#00ffff','#ff777d','#beff48','#ffffff'];"+
        "function draw(id,series,unit){const c=document.getElementById(id),ctx=c.getContext('2d'),w=c.width,h=c.height,L=70,R=25,T=30,B=55;ctx.fillStyle='#15191f';ctx.fillRect(0,0,w,h);let all=[];series.forEach(s=>s.points.forEach(p=>all.push({t:new Date(p.t),v:Number(p.v)})));if(!all.length){ctx.fillStyle='#eee';ctx.font='28px sans-serif';ctx.textAlign='center';ctx.fillText('履歴データがありません',w/2,h/2);return;}let min=Math.min(0,...all.map(p=>p.v)),max=Math.max(...all.map(p=>p.v));if(Math.abs(max-min)<.001)max=min+1;let pad=(max-min)*.08;max+=pad;min=Math.max(0,min-pad);let start=Math.min(...all.map(p=>p.t.getTime())),end=Math.max(...all.map(p=>p.t.getTime())),span=Math.max(1,end-start);ctx.strokeStyle='rgba(255,255,255,.16)';ctx.lineWidth=1;for(let i=0;i<=5;i++){let y=T+(h-T-B)*i/5;ctx.beginPath();ctx.moveTo(L,y);ctx.lineTo(w-R,y);ctx.stroke();let v=max-(max-min)*i/5;ctx.fillStyle='#cdd3dd';ctx.font='18px sans-serif';ctx.textAlign='right';ctx.fillText(v.toFixed(1),L-10,y+6);}for(let i=0;i<=6;i++){let x=L+(w-L-R)*i/6;ctx.beginPath();ctx.moveTo(x,T);ctx.lineTo(x,h-B);ctx.stroke();}series.forEach((s,si)=>{ctx.strokeStyle=colors[si%colors.length];ctx.lineWidth=3;ctx.beginPath();let first=true;s.points.forEach(p=>{let t=new Date(p.t).getTime(),v=Number(p.v);let x=L+(w-L-R)*(t-start)/span,y=T+(h-T-B)*(1-(v-min)/(max-min));if(first){ctx.moveTo(x,y);first=false}else ctx.lineTo(x,y);});ctx.stroke();let lx=L+10+(si%4)*180,ly=T+10+Math.floor(si/4)*24;ctx.fillStyle=colors[si%colors.length];ctx.fillRect(lx,ly,24,4);ctx.font='14px sans-serif';ctx.textAlign='left';ctx.fillText(s.name,lx+32,ly+8);});ctx.strokeStyle='#888';ctx.strokeRect(L,T,w-L-R,h-T-B);ctx.fillStyle='#e6ebf4';ctx.font='20px sans-serif';ctx.textAlign='center';ctx.fillText(unit,w/2,h-15);}"+
        "draw('chart1',data1,unit1);"+drawSecond+"setTimeout(()=>location.reload(),60000);</script></body></html>";
    }

    private async Task<string> BuildGraphHtml(string id)
    {
        var device=_get().FirstOrDefault(d=>d.Id.Equals(id,StringComparison.OrdinalIgnoreCase) || d.Name.Equals(id,StringComparison.OrdinalIgnoreCase));
        if(device is null) return "<!doctype html><meta charset='utf-8'><body style='background:#0f1319;color:white;font-family:sans-serif'><h1>Device not found</h1></body>";
        string H(string x)=>WebUtility.HtmlEncode(x??"");

        var primaryMetric=device.Kind==DeviceKind.Power?"power":device.Kind==DeviceKind.Humidity?"humidity":"temperature";
        var primaryId=device.Id+":metric:"+primaryMetric;
        var primaryPoints=_history is not null ? await _history(primaryId) : [];
        var humidityPoints=device.Kind==DeviceKind.Environment && _history is not null ? await _history(device.Id+":metric:humidity") : [];
        var primaryJson=JsonSerializer.Serialize(primaryPoints.Select(p=>new{t=p.Time.ToString("O"),v=p.Value}));
        var humidityJson=JsonSerializer.Serialize(humidityPoints.Select(p=>new{t=p.Time.ToString("O"),v=p.Value}));
        var primaryUnit=device.Kind switch{DeviceKind.Power=>"W",DeviceKind.Humidity=>"%",_=>"℃"};
        var dual=device.Kind==DeviceKind.Environment;
        var title=H(device.Name);
        var current=device.Kind switch
        {
            DeviceKind.Power=>$"{device.PowerWatts??0:0.0} W",
            DeviceKind.Environment=>$"{device.TemperatureC??0:0.0} ℃ / {device.HumidityPercent??0:0}%",
            DeviceKind.Temperature=>$"{device.TemperatureC??0:0.0} ℃",
            DeviceKind.Humidity=>$"{device.HumidityPercent??0:0}%",
            _=>device.CurrentValue
        };
        var sb=new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'><meta name=viewport content='width=device-width,initial-scale=1'>");
        sb.AppendLine("<title>"+title+" - Graph</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(":root{color-scheme:dark}body{margin:0;background:#0f1319;color:#f4f6fb;font-family:'Yu Gothic UI',Meiryo,Segoe UI,sans-serif;padding:22px}");
        sb.AppendLine(".header{display:flex;align-items:baseline;justify-content:space-between;gap:16px;margin-bottom:16px}h1{font-size:30px;margin:0}.now{font-size:22px;color:#b58cff;font-weight:800}");
        sb.AppendLine(".panel{background:#181d25;border:1px solid #636b78;border-radius:16px;padding:16px}canvas{width:100%;height:60vh;display:block;background:#15191f;border-radius:10px}");
        sb.AppendLine(".legend{display:flex;gap:20px;justify-content:center;margin-top:8px;font-weight:700}.temp{color:#00eaff}.hum{color:#ff42c8}");
        sb.AppendLine(".stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:10px;margin:14px 0}.stat{background:#232a34;border-radius:10px;padding:10px;text-align:center}.stat b{font-size:22px;color:#88f69d}");
        sb.AppendLine(".note{color:#c8ced8;margin-top:10px}");
        sb.AppendLine("</style></head><body>");
        var currentTime=(primaryPoints.Concat(humidityPoints).OrderBy(p=>p.Time).LastOrDefault()?.Time ?? DateTime.Now).ToString("HH:mm");
        sb.AppendLine("<div class='header'><h1>"+title+"</h1><div class='now'>現在 "+H(current)+" <span style='color:#d6dae0;font-size:.82em;margin-left:12px'>"+currentTime+"</span></div></div>");
        sb.AppendLine("<div class='panel'><canvas id='chart' width='1400' height='650'></canvas>");
        if(dual)sb.AppendLine("<div class='legend'><span class='temp'>■ 温度（左軸）</span><span class='hum'>■ 湿度（右軸）</span></div>");
        sb.AppendLine("<div class='stats' id='stats'></div>");
        sb.AppendLine("<div class='note'>実際の取得時刻をX軸に使用して、過去24時間の履歴を表示しています。未取得時間は空白時間として反映されます。</div></div>");
        sb.AppendLine("<script>");
        sb.AppendLine("const primary="+primaryJson+".map(p=>({t:new Date(p.t),v:Number(p.v)})).filter(p=>Number.isFinite(p.t.getTime())&&Number.isFinite(p.v));");
        sb.AppendLine("const humidity="+humidityJson+".map(p=>({t:new Date(p.t),v:Number(p.v)})).filter(p=>Number.isFinite(p.t.getTime())&&Number.isFinite(p.v));");
        sb.AppendLine("const primaryUnit="+JsonSerializer.Serialize(primaryUnit)+";const dual="+dual.ToString().ToLowerInvariant()+";");
        sb.AppendLine("const canvas=document.getElementById('chart'),ctx=canvas.getContext('2d');");
        sb.AppendLine(@"function bounds(values,unit){if(!values.length)return {min:0,max:1};let min=Math.min(...values),max=Math.max(...values);if(unit==='%'){min=Math.max(0,Math.floor((min-5)/5)*5);max=Math.min(100,Math.ceil((max+5)/5)*5);}else if(unit==='℃'){min=Math.floor(min-2);max=Math.ceil(max+2);}else{min=Math.max(0,Math.floor(min-(max-min)*.08));max=Math.ceil(max+(max-min)*.08);}if(max<=min)max=min+1;return {min,max};}");
        sb.AppendLine(@"function fmtTime(d,span){if(span>20*3600000)return d.toLocaleString('ja-JP',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit'});return d.toLocaleTimeString('ja-JP',{hour:'2-digit',minute:'2-digit'});}");
        sb.AppendLine(@"function draw(){const w=canvas.width,h=canvas.height,L=86,R=dual?86:28,T=34,B=72;ctx.fillStyle='#15191f';ctx.fillRect(0,0,w,h);const all=primary.concat(humidity);if(!all.length){ctx.fillStyle='#eee';ctx.font='28px sans-serif';ctx.textAlign='center';ctx.fillText('履歴データがありません',w/2,h/2);return;}let start=Math.min(...all.map(p=>p.t.getTime())),end=Math.max(...all.map(p=>p.t.getTime()));if(end<=start)end=start+60000;const timeSpan=end-start,pb=bounds(primary.map(p=>p.v),primaryUnit),hb=bounds(humidity.map(p=>p.v),'%');const x=t=>L+(w-L-R)*(t-start)/timeSpan,yp=v=>T+(h-T-B)*(1-(v-pb.min)/(pb.max-pb.min)),yh=v=>T+(h-T-B)*(1-(v-hb.min)/(hb.max-hb.min));ctx.strokeStyle='rgba(255,255,255,.14)';ctx.lineWidth=1;for(let i=0;i<=5;i++){let y=T+(h-T-B)*i/5;ctx.beginPath();ctx.moveTo(L,y);ctx.lineTo(w-R,y);ctx.stroke();ctx.font='17px sans-serif';ctx.fillStyle='#cdd3dd';ctx.textAlign='right';ctx.fillText((pb.max-(pb.max-pb.min)*i/5).toFixed(primaryUnit==='W'?0:1),L-10,y+6);if(dual){ctx.fillStyle='#ff8ddd';ctx.textAlign='left';ctx.fillText((hb.max-(hb.max-hb.min)*i/5).toFixed(0),w-R+10,y+6);}}for(let i=0;i<=6;i++){let xx=L+(w-L-R)*i/6;ctx.beginPath();ctx.moveTo(xx,T);ctx.lineTo(xx,h-B);ctx.stroke();let d=new Date(start+timeSpan*i/6);ctx.save();ctx.translate(xx,h-B+24);ctx.rotate(-.35);ctx.fillStyle='#cdd3dd';ctx.font='16px sans-serif';ctx.textAlign='right';ctx.fillText(fmtTime(d,timeSpan),0,0);ctx.restore();}function line(data,color,yf){if(!data.length)return;ctx.strokeStyle=color;ctx.lineWidth=4;ctx.beginPath();data.forEach((p,i)=>{let xx=x(p.t.getTime()),yy=yf(p.v);i?ctx.lineTo(xx,yy):ctx.moveTo(xx,yy)});ctx.stroke();}if(dual)line(humidity,'#ff2bbd',yh);line(primary,'#00eaff',yp);if(primary.length){const p=primary.at(-1);ctx.beginPath();ctx.fillStyle='#00eaff';ctx.arc(x(p.t.getTime()),yp(p.v),6,0,Math.PI*2);ctx.fill();}if(dual&&humidity.length){const p=humidity.at(-1);ctx.beginPath();ctx.fillStyle='#ff2bbd';ctx.arc(x(p.t.getTime()),yh(p.v),5,0,Math.PI*2);ctx.fill();}ctx.strokeStyle='#8b94a3';ctx.lineWidth=2;ctx.strokeRect(L,T,w-L-R,h-T-B);ctx.font='20px sans-serif';ctx.textAlign='center';ctx.fillStyle='#00eaff';ctx.fillText(primaryUnit,L-50,T-8);if(dual){ctx.fillStyle='#ff42c8';ctx.fillText('%',w-R+50,T-8);}const vals=primary.map(p=>p.v),avg=vals.length?vals.reduce((a,b)=>a+b,0)/vals.length:0;let cards=[['現在',vals.at(-1)??0,primaryUnit],['最小',vals.length?Math.min(...vals):0,primaryUnit],['最大',vals.length?Math.max(...vals):0,primaryUnit],['平均',avg,primaryUnit]];if(dual&&humidity.length){const hv=humidity.map(p=>p.v),ha=hv.reduce((a,b)=>a+b,0)/hv.length;cards.push(['現在湿度',hv.at(-1),'%'],['平均湿度',ha,'%']);}document.getElementById('stats').innerHTML=cards.map(c=>'<div class=stat>'+c[0]+'<br><b>'+Number(c[1]).toFixed(c[2]==='W'?1:c[2]==='%'?0:1)+' '+c[2]+'</b></div>').join('');}");
        sb.AppendLine("draw();");
        sb.AppendLine("setTimeout(()=>location.reload(),60000);");
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }

    private string BuildHtml(IEnumerable<DeviceSnapshot> devices)
    {
        var ds=devices.Where(d=>d.Kind!=DeviceKind.Hub).ToList();
        var powerForTotals=ds.Where(d=>d.Kind==DeviceKind.Power && !d.IsPowerSummary && d.Online && d.PowerWatts is >=0 and <=3000).ToList();
        var rate=_rate();
        string H(string s)=>WebUtility.HtmlEncode(s??"");
        double totalW=powerForTotals.Sum(d=>d.PowerWatts??0);
        double totalWh=powerForTotals.Sum(d=>d.TodayWh??0);
        double yen=totalWh/1000.0*rate;
        string value(DeviceSnapshot d)=>d.Kind switch
        {
            DeviceKind.Power => $"{d.PowerWatts??0:0} W",
            DeviceKind.Environment => $"{d.TemperatureC??0:0.0} ℃ / {d.HumidityPercent??0:0}%",
            DeviceKind.Temperature => $"{d.TemperatureC??0:0.0} ℃",
            DeviceKind.Humidity => $"{d.HumidityPercent??0:0}%",
            DeviceKind.Switch => d.IsOn==true?"ON":"OFF",
            _=>"--"
        };
        string groupCss(DeviceGroupKind g)=>g switch{DeviceGroupKind.Power=>"power",DeviceGroupKind.Environment=>"env",_=>"sw"};
        string cls(DeviceSnapshot d)=>groupCss(d.GroupKind);
        string tempClass(double? value)
        {
            var v=value??0;
            if(v<=0) return "white";
            if(v>=35) return "deepmagenta";
            if(v>=30) return "red";
            if(v>=25) return "yellow";
            if(v>=20) return "green";
            if(v>=15) return "lightblue";
            return "darkblue";
        }
        string humidityClass(double? value)
        {
            var v=value??0;
            if(v>=80) return "red";
            if(v>=60) return "yellow";
            if(v>=40) return "green";
            if(v>=20) return "lightblue";
            return "darkblue";
        }
        string powerClass(double? value)
        {
            var v=value??0;
            if(v>=1000) return "purple";
            if(v>=800) return "red";
            if(v>=400) return "yellow";
            if(v>=100) return "lime";
            return "green";
        }
        string valueClass(DeviceSnapshot d)
        {
            if(d.Kind==DeviceKind.Power || d.IsPowerSummary) return powerClass(d.PowerWatts);
            if(d.Kind==DeviceKind.Environment || d.Kind==DeviceKind.Temperature) return tempClass(d.TemperatureC);
            if(d.Kind==DeviceKind.Humidity) return humidityClass(d.HumidityPercent);
            if(d.Kind==DeviceKind.Switch) return d.IsOn==true ? "green" : "off";
            return "";
        }
        string envValue(DeviceSnapshot d)
        {
            var t=d.TemperatureC??0;
            var h=d.HumidityPercent??0;
            return $"<span class='{tempClass(t)}'>{t:0.0}<small>℃</small></span><span class='{humidityClass(h)}'>{h:0}<small>%</small></span>";
        }
        string card(DeviceSnapshot d)
        {
            var canGraph=d.IsPowerSummary||d.Kind is DeviceKind.Power or DeviceKind.Environment or DeviceKind.Temperature or DeviceKind.Humidity;
            var on=d.IsOn==true?"on":d.IsOn==false?"off":"";
            var dbl=canGraph?$" onclick=\"openGraph('{H(d.Id)}')\" title='クリックでグラフを開く'":"";
            if(d.Kind==DeviceKind.Switch) dbl=$" ondblclick=\"toggleSwitch('{H(d.Id)}')\" title='ダブルクリックでON/OFF'";
            var ip=string.IsNullOrWhiteSpace(d.Ip)?d.Hub:d.Ip;
            var today=d.Kind==DeviceKind.Power&&d.TodayWh is not null?$"<span>TODAY <b>{d.TodayWh:0.0} Wh</b></span>":"";
            var cost=d.Kind==DeviceKind.Power&&!d.IsPowerSummary&&d.TodayWh is not null?$"<span>COST <b>{(d.TodayWh/1000.0*rate):0.0} yen</b></span>":"";
            if(d.IsPowerSummary)
            {
                return $"<article class='card power summary-card' data-kind='{d.Kind}'{dbl}><div class='name'>{H(d.Name)}</div><div class='summary-lines'><div><span>使用電力</span><b class='{powerClass(d.PowerWatts)}'>{d.PowerWatts??0:0} W</b></div><div><span>本日消費</span><b>{d.TodayWh??0:0.0} Wh</b></div><div><span>概算</span><b>{d.MonthWh??0:0.0} yen</b></div></div></article>";
            }
            var val=d.Kind==DeviceKind.Environment ? envValue(d) : $"<span class='{valueClass(d)}'>{H(value(d))}</span>";
            return $"<article class='card {cls(d)} {on}' data-kind='{d.Kind}'{dbl}><div class='name'>{H(d.Name)}</div><div class='val'>{val}</div><div class='meta'><span>{H(ip)}</span><span>{(d.Online?"online":"offline")}</span>{today}{cost}</div></article>";
        }
        string section(string title,DeviceGroupKind g)
        {
            var body=string.Join("",ds.Where(d=>d.GroupKind==g).Select(card));
            var graphAttr=g==DeviceGroupKind.Switch?"":$" ondblclick=\"event.stopPropagation();openSeries('{groupCss(g)}')\" title='ダブルクリックで系列グラフを表示'";
            return $"<section class='section {groupCss(g)} collapsed'><div class='section-title' onclick='toggleSection(this)'{graphAttr}>{title}<span>{ds.Count(d=>d.GroupKind==g)} devices</span></div><div class='cards'>{body}</div></section>";
        }
        var updated=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return """
<!doctype html><html><head><meta charset='utf-8'><meta name=viewport content='width=device-width,initial-scale=1'>
<title>TapoCtrl</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;background:#0f1319;color:#f4f6fb;font-family:'Yu Gothic UI',Meiryo,Segoe UI,sans-serif;padding:22px}
h1{font-size:44px;margin:0}.top{display:flex;justify-content:space-between;align-items:baseline;gap:24px;flex-wrap:wrap;margin-bottom:18px}.updated{color:#c7cbd4;text-align:right;font-size:20px}.updated b{color:#b58cff}
.summary{border:1px solid #6d7280;border-radius:14px;padding:14px 18px;margin:14px 0;background:#141922;font-size:20px;text-align:center}.summary b{font-size:24px}.summary .w{color:#ff4d57}.summary .wh,.summary .yen{color:#b58cff}.summary-top{display:grid;gap:8px;justify-items:center}.summary-card{text-align:center}.summary-card .name{text-align:center}.summary-card .summary-lines b{font-family:Impact,'Arial Narrow',sans-serif;font-weight:400}.summary-lines{display:grid;gap:10px;justify-items:center;margin-top:10px}.summary-lines div{display:grid;gap:2px}.summary-lines span{color:#d7ddeb;font-size:17px}.summary-lines b{font-size:30px;color:#b58cff}
.section{border:2px solid #5d6573;border-radius:16px;margin:16px 0;padding:10px 12px 14px;background:#181d25}.section-title{font-size:24px;font-weight:900;margin:0;padding:12px 18px;border-radius:12px;color:#10151b;cursor:pointer;user-select:none}.section-title span{font-size:16px;font-weight:600;margin-left:8px;opacity:.8}
.power{border-color:#e9d878}.power .section-title{background:#e9d878}.env{border-color:#caa3ee}.env .section-title{background:#caa3ee}.sw{border-color:#8fd8a6}.sw .section-title{background:#8fd8a6}
.section.collapsed .cards{display:none}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:14px;margin-top:14px}
.card{background:#232a34;border:1.5px solid #737b89;border-radius:14px;padding:16px 18px;min-height:132px;cursor:default}.card[title]{cursor:pointer}.card.power{border-color:#e9d878}.card.env{border-color:#caa3ee}.card.sw{border-color:#8fd8a6}
.name{font-weight:800;font-size:22px;color:#d9e4f4}.val{font-size:48px;line-height:1.15;margin:10px 0;font-weight:800;display:flex;gap:36px;align-items:baseline}.card.env .val,.card.power .val{font-family:Impact,'Arial Narrow',sans-serif;font-weight:400}.val small{font-size:.48em;margin-left:2px}.purple{color:#8c5cff}.deepmagenta{color:#c2185b}.red{color:#ff4b3c}.yellow{color:#ffd61f}.lime{color:#beff48}.green{color:#24ff60}.lightblue{color:#5cd2ff}.darkblue{color:#2060ff}.white{color:#ffffff}.off{color:#ff777d}.sw.on .val{color:#72ff9e}.sw.off .val{color:#ff777d}
.meta{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:4px 14px;color:#d0d5dd;font-size:15px}.meta b{color:#fff}
button{background:#222a35;color:#fff;border:1px solid #7a8292;border-radius:12px;padding:10px 18px;font-weight:700;font-size:18px}a{color:#75eaff}
</style>
<script>
function toggleSection(el){el.parentElement.classList.toggle('collapsed');}
function openSeries(group){window.open('/series?group='+encodeURIComponent(group),'_blank');}
function openGraph(id){window.open('/graph?id='+encodeURIComponent(id),'_blank');}
async function toggleSwitch(id){try{let el=event?.currentTarget;if(el){let v=el.querySelector('.val');let on=el.classList.contains('on');el.classList.toggle('on',!on);el.classList.toggle('off',on);if(v)v.textContent=on?'OFF':'ON';}await fetch('/api/toggle?id='+encodeURIComponent(id),{cache:'no-store'});setTimeout(()=>location.reload(),500);}catch(e){alert('ON/OFFできません: '+e);}}
async function updateDashboard(){try{const r=await fetch('/?partial='+Date.now(),{cache:'no-store'});if(!r.ok)return;const text=await r.text();const doc=new DOMParser().parseFromString(text,'text/html');const newSummary=doc.querySelector('.summary');const oldSummary=document.querySelector('.summary');if(newSummary&&oldSummary)oldSummary.innerHTML=newSummary.innerHTML;const newUpdated=doc.querySelector('.updated');const oldUpdated=document.querySelector('.updated');if(newUpdated&&oldUpdated)oldUpdated.innerHTML=newUpdated.innerHTML;document.querySelectorAll('.section').forEach((oldSec,i)=>{const newSec=doc.querySelectorAll('.section')[i];if(!newSec)return;const oldCards=oldSec.querySelector('.cards'),newCards=newSec.querySelector('.cards');const oldTitle=oldSec.querySelector('.section-title span'),newTitle=newSec.querySelector('.section-title span');if(oldCards&&newCards)oldCards.innerHTML=newCards.innerHTML;if(oldTitle&&newTitle)oldTitle.textContent=newTitle.textContent;});}catch(e){console.debug('dashboard update failed',e);}}
setInterval(updateDashboard,60000);
</script></head><body>
"""+$"<div class='top'><h1>TapoCtrl</h1><span class='updated'>updated: <b>{updated}</b></span></div>"+
$"<div class='summary summary-top'><div>使用電力 <b><span class='w'>{totalW:0} W</span></b></div><div>本日消費 <b><span class='wh'>{totalWh:0.0} Wh</span></b></div><div>概算 <b><span class='yen'>{yen:0.0} yen</span></b></div></div>"+
section("電力系",DeviceGroupKind.Power)+section("温度・湿度系",DeviceGroupKind.Environment)+section("スイッチ系",DeviceGroupKind.Switch)+"</body></html>";
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
