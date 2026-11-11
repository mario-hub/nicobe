using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NicoBE.Encoder;

internal static class EncodeInterruptionTest
{
    sealed class Reporter(Action<EncodeProgress> report):IProgress<EncodeProgress>
    {public void Report(EncodeProgress value)=>report(value);}
    static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static Process[] Owned(string exe)
    {
        var result=new List<Process>();
        foreach(var process in Process.GetProcessesByName("ffmpeg"))
        {
            bool owned=false;try{owned=string.Equals(process.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase);}catch{}
            if(owned)result.Add(process);else process.Dispose();
        }
        return result.ToArray();
    }
    internal static int Run(string input)
    {
        if(ReleaseTestEnvironment.Root==null){Console.Error.WriteLine("Requires an isolated release-test copy and marker.");return 1;}
        var checks=new List<string>();var prior=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task=Verify(input,checks);var frame=new DispatcherFrame();
            var dispatcher=Dispatcher.CurrentDispatcher;
            task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
            Paths.WriteJson("encode-interruption-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("encode-interruption-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
    static async Task Verify(string input,List<string> checks)
    {
        void Check(bool value,string text){if(!value)throw new Exception(text);checks.Add(text);}
        string root=Path.Combine(EncoderPaths.Root,"temp","test-results","interruptions");Directory.CreateDirectory(root);
        var settings=EncoderSettings.Load();settings.Backend="CPU";settings.Codec="H264";settings.RateControl="Bitrate";settings.Passes=2;
        settings.Width=1280;settings.Height=720;settings.Speed="Fast";settings.EmbedComments=false;settings.SaveExternalAss=false;settings.UsePastLogComments=false;
        settings.OutputFolder=root;
        string existing=Path.Combine(root,Path.GetFileNameWithoutExtension(input)+".mp4"),existingAss=Path.ChangeExtension(existing,".ass");
        File.WriteAllText(existing,"existing video must survive");File.WriteAllText(existingAss,"existing ASS must survive");
        string videoHash=Hash(existing),assHash=Hash(existingAss);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach(int targetPass in new[]{1,2})
        {
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var observed=new List<Process>();bool received=false;
            try
            {
                await EncoderJob.RunPaced(input,settings,new Reporter(p=>
                {
                    if(p.Stage.StartsWith(targetPass+"/2")&&p.Detail.Contains("速度"))
                    {received=true;observed.AddRange(Owned(settings.Ffmpeg));cancel.Cancel();}
                }),cancel.Token);
                throw new Exception("Cancellation unexpectedly succeeded");
            }
            catch(OperationCanceledException ex)
            {
                Check(received&&!deadline.IsCancellationRequested&&observed.Count>0,$"Pass {targetPass}: cancellation occurred after actual FFmpeg progress");
                Check(ex.Message.Contains("処理ログ:"),"Cancellation reports processing log and retained-output information");
                Check(observed.All(p=>p.HasExited),"All observed FFmpeg processes exited before cancellation returned");
                if(targetPass==2)Check(Directory.GetFiles(root,"*_未完了_*.mp4").Length>0&&ex.Message.Contains("保持"),"Second-pass partial video retained and reported");
            }
            finally{foreach(var process in observed)process.Dispose();}
            Check(!Directory.Exists(EncoderPaths.Temp)||!Directory.GetDirectories(EncoderPaths.Temp,"passes-*").Any(),"Two-pass working data cleaned after cancellation");
            Check(Hash(existing)==videoHash&&Hash(existingAss)==assHash,"Existing video and external ASS unchanged after cancellation");
        }
        string log=Path.Combine(root,"real-ffmpeg-failure.log");
        try
        {
            await EncoderJob.RunPass(settings,new(){"-hide_banner","-f","lavfi","-i","color=size=32x32","-frames:v","1","-c:v","nicobe_missing_encoder","-f","null","-"},log,1,new Reporter(_=>{}),deadline.Token,1,1);
            throw new Exception("Invalid codec unexpectedly succeeded");
        }
        catch(Exception ex) when(ex.Message.Contains("終了コード"))
        {Check(File.ReadAllText(log).Contains("nicobe_missing_encoder"),"Actual FFmpeg nonzero exit reports failure and retains stderr diagnostics");}
        Check(Owned(settings.Ffmpeg).Length==0,"No FFmpeg left after cancellation and failure");

        // Run the actual GUI queue handler offscreen, using a synthetic invalid TS.
        string invalid=Path.Combine(root,"invalid.ts");File.WriteAllBytes(invalid,new byte[188]);
        settings.Passes=1;var window=new EncoderWindow(new[]{invalid},settings);
        try
        {
            await ((Task)typeof(EncoderWindow).GetMethod("Encode",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!).WaitAsync(TimeSpan.FromSeconds(20));
            Check(Field<TextBox>(window,"errorText").Text.Length>0&&Field<TextBox>(window,"status").Text.Contains("失敗 1件"),"GUI queue reports failure with selectable error details");
            Check(Field<Button>(window,"start").IsEnabled&&!Field<Button>(window,"cancel").IsEnabled,"GUI controls recovered after failure");
        }
        finally{window.Close();}
        // Exercise the actual GUI Cancel path after its own FFmpeg has started.
        var cancelWindow=new EncoderWindow(new[]{input},settings){RunJob=EncoderJob.RunPaced};
        try
        {
            var running=(Task)typeof(EncoderWindow).GetMethod("Encode",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(cancelWindow,null)!;
            var processes=Array.Empty<Process>();
            var until=DateTime.UtcNow.AddSeconds(20);
            while(!running.IsCompleted&&DateTime.UtcNow<until)
            {
                if(Field<TextBox>(cancelWindow,"status").Text.Contains("エンコード中"))
                {processes=Owned(settings.Ffmpeg);if(processes.Length>0)break;}
                await Task.Delay(10,deadline.Token);
            }
            Check(processes.Length>0,"GUI cancellation waits for its actual FFmpeg process");
            Field<Button>(cancelWindow,"cancel").RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Field<TextBox>(cancelWindow,"status").Text.Contains("中止しました")&&Field<TextBox>(cancelWindow,"errorText").Text.Contains("処理ログ:"),"GUI Cancel reports cancellation and log details");
            Check(Field<Button>(cancelWindow,"start").IsEnabled&&!Field<Button>(cancelWindow,"cancel").IsEnabled&&processes.All(p=>p.HasExited),"GUI Cancel recovers controls and waits for process exit");
            foreach(var process in processes)process.Dispose();
        }
        finally{Field<CancellationTokenSource?>(cancelWindow,"cancellation")?.Cancel();cancelWindow.Close();}
        Check(Hash(existing)==videoHash&&Hash(existingAss)==assHash,"Failure does not alter existing outputs");
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"NicoBE.Encoder.Cli.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
        start.ArgumentList.Add(invalid);start.ArgumentList.Add(Path.Combine(root,"cli-failure.mp4"));
        using var cli=Process.Start(start)!;var stdout=cli.StandardOutput.ReadToEndAsync();var stderr=cli.StandardError.ReadToEndAsync();
        try{await cli.WaitForExitAsync(deadline.Token);}finally{if(!cli.HasExited){cli.Kill(true);await cli.WaitForExitAsync();}}
        string cliError=await stderr,cliOutput=await stdout;
        File.WriteAllText(Path.Combine(root,"cli-failure.stderr.log"),cliError);File.WriteAllText(Path.Combine(root,"cli-failure.stdout.log"),cliOutput);
        Check(cli.ExitCode==1&&cliError.Contains("失敗:")&&!cliOutput.Contains("完了:"),$"Actual CLI returns exit 1 and stderr failure without success output (exit={cli.ExitCode})");
        Check(!File.Exists(Path.Combine(root,"cli-failure.mp4")),"Failed CLI publishes no final output");

        // Drive the CLI's real cancellation handler with a token; console-key delivery is manual.
        var assembly=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"NicoBE.Encoder.Cli.dll"));
        var entry=assembly.GetType("NicoBE.Encoder.Cli.Program")!.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!;
        using var stopCli=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        stopCli.Cancel();
        var priorOut=Console.Out;var priorError=Console.Error;
        using var capturedOut=new StringWriter();using var capturedError=new StringWriter();
        Console.SetOut(capturedOut);Console.SetError(capturedError);
        try
        {
            var running=(Task<int>)entry.Invoke(null,new object[]{new[]{input,Path.Combine(root,"cli-cancel.mp4")},stopCli.Token})!;
            int code=await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(code==130&&capturedError.ToString().Contains("中止:")&&!capturedOut.ToString().Contains("完了:"),"CLI cancellation returns 130 with stderr details and no success message");
            Check(!File.Exists(Path.Combine(root,"cli-cancel.mp4")),"Pre-canceled CLI publishes no completed output");
        }
        finally{stopCli.Cancel();Console.SetOut(priorOut);Console.SetError(priorError);}
    }
}
