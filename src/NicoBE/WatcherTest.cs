using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

namespace NicoBE;

internal static class WatcherTest
{
    public static int Run(string executable,bool memoryOnly=false)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};int result=1;
        app.Startup+=async (_,_)=>{result=await Execute(executable,memoryOnly);app.Shutdown(result);};
        app.Run();return result;
    }
    static object Memory()
    {
        using var p=Process.GetCurrentProcess();p.Refresh();
        return new {workingSetMiB=p.WorkingSet64/1048576d,privateMiB=p.PrivateMemorySize64/1048576d,managedMiB=GC.GetTotalMemory(false)/1048576d};
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Seed(MainWindow session)
    {
        var engine=(CommentEngine)typeof(MainWindow).GetField("engine",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
        engine.SetComments(Enumerable.Range(0,30000).Select(i=>new Comment(i,"メモリ解放確認 "+i+new string('あ',100),"#FFFFFF",1,CommentPosition.Scroll)).ToArray());
    }
    static async Task<int> Execute(string executable,bool memoryOnly)
    {
        var checks=new List<string>();var owned=new List<Process>();ResidentController? resident=null;
        object? oldLoad=null,newLoad=null,baseline=null,active=null,idle=null;
        void Check(bool test,string name){if(!test)throw new Exception(name);checks.Add(name);}
        async Task Wait(Func<bool> test,string name)
        {
            var clock=Stopwatch.StartNew();
            while(!test()){if(clock.Elapsed.TotalSeconds>15)throw new Exception("Timeout: "+name);await Task.Delay(100);}
            checks.Add(name);
        }
        Process Launch()
        {
            var start=new ProcessStartInfo(executable) {UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable),WindowStyle=ProcessWindowStyle.Hidden};
            foreach(string argument in new[]{"/new","/minimized","/nofocus"})start.ArgumentList.Add(argument);
            start.Environment["TEMP"]=Path.Combine(Paths.Root,"temp");start.Environment["TMP"]=start.Environment["TEMP"];
            var process=Process.Start(start)!;owned.Add(process);return process;
        }
        try
        {
            string root=Path.GetFullPath(Paths.Root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(executable).StartsWith(root,StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(executable,".ini")),"Isolated portable player inside nicobe");
            using var self=Process.GetCurrentProcess();
            if(!memoryOnly)
            {
            for(int i=0;i<10;i++)foreach(var p in PlayerLocator.Candidates())p.Dispose();
            self.Refresh();double cpu=self.TotalProcessorTime.TotalMilliseconds;var timer=Stopwatch.StartNew();
            const int repeats=300;
            for(int i=0;i<repeats;i++)foreach(var p in PlayerLocator.Candidates())p.Dispose();
            timer.Stop();self.Refresh();
            oldLoad=new{scans=repeats,cpuMsPerScan=(self.TotalProcessorTime.TotalMilliseconds-cpu)/repeats,elapsedMsPerScan=timer.Elapsed.TotalMilliseconds/repeats};
            using(var watcher=new PlayerWatcher((_,_)=>true,measure:true))
            {
                watcher.Start();await Task.Delay(2000);
                self.Refresh();cpu=self.TotalProcessorTime.TotalMilliseconds;
                long notifications=watcher.Notifications,callbackTicks=watcher.CallbackTicks;timer.Restart();
                Paths.WriteJson("watcher-phase.json",new{phase="idle-cpu",pid=self.Id});
                await Task.Delay(30000);
                timer.Stop();self.Refresh();
                newLoad=new{seconds=timer.Elapsed.TotalSeconds,processCpuMs=self.TotalProcessorTime.TotalMilliseconds-cpu,
                    callbackMs=(watcher.CallbackTicks-callbackTicks)*1000d/Stopwatch.Frequency,notifications=watcher.Notifications-notifications,
                    initialEnumerations=watcher.InitialEnumerations,pending=watcher.PendingCount,retryTimerActive=watcher.RetryActive};
                Check(watcher.InitialEnumerations==1 && !watcher.RetryActive,"No recurring scan or retry timer while idle");
            }
            }
            GC.Collect(2,GCCollectionMode.Aggressive,true,true);baseline=Memory();
            var first=Launch();
            await Wait(()=>{first.Refresh();return first.MainWindowHandle!=0;},"First player window ready");
            resident=new ResidentController(new Config{SetupCompleted=true,CommentsVisible=false},false,p=>owned.Any(x=>x.Id==p.Id));
            await Wait(()=>resident.Sessions.Count==1 && resident.Sessions[0].PlayerId==first.Id,"Detect an already-running player");
            var second=Launch();
            await Wait(()=>resident.Sessions.Count==2,"Detect a second player through a window notification");
            Check(resident.Sessions.Select(s=>s.PlayerId).Distinct().Count()==2,"No duplicate sessions");
            foreach(var session in resident.Sessions)Seed(session);
            Check(GpuNative.NicoActiveRenderers()==2,"Two native renderers active");
            active=Memory();
            Paths.WriteJson("watcher-phase.json",new{phase="active-memory",pid=self.Id,memory=active});await Task.Delay(4000);
            var references=resident.Sessions.Select(s=>new WeakReference(s)).ToArray();
            foreach(var p in owned)p.CloseMainWindow();
            await Wait(()=>resident.Sessions.Count==0,"Process exit notifications remove all sessions");
            await Wait(()=>resident.IdleCollections>0,"One-time idle memory collection");
            Check(GpuNative.NicoActiveRenderers()==0,"All native renderers released");
            Check(resident.LastIdleTrimSucceeded,"Return idle working-set pages to Windows once");
            Check(references.All(r=>!r.IsAlive),"Closed sessions and their comment caches collected");
            idle=Memory();
            Paths.WriteJson("watcher-phase.json",new{phase="released-memory",pid=self.Id,memory=idle});await Task.Delay(4000);
            int collections=resident.IdleCollections;
            var third=Launch();
            await Wait(()=>resident.Sessions.Count==1 && GpuNative.NicoActiveRenderers()==1,"Reconnect after idle memory release");
            third.CloseMainWindow();
            await Wait(()=>resident.Sessions.Count==0 && resident.IdleCollections>collections,"Return to idle after reconnect");
            Paths.WriteJson(memoryOnly?"watcher-memory-test.json":"watcher-test.json",new{passed=true,checks,oldLoad,newLoad,baseline,active,idle});return 0;
        }
        catch(Exception error){Paths.WriteJson(memoryOnly?"watcher-memory-test.json":"watcher-test.json",new{passed=false,checks,oldLoad,newLoad,baseline,active,idle,error=error.ToString()});return 1;}
        finally
        {
            resident?.Dispose();
            foreach(var p in owned)
            {
                if(!p.HasExited){p.CloseMainWindow();try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){p.Kill();}}
                p.Dispose();
            }
            Paths.WriteJson("watcher-phase.json",new{phase="finished",pid=Environment.ProcessId});
        }
    }
}
