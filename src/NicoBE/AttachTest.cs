using System.Diagnostics;
using System.Globalization;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NicoBE;

public static class AttachTest
{
    sealed class Probe : IDisposable
    {
        public readonly HwndSource Source;
        public readonly MpcClient Client;
        public double Position=-1;
        public int Connections;
        public string Version="",File="";
        public Probe(string name)
        {
            Source=new HwndSource(new HwndSourceParameters(name) {WindowStyle=unchecked((int)0x80000000),Width=0,Height=0});
            Client=new MpcClient(Source.Handle,Dispatcher.CurrentDispatcher);
            Client.Message+=(code,text)=>
            {
                if(code!=MpcClient.Position)TraceLog.Write($"attach-test {name} code=0x{code:X8} value={text}");
                if(code==MpcClient.Connect){Connections++;Client.Send(MpcClient.GetNowPlaying);Client.Send(MpcClient.GetVersion);}
                if(code==MpcClient.Position && double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double p))Position=p;
                if(code==0x5000000a)Version=text;
                if(code==MpcClient.NowPlaying)Client.Send(MpcClient.GetPlaylist);
                if(code==MpcClient.Playlist){var fields=MpcClient.SplitFields(text);if(fields.Length>1 && int.TryParse(fields[^1],out int selected) && selected>=0 && selected<fields.Length-1)File=fields[selected];}
            };
        }
        public void Dispose(){Client.Dispose();Source.Dispose();}
    }
    public static int Run(Config config,string ts)
    {
        int exit=1;var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async ()=>{exit=await Execute(config,ts);frame.Continue=false;});
        Dispatcher.PushFrame(frame);return exit;
    }
    static async Task<int> Execute(Config config,string ts)
    {
        var processes=new List<Process>();var probes=new List<Probe>();var checks=new List<object>();
        async Task Wait(Func<bool> condition,string name,int ms=20000)
        {
            var watch=Stopwatch.StartNew();
            while(!condition())
            {
                if(watch.ElapsedMilliseconds>ms)throw new Exception("Timeout: "+name);
                foreach(var p in probes)p.Client.Send(MpcClient.GetPosition);
                await Task.Delay(100);
            }
        }
        try
        {
            string exe=System.IO.Path.Combine(Paths.Runtime,"player","mpc-be64.exe");
            if(!PlayerLocator.IsPlayerExecutable(exe)||!System.IO.File.Exists(System.IO.Path.ChangeExtension(exe,".ini")))throw new Exception("開発用の既存ポータブルMPC-BEが必要です。");
            for(int i=0;i<2;i++)
            {
                var start=new ProcessStartInfo(exe) {UseShellExecute=false,WorkingDirectory=System.IO.Path.GetDirectoryName(exe),WindowStyle=ProcessWindowStyle.Minimized};
                // Ordinary startup: no /slave. Only the isolated copy is used.
                foreach(string arg in new[]{"/new","/minimized","/nofocus","/startpos","00:02:00","/play",ts})start.ArgumentList.Add(arg);
                start.Environment["TEMP"]=System.IO.Path.Combine(Paths.Root,"temp");start.Environment["TMP"]=start.Environment["TEMP"];
                var process=Process.Start(start)!;processes.Add(process);
            }
            await Wait(()=>processes.All(p=>{p.Refresh();return p.MainWindowHandle!=0;}),"two ordinary players");
            await Task.Delay(1500);
            for(int i=0;i<2;i++){var probe=new Probe("NicoBE attach test "+i);probes.Add(probe);if(!probe.Client.Attach(Process.GetProcessById(processes[i].Id)))throw new Exception("Attach request failed");}
            await Wait(()=>probes.All(p=>p.Connections==1 && p.Position>=120 && p.File==ts && p.Version.Length>0),"attach and get current media");
            checks.Add(new{test="attach-to-two-already-running-players",passed=true,pids=processes.Select(p=>p.Id).ToArray(),positions=probes.Select(p=>p.Position).ToArray(),version=probes[0].Version});
            var a=probes[0];var b=probes[1];double b0=b.Position;
            a.Client.Send(MpcClient.Pause);a.Client.Send(MpcClient.SetPosition,"3600");
            await Wait(()=>Math.Abs(a.Position-3600)<.01 && b.Position>b0+.8,"independent pause and seek");
            checks.Add(new{test="player-A-paused-and-seeked-player-B-keeps-playing",passed=true,a=a.Position,b=b.Position});
            int before=a.Connections;a.Client.SendCommandLine(a.Client.PlayerWindow,"/slave","0");
            double frozen=a.Position;await Task.Delay(1100);a.Client.Send(MpcClient.GetPosition);await Task.Delay(200);
            await Wait(()=>{a.Client.Watch();return a.Connections>before;},"watchdog reattaches",10000);
            a.Client.Send(MpcClient.SetPosition,"2400");await Wait(()=>Math.Abs(a.Position-2400)<.01,"reconnected channel");
            checks.Add(new{test="reconnect-after-command-line-clears-slave",passed=true,connections=a.Connections,position=a.Position});
            int pid=processes[0].Id;a.Client.Dispose();await Task.Delay(500);
            if(processes[0].HasExited)throw new Exception("Detaching closed the player");
            checks.Add(new{test="detach-keeps-original-player-running",passed=true,pid});
            Paths.WriteJson("attach-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("attach-test.json",new{passed=false,checks,probes=probes.Select(p=>new{p.Connections,p.Position,p.Version,p.File}).ToArray(),error=ex.ToString()});return 1;}
        finally
        {
            foreach(var p in processes)if(!p.HasExited)p.CloseMainWindow();
            foreach(var p in processes){if(!p.HasExited){try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){p.Kill();}}}
            foreach(var p in probes)p.Dispose();
            foreach(var p in processes)p.Dispose();
        }
    }
}
