using System.Windows;

namespace NicoBE;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return SafeFileLog.Run(()=>Run(args),ex=>TraceLog.Write("fatal "+ex),
            ex=>MessageBox.Show("NicoBEを実行できませんでした。\n"+ex.Message+"\n\n保存先: "+Paths.Root+"\n保存先のアクセス権と空き容量を確認してください。","NicoBE",MessageBoxButton.OK,MessageBoxImage.Error));
    }
    static int Run(string[] args)
    {
        Paths.Initialize();
        // WPF draws settings/hidden controls only; comments have their own D3D11 renderer.
        // Do not keep an additional WPF GPU device alive while the player is absent.
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        if(args.Contains("--failure-test"))return FailureTest.Run();
        if(args.Contains("--gpu-recovery-test"))return GpuNative.RunRecoveryTest();
        if(args.Contains("--emoji-test"))return GpuNative.RunEmojiTest();
        if(args.Contains("--playback-panel-test"))return PlaybackPanelTest.Run();
        if(args.Contains("--mpc-control-test"))return MpcControlTest.Run();
        if(args.Contains("--storage-test"))return StorageLayoutTest.Run();
        if(args.Contains("--live-test"))return LiveCommentTest.Run();
        if(args.Contains("--watcher-test"))return WatcherTest.Run(args.First(a=>!a.StartsWith("--")),args.Contains("--memory-only"));
        if(args.Contains("--self-test")) return SelfTest.Run();
        if(args.Contains("--visibility-test")) return VisibilityTest.Run();
        if(args.Contains("--ass-test")) return AssPlaybackTest.Run();
        if(args.Contains("--recording-test"))return SelfTest.ReadRecording(args.First(a=>!a.StartsWith("--")));
        if(args.Contains("--gpu-benchmark")) return GpuNative.RunBenchmark();
        if(args.Contains("--settings-test")) return SettingsTest.Run();
        var config=Config.Load(false);
        using var instance=new ResidentInstance();
        if(!instance.IsFirst){instance.Forward(args.Where(a=>a is "--settings" or "--window").ToArray());return 0;}
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.DispatcherUnhandledException+=(_,e)=>{TraceLog.Write("unhandled "+e.Exception);MessageBox.Show(e.Exception.Message,"NicoBE");e.Handled=true;};
        ResidentController? resident=null;
        void Accept(string[] received)
        {
            if(received.Contains("--settings")||received.Contains("--window"))resident!.ShowSettings();
        }
        app.Startup+=(_,_)=>
        {
            resident=new ResidentController(config);Accept(args);
            _=instance.Listen(received=>app.Dispatcher.BeginInvoke(()=>Accept(received)));
        };
        app.Exit+=(_,_)=>resident?.Dispose();
        return app.Run();
    }
}
