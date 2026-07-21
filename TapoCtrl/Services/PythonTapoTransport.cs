using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using TapoCtrl.Models;

namespace TapoCtrl.Services;

public sealed class PythonTapoTransport : ITapoTransport, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _io = new(1,1);
    private Process? _process;
    public event Action<string>? StatusChanged;

    public PythonTapoTransport(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> RefreshMetadataAsync(IReadOnlyList<DeviceSnapshot> known, CancellationToken ct)
    {
        var (user, pass) = _settingsService.LoadSecret();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            throw new InvalidOperationException("TP-Link IDまたはパスワードが未設定です。右クリック→設定から登録してください。");
        StatusChanged?.Invoke("Pythonワーカーへデバイス探索を依頼しています…");
        var result = await SendAsync(new { cmd="metadata", user, pass, hubIps=_settings.HubIps }, ct, restoreSessionOnRetry:false);
        return ParseDevices(result, known);
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> RefreshValuesAsync(IReadOnlyList<DeviceSnapshot> known, CancellationToken ct)
    {
        StatusChanged?.Invoke("Pythonワーカーから現在値を取得しています…");
        var result = await SendAsync(new { cmd="values" }, ct, restoreSessionOnRetry:true);
        return ParseDevices(result, known);
    }

    public async Task<bool> SetPowerAsync(DeviceSnapshot device, bool on, CancellationToken ct)
    {
        StatusChanged?.Invoke($"{device.Name} を {(on?"ON":"OFF")} にしています…");
        var result = await SendAsync(new { cmd="power", id=device.Ip.Length>0?device.Ip:device.Id, on }, ct, restoreSessionOnRetry:true);
        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(ReadError(result));
        return true;
    }

    public async Task<bool> SetPowerOneShotAsync(DeviceSnapshot device, bool on, CancellationToken ct = default)
    {
        var (user, pass) = _settingsService.LoadSecret();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            throw new InvalidOperationException("TP-Link IDまたはパスワードが未設定です。");
        if (string.IsNullOrWhiteSpace(device.Ip))
            throw new InvalidOperationException("対象デバイスのIPアドレスがありません。");

        StatusChanged?.Invoke($"{device.Name} へ独立コマンドで {(on ? "ON" : "OFF")} を送信しています…");
        var script = EnsureWorkerScript();
        var python = string.IsNullOrWhiteSpace(_settings.PythonPath) ? "python" : _settings.PythonPath;
        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-u \"{script}\" --one-shot-power",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("独立電源コマンドを起動できませんでした。");
        var payload = JsonSerializer.Serialize(new { user, pass, ip = device.Ip, model = device.Model, on });
        await process.StandardInput.WriteLineAsync(payload);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException("独立電源コマンドから応答がありません。" + (string.IsNullOrWhiteSpace(stderr) ? "" : " / " + stderr));
            }
            using var doc = JsonDocument.Parse(line);
            var result = doc.RootElement;
            if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new InvalidOperationException(ReadError(result));
            device.IsOn = on;
            if (!on) device.PowerWatts = 0;
            device.Timestamp = DateTime.Now;
            device.Online = true;
            StatusChanged?.Invoke($"{device.Name} を {(on ? "ON" : "OFF")} にしました。");
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw new TimeoutException("独立電源コマンドが25秒以内に完了しませんでした。");
        }
    }

    private IReadOnlyList<DeviceSnapshot> ParseDevices(JsonElement root, IReadOnlyList<DeviceSnapshot> fallback)
    {
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(ReadError(root));
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Pythonワーカーからデバイス一覧が返されませんでした。");
        var list = JsonSerializer.Deserialize<List<DeviceSnapshot>>(data.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
        return list ?? fallback;
    }

    private static string ReadError(JsonElement root)
    {
        var error=root.TryGetProperty("error",out var e)?e.GetString():null;
        var detail=root.TryGetProperty("detail",out var d)?d.GetString():null;
        return string.Join(" / ",new[]{error,detail}.Where(x=>!string.IsNullOrWhiteSpace(x))) switch { "" => "Pythonワーカーで不明なエラーが発生しました。", var x=>x };
    }

    private async Task<JsonElement> SendAsync(object payload, CancellationToken ct, bool restoreSessionOnRetry)
    {
        await _io.WaitAsync(ct);
        try
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    EnsureStarted();
                    if (attempt > 1 && restoreSessionOnRetry)
                        await RestoreWorkerSessionAsync(ct);
                    var response=await SendCoreAsync(payload, ct);
                    if(attempt>1){StatusChanged?.Invoke("通信が復旧しました。監視を再開します。");AppLog.Info("Python worker communication recovered");}
                    return response;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = new TimeoutException("Tapo通信が30秒以内に完了しませんでした。");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or JsonException)
                {
                    lastError = ex;
                }

                ResetWorker();
                if (attempt == 1)
                    StatusChanged?.Invoke("通信エラーを検出しました。Pythonワーカーを再起動して自動復旧を試みています…");
            }
            throw lastError ?? new InvalidOperationException("Tapo通信の自動復旧に失敗しました。");
        }
        finally { _io.Release(); }
    }

    private async Task<JsonElement> SendCoreAsync(object payload, CancellationToken ct)
    {
        EnsureStarted();
        var json = JsonSerializer.Serialize(payload);
        await _process!.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(line))
        {
            var stderr = _process.HasExited ? await _process.StandardError.ReadToEndAsync(ct) : string.Empty;
            throw new InvalidOperationException("Python worker did not return a response. " + stderr);
        }
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    private async Task RestoreWorkerSessionAsync(CancellationToken ct)
    {
        var (user, pass) = _settingsService.LoadSecret();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            throw new InvalidOperationException("自動復旧に必要なTP-Link IDまたはパスワードが未設定です。");
        StatusChanged?.Invoke("Pythonワーカーのデバイス情報を復元しています…");
        var restored = await SendCoreAsync(new { cmd="metadata", user, pass, hubIps=_settings.HubIps }, ct);
        if (!restored.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(ReadError(restored));
    }

    private void ResetWorker()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try { process.Dispose(); } catch { }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited:false }) return;
        var script = EnsureWorkerScript();
        var python = string.IsNullOrWhiteSpace(_settings.PythonPath) ? "python" : _settings.PythonPath;
        StatusChanged?.Invoke("Pythonワーカーを起動しています…");
        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-u \"{script}\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Python worker could not be started.");
    }

    private static string EnsureWorkerScript()
    {
        var workerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TapoCtrl", "Worker");
        Directory.CreateDirectory(workerDir);

        var workerPath = Path.Combine(workerDir, "tapo_worker.py");
        var assembly = Assembly.GetExecutingAssembly();
        using var source = assembly.GetManifestResourceStream("TapoCtrl.Python.tapo_worker.py")
            ?? throw new InvalidOperationException("EXE内のPythonワーカーを読み出せませんでした。");

        using var memory = new MemoryStream();
        source.CopyTo(memory);
        var embeddedBytes = memory.ToArray();
        var shouldWrite = !File.Exists(workerPath) || !File.ReadAllBytes(workerPath).SequenceEqual(embeddedBytes);

        if (shouldWrite)
            File.WriteAllBytes(workerPath, embeddedBytes);

        return workerPath;
    }

    public void Dispose()
    {
        ResetWorker();
        _io.Dispose();
    }
}
