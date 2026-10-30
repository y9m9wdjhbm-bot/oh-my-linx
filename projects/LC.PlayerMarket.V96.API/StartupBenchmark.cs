using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
namespace LcPlayerMarket;
internal static class StartupTiming
{
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static readonly Dictionary<string,double> marks = new();
    internal static void Mark(string name) { lock(marks) marks[name] = clock.Elapsed.TotalMilliseconds; }
    internal static void MarkOnce(string name) { lock(marks) marks.TryAdd(name, clock.Elapsed.TotalMilliseconds); }
    internal static Dictionary<string,double> Snapshot() { lock(marks) return new(marks); }
}
internal static class StartupBenchmark
{
    internal static int Run(string stateRoot,string output,string mode)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(File.Exists(output)) return 2;
        MainForm? form=null;Exception? failure=null;bool ready=false;bool refreshStarted=false;Task? refreshTask=null;int ticks=0;double maxGap=0;var previous=Stopwatch.GetTimestamp();
        try {
            var constructor=typeof(MainForm).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,[typeof(LocalStore),typeof(StandaloneMarketBackend),typeof(bool)],null)!;
            form=(MainForm)constructor.Invoke([new LocalStore(stateRoot),null,true]);
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-16000,-16000);form.ShowInTaskbar=false;
            using var timer=new System.Windows.Forms.Timer {Interval=100};
            timer.Tick+=(_,_)=>{
                double gap=Stopwatch.GetElapsedTime(previous).TotalMilliseconds;previous=Stopwatch.GetTimestamp();maxGap=Math.Max(maxGap,gap);ticks++;
                StartupTiming.MarkOnce("ui-first-tick");
                var marks=StartupTiming.Snapshot();
                ready=marks.ContainsKey("ready-for-readonly-refresh");
                if(ready && mode.Contains("readonly-refresh") && !refreshStarted)
                {
                    refreshStarted=true;StartupTiming.Mark("readonly-refresh-start");
                    refreshTask=(Task)typeof(MainForm).GetMethod("RefreshNearbyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,null)!;
                }
                if(refreshTask?.IsCompleted == true)
                {
                    StartupTiming.MarkOnce("readonly-refresh-end");
                    var scan=(Button)typeof(MainForm).GetField("scan",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
                    if(scan.Enabled)StartupTiming.MarkOnce("native-search-controls-ready");
                }
                bool historyDone=marks.ContainsKey("history-end") && (!mode.Contains("readonly-refresh") || refreshTask?.IsCompleted == true);
                if((ready&&historyDone&&ticks>=15)||marks.GetValueOrDefault("ui-first-tick")+180000 < (DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds){ timer.Stop();form.Close(); }
            };
            form.Shown+=(_,_)=>{previous=Stopwatch.GetTimestamp();timer.Start();};
            Application.Run(form);
        } catch(Exception e){failure=e;}
        finally {form?.Dispose();}
        File.WriteAllText(output,JsonSerializer.Serialize(new {Success=failure is null&&ready,Mode=mode,At=DateTimeOffset.UtcNow,StagesMs=StartupTiming.Snapshot(),UiTicks=ticks,MaximumTickGapMs=maxGap,NativeRequests=0,GameInput=0,ColdFilesystemCacheNotForced=true,Error=failure?.ToString()},new JsonSerializerOptions {WriteIndented=true}));
        return failure is null&&ready?0:1;
    }
}
