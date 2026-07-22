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
 private static void ArchiveOldLogs(){try{Directory.CreateDirectory(LogDir);var archive=Path.Combine(LogDir,"archive");Directory.CreateDirectory(archive);var cutoff=DateTime.Today.AddDays(-7);var old=Directory.GetFiles(LogDir,"TapoCtrl_*.log").Where(f=>File.GetLastWriteTime(f)<cutoff).ToList();if(old.Count==0)return;var monday=DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek+6)%7));var zip=Path.Combine(archive,$"TapoCtrl_logs_{monday.AddDays(-7):yyyyMMdd}.zip");using var za=ZipFile.Open(zip,ZipArchiveMode.Update);foreach(var f in old){var n=Path.GetFileName(f);za.GetEntry(n)?.Delete();za.CreateEntryFromFile(f,n,CompressionLevel.Optimal);File.Delete(f);}}catch{}}
}
