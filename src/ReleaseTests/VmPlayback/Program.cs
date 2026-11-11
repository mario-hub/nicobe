using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using NicoBE;

// Optional integration probe for a disposable logged-in Windows VM.
// Uses the unchanged release assembly; only owned portable players are controlled.
internal static class Program
{
    static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static object? Get(object o,string name)=>o.GetType().GetProperty(name,Flags)?.GetValue(o)??o.GetType().GetField(name,Flags)?.GetValue(o);
    static T Read<T>(object o,string name)=>(T)Get(o,name)!;
    static object? Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Flags)!.Invoke(o,args);
    static readonly List<object> checks=new();
    static string work="";
    static bool completed;
    static void Save(object? error=null)=>File.WriteAllText(Path.Combine(work,"playback-result.json"),JsonSerializer.Serialize(new{completed,passed=completed&&error==null,checks,error},new JsonSerializerOptions{WriteIndented=true}));
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks.Add(new{name=text,passed=true});Save();}
    static async Task Wait(Func<bool> condition,string name,int seconds=25)
    {var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed.TotalSeconds>seconds)throw new Exception("Timeout: "+name);await Task.Delay(100);}Check(true,name);}
    [STAThread] static int Main(string[] args)
    {
        work=Path.GetFullPath(args[0]);Directory.CreateDirectory(work);Paths.Initialize();Save();
        int result=1;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.DispatcherUnhandledException+=(_,e)=>{Save(e.Exception.ToString());e.Handled=true;app.Shutdown(1);};
        app.Startup+=async(_,_)=>{result=await Run();app.Shutdown(result);};app.Run();return result;
    }
    static async Task<int> Run()
    {
        var owned=new List<Process>();ResidentController? resident=null;
        IReadOnlyList<MainWindow> Sessions()=>Read<IReadOnlyList<MainWindow>>(resident!,"Sessions");
        bool Ready(MainWindow s)=>Read<bool>(s,"CommentsReady");
        bool Playing(MainWindow s)=>Read<bool>(s,"Playing");
        double Position(MainWindow s)=>Read<double>(s,"MediaPosition");
        IReadOnlyList<ActiveComment> Active(MainWindow s)=>Read<IReadOnlyList<ActiveComment>>(s,"VisibleComments");
        async Task Control(MainWindow s,uint cmd,string arg="")
        {
            var client=(MpcClient)Get(s,"mpc")!;
            nint source=((System.Windows.Interop.HwndSource)Get(client,"source")!).Handle;
            nint target=client.PlayerWindow;uint pid=(uint)client.PlayerProcessId!.Value;
            var send=typeof(Config).Assembly.GetType("NicoBE.MpcControlTransport")!.GetMethod("Send",BindingFlags.Static|BindingFlags.NonPublic)!;
            bool sent=await Task.Run(()=>(bool)send.Invoke(null,new object[]{target,source,pid,cmd,arg})!);
            if(!sent)throw new Exception("Control failed "+cmd.ToString("X"));
        }
        try
        {
            string exe=Path.Combine(work,"mpc","mpc-be64.exe"),first=Path.Combine(work,"media","first.mp4"),second=Path.Combine(work,"media","second.mp4");
            if(!File.Exists(Path.ChangeExtension(exe,".ini")))throw new Exception("Portable MPC-BE INI is required");
            foreach(var item in new[]{(first,"FIRST"),(second,"SECOND")})
                AssComments.Write(Path.ChangeExtension(item.Item1,".ass"),Enumerable.Range(0,65).Select(i=>new Comment(i,item.Item2+" コメント "+i,"#FFFFFF",1,CommentPosition.Scroll)).ToArray(),CancellationToken.None);
            Process Launch()
            {
                var start=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe)};
                foreach(string arg in new[]{"/new","/nofocus","/play",first})start.ArgumentList.Add(arg);
                var p=Process.Start(start)!;owned.Add(p);return p;
            }
            var aProcess=Launch();
            await Wait(()=>{aProcess.Refresh();return aProcess.MainWindowHandle!=0;},"real portable MPC-BE starts");
            var config=new Config{SetupCompleted=true,AutoFetchPastLogs=false,CommentsVisible=true,KeepPlaybackSpeed=true};
            resident=new ResidentController(config,false,p=>owned.Any(x=>x.Id==p.Id));
            await Wait(()=>Sessions().Count==1&&Ready(Sessions()[0])&&Playing(Sessions()[0]),"attach to already-playing MPC-BE and load external ASS");
            var a=Sessions()[0];
            await Wait(()=>Active(a).Count>0,"comments enter active scene during real playback");
            var bProcess=Launch();
            await Wait(()=>Sessions().Count==2&&Sessions().All(s=>Ready(s)&&Playing(s)),"second player with same media attaches independently");
            var b=Sessions().Single(s=>s.PlayerId==bProcess.Id);
            await Control(a,MpcClient.Pause);await Control(a,MpcClient.SetPosition,"20");
            await Wait(()=>!Playing(a)&&Math.Abs(Position(a)-20)<.1,"real pause and seek synchronize");
            double bStart=Position(b);await Task.Delay(1200);
            Check(Position(b)>bStart+.5&&Math.Abs(Position(a)-20)<.15,"second player continues while first stays paused");
            config.CommentsVisible=false;a.ApplySettings(false);b.ApplySettings(false);
            Check(!a.CommentsVisible&&!b.CommentsVisible,"comment OFF applies to both real sessions");
            config.CommentsVisible=true;a.ApplySettings(false);b.ApplySettings(false);
            Check(a.CommentsVisible&&b.CommentsVisible,"comment ON applies without restarting players");
            await Control(a,MpcClient.Play);
            object panel=Get(a,"playbackPanel")!;
            await (Task)Call(panel,"ChangeSpeedAsync",1.5)!;
            await Task.Delay(600);await (Task)Call(panel,"RefreshSpeedAsync",aProcess.MainWindowHandle)!;
            await Wait(()=>Get(panel,"SelectedSpeed") is double rate&&Math.Abs(rate-1.5)<.01,"panel changes actual player speed to 1.5x");
            await Control(a,MpcClient.OpenFile,second);
            await Wait(()=>a.SessionTitle=="second.mp4"&&Ready(a)&&Playing(a),"file switch loads second file");
            await Wait(()=>Active(a).Count>0&&Active(a).All(c=>c.Comment.Text.StartsWith("SECOND")),"old comments absent from new file scene");
            // Hidden panels intentionally do not poll. Explicitly read the real status label.
            for(int i=0;i<50;i++){await (Task)Call(panel,"RefreshSpeedAsync",aProcess.MainWindowHandle)!;if(Get(panel,"SelectedSpeed") is double r&&Math.Abs(r-1.5)<.01)break;await Task.Delay(100);}
            await Wait(()=>Get(panel,"SelectedSpeed") is double rate&&Math.Abs(rate-1.5)<.01,"speed survives real file switch");
            var client=(MpcClient)Get(a,"mpc")!;
            Check(await client.SendControlAsync(MpcClient.SetSpeed,"2"),"player speed changed outside panel path");
            await Wait(()=>Get(Get(a,"speedMemory")!,"Remembered") is double r&&Math.Abs(r-2)<.01,"external player speed is detected");
            await Control(a,MpcClient.OpenFile,first);
            await Wait(()=>a.SessionTitle=="first.mp4"&&Ready(a)&&Playing(a),"switch back after external speed change");
            for(int i=0;i<50;i++){await (Task)Call(panel,"RefreshSpeedAsync",aProcess.MainWindowHandle)!;if(Get(panel,"SelectedSpeed") is double r&&Math.Abs(r-2)<.01)break;await Task.Delay(100);}
            Check(Get(panel,"SelectedSpeed") is double current&&Math.Abs(current-2)<.01,"external speed survives file switch");
            aProcess.CloseMainWindow();
            await Wait(()=>Sessions().Count==1&&Sessions()[0].PlayerId==bProcess.Id,"closing one player preserves other session");
            double remaining=Position(b);await Task.Delay(900);Check(Position(b)>remaining+.3,"remaining player still advances");
            bProcess.CloseMainWindow();
            await Wait(()=>Sessions().Count==0,"closing last player removes all sessions");
            await Wait(()=>Read<int>(resident,"IdleCollections")>0,"idle cleanup runs after last player closes");
            Check((int)typeof(GpuNative).GetMethod("NicoActiveRenderers",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,null)! == 0,"all native renderers released");
            using(var self=Process.GetCurrentProcess()){self.Refresh();var cpu=self.TotalProcessorTime;await Task.Delay(5000);self.Refresh();checks.Add(new{name="idle sample",cpuMsPerSecond=(self.TotalProcessorTime-cpu).TotalMilliseconds/5,workingSetMiB=self.WorkingSet64/1048576d});}
            var third=Launch();await Wait(()=>Sessions().Count==1&&Ready(Sessions()[0]),"player reconnects after idle resource release");
            var fourth=Launch();await Wait(()=>Sessions().Count==2&&Sessions().All(Ready),"two players reconnect after idle");
            third.CloseMainWindow();fourth.CloseMainWindow();
            await Wait(()=>Sessions().Count==0,"simultaneous player exits remove both sessions");
            var last=Launch();await Wait(()=>Sessions().Count==1&&Ready(Sessions()[0]),"reconnect after simultaneous exits");
            resident.Dispose();resident=null;await Task.Delay(500);Check(!last.HasExited,"NicoBE shutdown leaves player open");
            completed=true;Save();return 0;
        }
        catch(Exception e){Save(e.ToString());return 1;}
        finally
        {
            resident?.Dispose();
            foreach(var p in owned){try{if(!p.HasExited){p.CloseMainWindow();if(!p.WaitForExit(2000))p.Kill();}}catch(InvalidOperationException){}p.Dispose();}
        }
    }
}
