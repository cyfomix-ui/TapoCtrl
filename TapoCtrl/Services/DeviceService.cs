using TapoCtrl.Models;
namespace TapoCtrl.Services;
public interface ITapoTransport
{
    event Action<string>? StatusChanged;
    Task<IReadOnlyList<DeviceSnapshot>> RefreshValuesAsync(IReadOnlyList<DeviceSnapshot> known,CancellationToken ct);
    Task<IReadOnlyList<DeviceSnapshot>> RefreshMetadataAsync(IReadOnlyList<DeviceSnapshot> known,CancellationToken ct);
    Task<bool> SetPowerAsync(DeviceSnapshot device,bool on,CancellationToken ct);
}
public sealed class DemoTransport : ITapoTransport
{
    private readonly Random _r=new();
    public event Action<string>? StatusChanged;
    public Task<IReadOnlyList<DeviceSnapshot>> RefreshMetadataAsync(IReadOnlyList<DeviceSnapshot> known,CancellationToken ct)
    {
        StatusChanged?.Invoke("デモデバイスを準備しています…");
        IReadOnlyList<DeviceSnapshot> list=known.Count>0?known:[
            new(){Id="power-1",Name="Desk Power",Ip="192.168.2.109",Kind=DeviceKind.Power,PowerWatts=42.1,IsOn=true},
            new(){Id="temp-1",Name="Temp 1F Room",Hub="192.168.2.187",Kind=DeviceKind.Temperature,TemperatureC=24.8},
            new(){Id="humid-1",Name="Humidity 1F",Hub="192.168.2.187",Kind=DeviceKind.Humidity,HumidityPercent=52}
        ]; return Task.FromResult(list);
    }
    public Task<IReadOnlyList<DeviceSnapshot>> RefreshValuesAsync(IReadOnlyList<DeviceSnapshot> known,CancellationToken ct)
    {
        StatusChanged?.Invoke("デモデバイスの現在値を更新しています…");
        foreach(var d in known){d.Timestamp=DateTime.Now;if(d.Kind==DeviceKind.Power)d.PowerWatts=Math.Max(0,(d.PowerWatts??40)+_r.NextDouble()*4-2);if(d.Kind==DeviceKind.Temperature)d.TemperatureC=(d.TemperatureC??24)+_r.NextDouble()*.3-.15;if(d.Kind==DeviceKind.Humidity)d.HumidityPercent=(d.HumidityPercent??50)+_r.NextDouble()-.5;} return Task.FromResult(known);
    }
    public Task<bool> SetPowerAsync(DeviceSnapshot d,bool on,CancellationToken ct){d.IsOn=on;if(!on)d.PowerWatts=0;return Task.FromResult(true);}
}
public sealed class DeviceCoordinator : IDisposable
{
    private readonly ITapoTransport _transport; private readonly HistoryService _history; private readonly SemaphoreSlim _gate=new(1,1); private CancellationTokenSource? _cts;
    public event Action<IReadOnlyList<DeviceSnapshot>>? Updated;
    public event Action<string,bool>? StatusChanged;
    public List<DeviceSnapshot> Devices {get;}=[];
    public DeviceCoordinator(ITapoTransport transport,HistoryService history)
    {
        (_transport,_history)=(transport,history);
        _transport.StatusChanged += text => StatusChanged?.Invoke(text,true);
    }
    public async Task StartAsync(IEnumerable<DeviceSnapshot> seed,int valueSec,int metadataMin)
    {
        Devices.Clear();Devices.AddRange(seed);_cts=new();
        // 起動直後は保存済みの最終スナップショットを先に表示する。
        // 初回探索が失敗しても画面とミニパネルを空にしない。
        if(Devices.Count>0) Updated?.Invoke(Devices);
        StatusChanged?.Invoke("Tapoデバイスを探索しています…",true);
        try
        {
            await RefreshMetadataAsync(_cts.Token);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke("初回探索に失敗しました: "+ex.Message+" / 保存済み情報を表示したまま自動復旧を継続します。",false);
        }
        _=LoopAsync(TimeSpan.FromSeconds(Math.Max(10,valueSec)),false,_cts.Token);
        _=LoopAsync(TimeSpan.FromMinutes(Math.Max(1,metadataMin)),true,_cts.Token);
    }
    private async Task LoopAsync(TimeSpan interval,bool metadata,CancellationToken ct)
    {
        using var timer=new PeriodicTimer(interval);
        try
        {
            while(await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if(metadata) await RefreshMetadataAsync(ct); else await RefreshValuesAsync(ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
                catch(Exception ex)
                {
                    StatusChanged?.Invoke("定期更新に失敗しました: "+ex.Message+" / 次回周期にも自動復旧を試みます。",false);
                }
            }
        }
        catch(OperationCanceledException){ }
    }
    public async Task RefreshValuesAsync(CancellationToken ct=default)
    {
        if(!await _gate.WaitAsync(0,ct)){StatusChanged?.Invoke("別の更新処理が実行中です。",true);return;}
        try
        {
            StatusChanged?.Invoke("現在値を取得しています…",true);
            var list=await _transport.RefreshValuesAsync(Devices,ct);
            if(list.Count==0 && Devices.Count>0)
                throw new InvalidOperationException("監視結果が0件でした。最後に正常取得した情報を保持して復旧を待ちます。");
            Replace(list);
            foreach(var d in Devices)await _history.AppendAsync(d);
            Updated?.Invoke(Devices);
            StatusChanged?.Invoke($"現在値を更新しました / {Devices.Count} devices",false);
        }
        catch(Exception ex){StatusChanged?.Invoke("現在値の取得に失敗しました: "+ex.Message,false);throw;}
        finally{_gate.Release();}
    }
    public async Task RefreshMetadataAsync(CancellationToken ct=default)
    {
        if(!await _gate.WaitAsync(0,ct)){StatusChanged?.Invoke("別の探索処理が実行中です。",true);return;}
        try
        {
            StatusChanged?.Invoke("LAN内のTapoデバイスとHUBを探索しています…",true);
            var list=await _transport.RefreshMetadataAsync(Devices,ct);
            if(list.Count==0 && Devices.Count>0)
                throw new InvalidOperationException("探索結果が0件でした。登録済みデバイス情報を保持して復旧を待ちます。");
            Replace(list);
            Updated?.Invoke(Devices);
            StatusChanged?.Invoke(Devices.Count>0?$"探索完了 / {Devices.Count} devices":"探索は完了しましたが、デバイスが見つかりませんでした。設定とHUB IPを確認してください。",false);
        }
        catch(Exception ex){StatusChanged?.Invoke("デバイス探索に失敗しました: "+ex.Message,false);throw;}
        finally{_gate.Release();}
    }
    public Task<bool> SetPowerAsync(DeviceSnapshot d,bool on,CancellationToken ct=default)=>_transport.SetPowerAsync(d,on,ct);
    private void Replace(IEnumerable<DeviceSnapshot> list){Devices.Clear();Devices.AddRange(list);}
    public void Dispose(){_cts?.Cancel();_cts?.Dispose();_gate.Dispose();}
}
