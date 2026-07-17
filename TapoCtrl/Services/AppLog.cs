using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
namespace TapoCtrl.Services;
public enum AppLogLevel { Error=0, Warning=1, Information=2, Debug=3, Trace=4 }
public static class AppLog
{
 private static readonly object Gate=new(); private static bool _enabled=true,_entry=false; private static AppLogLevel _level=AppLogLevel.Information;
 private static string LogDir=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TapoCtrl","logs");
 public static void Configure(bool enabled,string level,bool entry){_enabled=enabled;_entry=entry;if(!Enum.TryParse(level,true,out _level))_level=AppLogLevel.Information;Directory.CreateDirectory(LogDir);ArchiveOldLogs();}
 public static void Enter(string text="",[CallerFilePath]string file="",[CallerLineNumber]int line=0,[CallerMemberName]string member=""){if(_entry)Write(AppLogLevel.Trace,$"ENTER {member}: {text}",file,line);}
 public static void Info(string text,[CallerFilePath]string file="",[CallerLineNumber]int line=0)=>Write(AppLogLevel.Information,text,file,line);
 public static void Warn(string text,[CallerFilePath]string file="",[CallerLineNumber]int line=0)=>Write(AppLogLevel.Warning,text,file,line);
 public static void Error(string text,Exception? ex=null,[CallerFilePath]string file="",[CallerLineNumber]int line=0)=>Write(AppLogLevel.Error,ex is null?text:$"{text} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",file,line);
 public static void Debug(string text,[CallerFilePath]string file="",[CallerLineNumber]int line=0)=>Write(AppLogLevel.Debug,text,file,line);
 private static void Write(AppLogLevel level,string text,string file,int line){if(!_enabled||level>_level)return;try{lock(Gate){Directory.CreateDirectory(LogDir);var path=Path.Combine(LogDir,$"TapoCtrl_{DateTime.Now:yyyyMMdd}.log");File.AppendAllText(path,$"{DateTime.Now:yy/MM/dd HH:mm:ss} [{level}] [{Path.GetFileName(file)}:{line}] {text}{Environment.NewLine}");}}catch{}}
 private static void ArchiveOldLogs(){try{Directory.CreateDirectory(LogDir);var archive=Path.Combine(LogDir,"archive");Directory.CreateDirectory(archive);var currentMonday=StartOfWeek(DateTime.Today);var groups=Directory.GetFiles(LogDir,"TapoCtrl_*.log").Select(f=>(Path:f,Date:LogDate(f))).Where(x=>x.Date.HasValue&&x.Date.Value<currentMonday).GroupBy(x=>StartOfWeek(x.Date!.Value));foreach(var week in groups){var zip=Path.Combine(archive,$"TapoCtrl_logs_{week.Key:yyyyMMdd}-{week.Key.AddDays(6):yyyyMMdd}.zip");using var za=ZipFile.Open(zip,ZipArchiveMode.Update);foreach(var item in week){var n=Path.GetFileName(item.Path);za.GetEntry(n)?.Delete();za.CreateEntryFromFile(item.Path,n,CompressionLevel.Optimal);File.Delete(item.Path);}}}catch{}}
 private static DateTime? LogDate(string path){var name=Path.GetFileNameWithoutExtension(path);return DateTime.TryParseExact(name,"'TapoCtrl_'yyyyMMdd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var date)?date.Date:null;}
 private static DateTime StartOfWeek(DateTime date)=>date.Date.AddDays(-(((int)date.DayOfWeek+6)%7));
}
