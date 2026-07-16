using System.IO;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TapoCtrl.Models;
namespace TapoCtrl.Services;
public sealed class SettingsService
{
    private readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapoCtrl");
    public string SettingsPath => Path.Combine(_dir, "settings.json");
    public AppSettings Load()
    {
        try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions()) ?? new(); } catch { }
        return new();
    }
    public void Save(AppSettings settings) { Directory.CreateDirectory(_dir); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions())); }
    public void SaveSecret(string user, string pass)
    {
        Directory.CreateDirectory(_dir); var json = JsonSerializer.Serialize(new { user, pass });
        File.WriteAllBytes(Path.Combine(_dir, "credentials.bin"), ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
    }
    public (string User,string Pass) LoadSecret()
    {
        try { var bytes=ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(_dir,"credentials.bin")),null,DataProtectionScope.CurrentUser); using var d=JsonDocument.Parse(bytes); return (d.RootElement.GetProperty("user").GetString()??"", d.RootElement.GetProperty("pass").GetString()??""); } catch { return ("",""); }
    }
    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented=true, PropertyNameCaseInsensitive=true, Converters={new JsonStringEnumConverter()} };
}
