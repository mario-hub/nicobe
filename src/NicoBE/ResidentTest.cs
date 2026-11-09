using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NicoBE;

public static class ResidentTest
{
    public static int Run(Config config,string ts)
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};int result=1;
        app.Startup+=async (_,_)=>{result=await Execute(config,ts);app.Shutdown(result);};
        app.Run();return result;
    }
    static async Task<int> Execute(Config config,string ts)
    {
        var processes=new List<Process>();ResidentController? resident=null;var checks=new List<object>();
        async Task Wait(Func<bool> predicate,string name)
        {
            var timer=Stopwatch.StartNew();while(!predicate()){if(timer.Elapsed.TotalSeconds>35)throw new Exception("Timeout: "+name);await Task.Delay(100);}
        }
        try
        {
            string exe=System.IO.Path.Combine(Paths.Runtime,"player","mpc-be64.exe");
            if(!PlayerLocator.IsPlayerExecutable(exe)||!System.IO.File.Exists(System.IO.Path.ChangeExtension(exe,".ini")))throw new Exception("開発用の既存ポータブルMPC-BEが必要です。");
            // Never attach the user's other players during this diagnostic.
            var testConfig=new Config {NicoJkLogFolder=config.NicoJkLogFolder};
            resident=new ResidentController(testConfig,false,p=>processes.Any(owned=>owned.Id==p.Id));
            for(int i=0;i<2;i++)
            {
                var start=new ProcessStartInfo(exe) {UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe),WindowStyle=ProcessWindowStyle.Minimized};
                foreach(string arg in new[]{"/new","/minimized","/nofocus","/play",ts})start.ArgumentList.Add(arg);
                start.Environment["TEMP"]=Path.Combine(Paths.Root,"temp");start.Environment["TMP"]=start.Environment["TEMP"];
                processes.Add(Process.Start(start)!);
                int expected=i+1;await Wait(()=>resident.Sessions.Count==expected && resident.Sessions.All(s=>s.CommentsReady && s.Playing),"automatic discovery and comments "+expected);
            }
            var a=resident.Sessions.Single(s=>s.PlayerId==processes[0].Id);var b=resident.Sessions.Single(s=>s.PlayerId==processes[1].Id);
            checks.Add(new{test="automatic-discovery-and-comment-preparation-for-two-players",passed=true,pids=processes.Select(p=>p.Id).ToArray(),files=resident.Sessions.Select(s=>s.SessionTitle).ToArray()});
            double b0=b.MediaPosition;a.Control(MpcClient.Pause);a.Control(MpcClient.SetPosition,"3600");
            await Wait(()=>!a.Playing && Math.Abs(a.MediaPosition-3600)<.01 && b.Playing && b.MediaPosition>b0+1,"independent sessions");
            checks.Add(new{test="independent-session-clocks",passed=true,a=a.MediaPosition,b=b.MediaPosition});
            if(a.VisibleComments.Count==0 || a.VisibleComments.Any(c=>c.Comment.MediaSeconds<3590 || c.Comment.MediaSeconds>3600))throw new Exception("Seek history not restored");
            checks.Add(new{test="paused-seek-restores-previous-ten-seconds",passed=true,visible=a.VisibleComments.Count});
            var settings=testConfig.Clone();settings.FontSizePercent=150;settings.ScrollSpeedPercent=75;settings.AdvanceSeconds=2.5;
            double bBefore=b.MediaPosition;resident.ApplySettings(settings);
            await Wait(()=>!a.Playing && Math.Abs(a.MediaPosition-3600)<.01 && b.Playing && b.MediaPosition>bBefore+.5,"live settings preserve playback");
            if(testConfig.FontSize!=48 || testConfig.ScrollPixelsPerSecond!=180 || a.VisibleComments.Any(c=>c.Comment.MediaSeconds+testConfig.CommentOffsetSeconds<3590 || c.Comment.MediaSeconds+testConfig.CommentOffsetSeconds>3600))throw new Exception("Live display settings not applied");
            checks.Add(new{test="live-size-speed-offset-apply-to-all-sessions-without-seeking",passed=true,fontPercent=testConfig.FontSizePercent,speedPercent=testConfig.ScrollSpeedPercent,advanceSeconds=testConfig.AdvanceSeconds});
            resident.Dispose();resident=null;await Task.Delay(500);
            if(processes.Any(p=>p.HasExited))throw new Exception("Resident shutdown closed a player");
            checks.Add(new{test="resident-shutdown-preserves-both-players",passed=true});
            Paths.WriteJson("resident-test.json",new{passed=true,trayVisibleDuringTest=false,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("resident-test.json",new{passed=false,checks,sessions=resident?.Sessions.Select(s=>new{s.PlayerId,s.SessionTitle,s.CommentsReady,s.Playing,s.MediaPosition}).ToArray(),error=ex.ToString()});return 1;}
        finally
        {
            resident?.Dispose();
            foreach(var p in processes)if(!p.HasExited)p.CloseMainWindow();
            foreach(var p in processes){if(!p.HasExited){try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){p.Kill();}}p.Dispose();}
        }
    }
}
