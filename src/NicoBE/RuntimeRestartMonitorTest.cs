namespace NicoBE;

internal static class RuntimeRestartMonitorTest
{
    internal static int Run()
    {
        if(ReleaseTestEnvironment.Root==null){Console.Error.WriteLine("Requires an isolated release-test copy and marker.");return 1;}
        var checks=new List<string>();
        void Check(bool value,string text){if(!value)throw new Exception(text);checks.Add(text);}
        try
        {
            int reads=0,notifications=0,resolutions=0;bool present=true;string notice="";
            using var monitor=new RuntimeRestartMonitor(message=>{notifications++;notice=message;},
                ()=>{resolutions++;return new[]{"test-core-runtime.dll","test-desktop-runtime.dll"};},
                _=>{reads++;return present;});
            monitor.CheckNow();
            Check(reads==0&&resolutions==0&&!monitor.TimerRunning,"Idle startup performs no runtime file checks or timer ticks");
            monitor.SetActive(true);
            Check(reads==2&&resolutions==1&&monitor.TimerRunning&&notifications==0,"First player checks immediately and starts the shared timer");
            monitor.SetActive(true);
            Check(reads==2&&monitor.TimerRunning,"Additional player does not create another check or timer");
            monitor.CheckNow();
            Check(reads==4&&notifications==0,"Existing in-use runtime remains valid; no warning merely for an update elsewhere");
            monitor.SetActive(false);present=false;int idleReads=reads;
            monitor.CheckNow();
            Check(reads==idleReads&&!monitor.TimerRunning,"Last player exit stops all checks, including when files disappear while idle");
            monitor.SetActive(true);
            Check(notifications==1&&notice.Contains("再起動")&&notice.Contains("test-core-runtime.dll")&&!monitor.TimerRunning,
                "Next player detects removed runtime and requests restart with missing file details");
            monitor.CheckNow();monitor.SetActive(false);monitor.SetActive(true);
            Check(notifications==1&&!monitor.TimerRunning&&reads==idleReads+2,"Restart warning is issued once per resident process");
            using var desktop=new RuntimeRestartMonitor(_=>notifications++,()=>new[]{"core","desktop"},p=>p=="core");
            desktop.SetActive(true);
            Check(notifications==2&&!desktop.TimerRunning,"Desktop runtime removal alone also triggers restart warning");
            using var disposed=new RuntimeRestartMonitor(_=>{},()=>new[]{"current"},_=>{reads++;return true;});
            disposed.SetActive(true);disposed.Dispose();idleReads=reads;
            disposed.SetActive(true);disposed.CheckNow();
            Check(reads==idleReads&&!disposed.TimerRunning,"Disposal stops the timer and prevents later checks");
            Check(RuntimeRestartMonitor.Interval==TimeSpan.FromSeconds(30),"Active check interval is 30 seconds");
            Paths.WriteJson("runtime-restart-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("runtime-restart-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
    }
}
