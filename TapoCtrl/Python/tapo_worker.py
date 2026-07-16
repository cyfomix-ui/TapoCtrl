import asyncio, json, sys, base64
from typing import Any

try:
    from kasa import Discover
    from tapo import ApiClient
except Exception as e:
    print(json.dumps({"ok":False,"error":"Python dependency missing: pip install python-kasa tapo", "detail":str(e)}), flush=True)
    sys.exit(2)

handlers = {}
hubs = {}
sensors = {}
creds = ("", "")

def dct(o):
    if isinstance(o, dict): return o
    for n in ("model_dump","dict","to_dict","as_dict"):
        try:
            f=getattr(o,n,None)
            if callable(f):
                x=f()
                if isinstance(x,dict): return x
        except Exception: pass
    try: return dict(getattr(o,"__dict__",{}) or {})
    except Exception: return {}

def val(d,*keys,default=None):
    for k in keys:
        if k in d and d[k] is not None: return d[k]
    return default

def dec_name(s):
    s=str(s or '').strip()
    try:
        if len(s)%4==0:
            x=base64.b64decode(s,validate=True).decode('utf-8').strip()
            if x: return x
    except Exception: pass
    return s

def normalize_watts(value):
    """tapo/python-kasaの版差（W/mW、数値/DTO）を吸収する。"""
    if value is None: return None
    if isinstance(value, bool): return None
    if isinstance(value, (int,float)):
        n=float(value)
    else:
        data=dct(value)
        n=None
        # 明示的にmWと分かるキーを先に見る。
        for key in ('current_power_mw','power_mw'):
            if key in data and data[key] is not None:
                try:
                    mw=float(data[key])
                    return mw/1000.0 if 0 <= mw <= 5000000 else None
                except Exception: pass
        for key in ('current_power','currentPower','power','power_w','current_power_w','current_consumption','value'):
            if key in data and data[key] is not None:
                try: n=float(data[key]); break
                except Exception: pass
        if n is None: return None
    # 3000W超は家庭用Tapoとして異常値扱い。mWらしい大値だけWへ補正する。
    if n < 0: return None
    if n > 5000:
        n=n/1000.0
    if n > 3000:
        return None
    return n

def explicit_power_from_dict(data):
    """energy_usage等のDTOから、現在電力だと明示されているキーだけを拾う。"""
    data=dct(data)
    for key in ('current_power_mw','power_mw'):
        if key in data and data[key] is not None:
            try:
                mw=float(data[key])
                return mw/1000.0 if 0 <= mw <= 5000000 else None
            except Exception: pass
    for key in ('current_power','currentPower','current_power_w','power_w','power'):
        if key in data and data[key] is not None:
            try:
                return normalize_watts(float(data[key]))
            except Exception: pass
    return None

async def read_power(handler, info=None):
    """電力取得を最優先で試し、成功したら必ずPower系に分類できるようにする。"""
    for method_name in ('get_current_power','get_emeter_realtime'):
        try:
            method=getattr(handler,method_name,None)
            if callable(method):
                p=normalize_watts(await method())
                if p is not None: return p
        except Exception: pass
    # get_energy_usage は本日消費量/累計値を含むため、汎用 value を現在Wとして扱わない。
    try:
        method=getattr(handler,'get_energy_usage',None)
        if callable(method):
            p=explicit_power_from_dict(await method())
            if p is not None: return p
    except Exception: pass
    # python-kasaのプロパティ/feature/moduleから取得
    for attr in ('emeter_realtime','energy','current_power','current_consumption'):
        try:
            raw=getattr(handler,attr,None)
            if callable(raw): raw=raw()
            if attr in ('emeter_realtime','energy'):
                p=explicit_power_from_dict(raw)
            else:
                p=normalize_watts(raw)
            if p is not None: return p
        except Exception: pass
    try:
        features=getattr(handler,'features',{}) or {}
        for key in ('current_consumption','current_power','power'):
            feature=features.get(key) if hasattr(features,'get') else None
            p=normalize_watts(getattr(feature,'value',feature))
            if p is not None: return p
    except Exception: pass
    p=explicit_power_from_dict(info)
    return p


def normalize_wh(value):
    if value is None or isinstance(value,bool): return None
    try:
        n=float(value)
        # 一部APIはkWh、別の版はWhを返す。小数で家庭用途として小さい値はkWhとみなす。
        return n*1000.0 if 0 <= n < 50 and abs(n-round(n)) > 1e-9 else n
    except Exception: return None

async def read_energy(handler):
    try:
        method=getattr(handler,'get_energy_usage',None)
        if not callable(method): return (None,None,None)
        raw=await method(); data=dct(raw)
        # energy_usage内の汎用 value / today値を現在Wとして誤読しない。
        power=explicit_power_from_dict(data)
        today=None; month=None
        for key in ('today_energy','todayEnergy','today_wh','today_energy_wh','today_usage'):
            if key in data and data[key] is not None: today=normalize_wh(data[key]); break
        for key in ('month_energy','monthEnergy','month_wh','month_energy_wh','month_usage'):
            if key in data and data[key] is not None: month=normalize_wh(data[key]); break
        return (power,today,month)
    except Exception: return (None,None,None)


def normalize_temp_c(value):
    if value is None or isinstance(value, bool): return None
    data=dct(value) if not isinstance(value,(int,float,str)) else {}
    if data:
        for key in ('current_temp','current_temperature','temperature','temp_celsius','temp','value'):
            if key in data and data[key] is not None:
                return normalize_temp_c(data[key])
    try:
        n=float(value)
        # T310 child list usually returns current_temp as Celsius.
        # If a future API returns centi-degrees, only very large values are scaled.
        return n/100.0 if abs(n) > 200 else n
    except Exception:
        return None

def normalize_humidity(value):
    if value is None or isinstance(value, bool): return None
    data=dct(value) if not isinstance(value,(int,float,str)) else {}
    if data:
        for key in ('current_humidity','humidity','hum','value'):
            if key in data and data[key] is not None:
                return normalize_humidity(data[key])
    try:
        n=float(value)
        return n/100.0 if abs(n) > 1000 else n
    except Exception:
        return None

def is_energy_model(model):
    m=str(model or '').upper().replace('-','')
    return any(x in m for x in ('P110','P115','KP115','EP25','P304M','P316M'))

async def discover_all(user,pw,hub_ips):
    global creds
    creds=(user,pw)
    found={}
    for target in ('255.255.255.255',):
        try: found.update(await Discover.discover(target=target, timeout=5, username=user, password=pw))
        except TypeError:
            try: found.update(await Discover.discover(target=target, timeout=5))
            except Exception: pass
        except Exception: pass
    client=ApiClient(user,pw)
    out=[]
    # 単体機器。電力判定をスイッチ判定より先に行う。
    for ip,dev in found.items():
        model=str(getattr(dev,'model','') or '')
        if model.upper().startswith('H'): continue
        try:
            try: await dev.update()
            except Exception: pass
            handler=dev
            info=dct(getattr(dev,'sys_info',None))
            # tapo-pyハンドラーも試す（P110/P100/P105等）
            for method_name in (model.lower().replace('-',''), model.lower()[:4], 'p110','p115','p100','p105'):
                try:
                    method=getattr(client,method_name,None)
                    if callable(method):
                        candidate=await method(ip)
                        candidate_info=dct(await candidate.get_device_info())
                        handler=candidate; info={**info,**candidate_info}; break
                except Exception: continue
            handlers[ip]=handler
            name=dec_name(val(info,'nickname','device_name','alias',default=getattr(dev,'alias',None))) or ip
            ep, today_wh, month_wh = await read_energy(handler)
            p=ep if ep is not None else await read_power(handler,info)
            is_on=bool(val(info,'device_on','is_on','on',default=getattr(dev,'is_on',False)))
            kind='Power' if (p is not None or is_energy_model(model) or is_energy_model(val(info,'model','device_model',default=''))) else 'Switch'
            out.append({'id':ip,'name':name,'ip':ip,'hub':'','kind':kind,'model':model,'powerWatts':p,'todayWh':today_wh,'monthWh':month_wh,'isOn':is_on,'online':True})
        except Exception:
            continue
    # Hub本体は返さず、子センサーだけ返す。
    candidates=list(dict.fromkeys([x for x in hub_ips if x] + [ip for ip,d in found.items() if str(getattr(d,'model','')).upper().startswith('H')]))
    for ip in candidates:
        try:
            hub=await client.h100(ip); hubs[ip]=hub
            items=[dct(x) for x in (await hub.get_child_device_list() or [])]
            for item in items:
                model=str(val(item,'model','device_model','deviceModel',default='')).upper()
                typ=str(val(item,'type','device_type','deviceType',default='')).upper()
                if not ('T31' in model or 'SENSOR' in typ): continue
                did=str(val(item,'device_id','deviceId',default='')).strip()
                name=dec_name(val(item,'nickname','name',default=did)) or did
                sensors[did]=(ip,item)
                temp=val(item,'current_temp','current_temperature','temperature','temp_celsius','temp')
                hum=val(item,'current_humidity','humidity')
                if temp is not None or hum is not None:
                    out.append({'id':did,'name':name,'ip':'','hub':ip,'kind':'Environment','temperatureC':normalize_temp_c(temp),'humidityPercent':normalize_humidity(hum),'online':True})
        except Exception: continue
    return out

async def refresh_values():
    out=[]
    for ip,h in list(handlers.items()):
        try:
            try: await h.update()
            except Exception: pass
            info=dct(getattr(h,'sys_info',None))
            try:
                getter=getattr(h,'get_device_info',None)
                if callable(getter): info={**info,**dct(await getter())}
            except Exception: pass
            name=dec_name(val(info,'nickname','device_name','alias',default=getattr(h,'alias',ip))) or ip
            model=str(val(info,'model','device_model',default=getattr(h,'model','')))
            ep, today_wh, month_wh = await read_energy(h)
            p=ep if ep is not None else await read_power(h,info)
            is_on=bool(val(info,'device_on','is_on','on',default=getattr(h,'is_on',False)))
            kind='Power' if (p is not None or is_energy_model(model)) else 'Switch'
            out.append({'id':ip,'name':name,'ip':ip,'hub':'','kind':kind,'model':model,'powerWatts':p,'todayWh':today_wh,'monthWh':month_wh,'isOn':is_on,'online':True})
        except Exception:
            out.append({'id':ip,'name':ip,'ip':ip,'kind':'Unknown','online':False})
    for hip,hub in list(hubs.items()):
        try:
            items=[dct(x) for x in (await hub.get_child_device_list() or [])]
            for item in items:
                did=str(val(item,'device_id','deviceId',default='')).strip()
                if not did: continue
                model=str(val(item,'model','device_model','deviceModel',default='')).upper(); typ=str(val(item,'type','device_type','deviceType',default='')).upper()
                if not ('T31' in model or 'SENSOR' in typ): continue
                name=dec_name(val(item,'nickname','name',default=did)) or did
                temp=val(item,'current_temp','current_temperature','temperature','temp_celsius','temp'); hum=val(item,'current_humidity','humidity')
                if temp is not None or hum is not None:
                    out.append({'id':did,'name':name,'hub':hip,'kind':'Environment','temperatureC':normalize_temp_c(temp),'humidityPercent':normalize_humidity(hum),'online':True})
        except Exception: pass
    return out


async def direct_power_once(user, pw, ip, model, on):
    """監視セッションを使わず、対象IPへ直接接続してON/OFFする。"""
    if not user or not pw: raise Exception('TP-Link ID or password is not configured')
    if not ip: raise Exception('device IP is empty')
    client=ApiClient(user,pw)
    candidates=[]
    normalized=str(model or '').lower().replace('-','').replace('_','')
    if normalized: candidates.extend([normalized, normalized[:4]])
    candidates.extend(['p110','p115','p100','p105','p125','p304m','p316m'])
    seen=set(); last=None
    for method_name in candidates:
        if not method_name or method_name in seen: continue
        seen.add(method_name)
        try:
            method=getattr(client,method_name,None)
            if not callable(method): continue
            handler=await method(ip)
            fn=getattr(handler,'on' if on else 'off',None) or getattr(handler,'turn_on' if on else 'turn_off',None)
            if not callable(fn): continue
            await fn()
            return True
        except Exception as e:
            last=e
    # tapo-pyの機種別生成に失敗した場合はpython-kasaでIPを直接開く。
    try:
        try:
            dev=await Discover.discover_single(ip,username=user,password=pw)
        except TypeError:
            dev=await Discover.discover_single(ip)
        try: await dev.update()
        except Exception: pass
        fn=getattr(dev,'turn_on' if on else 'turn_off',None) or getattr(dev,'on' if on else 'off',None)
        if not callable(fn): raise Exception('power operation is not supported')
        await fn()
        return True
    except Exception as e:
        last=e
    raise last or Exception('direct power command failed')

async def main():
    for line in sys.stdin:
        try:
            q=json.loads(line); cmd=q.get('cmd')
            if cmd=='metadata': data=await discover_all(q.get('user',''),q.get('pass',''),q.get('hubIps',[]))
            elif cmd=='values': data=await refresh_values()
            elif cmd=='power':
                ident=q.get('id',''); h=handlers.get(ident)
                if h is None: raise Exception('device not found')
                fn = getattr(h, 'on' if q.get('on') else 'off', None) or getattr(h, 'turn_on' if q.get('on') else 'turn_off', None)
                if not callable(fn): raise Exception('power operation is not supported')
                await fn(); data=True
            else: raise Exception('unknown command')
            print(json.dumps({'ok':True,'data':data},ensure_ascii=False),flush=True)
        except Exception as e:
            print(json.dumps({'ok':False,'error':str(e)},ensure_ascii=False),flush=True)


if len(sys.argv)>1 and sys.argv[1]=='--one-shot-power':
    async def one_shot_main():
        try:
            line=sys.stdin.readline()
            q=json.loads(line)
            await direct_power_once(q.get('user',''),q.get('pass',''),q.get('ip',''),q.get('model',''),bool(q.get('on')))
            print(json.dumps({'ok':True,'data':True},ensure_ascii=False),flush=True)
        except Exception as e:
            print(json.dumps({'ok':False,'error':str(e)},ensure_ascii=False),flush=True)
    asyncio.run(one_shot_main())
else:
    asyncio.run(main())
