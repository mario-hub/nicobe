using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NicoBE.Encoder;

internal static class EncoderRestartTest
{
    static T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
    static Task Encode(EncoderWindow window)=>(Task)typeof(EncoderWindow).GetMethod("Encode",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
    internal static int Run()
    {
        if(ReleaseTestEnvironment.Root==null)throw new InvalidOperationException("Isolated release-test environment required.");
        var checks=new List<string>();var prior=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task=RunAsync(checks).WaitAsync(TimeSpan.FromSeconds(20));
            if(!task.IsCompleted)
            {
                var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;
                task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(EncoderPaths.Logs,"restart-test.json"),JsonSerializer.Serialize(new{passed=true,checks}));return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(EncoderPaths.Logs,"restart-test.json"),JsonSerializer.Serialize(new{passed=false,checks,error=ex.ToString()}));return 1;}
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
    static async Task RunAsync(List<string> checks)
    {
        void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        string root=Path.Combine(EncoderPaths.Temp,"restart");Directory.CreateDirectory(root);
        string first=Path.Combine(root,"first.ts"),second=Path.Combine(root,"second.ts");
        File.WriteAllText(first,"synthetic first");File.WriteAllText(second,"synthetic second");
        string ffmpeg=Path.Combine(root,"ffmpeg.exe");File.WriteAllText(ffmpeg,"");File.WriteAllText(Path.Combine(root,"ffprobe.exe"),"");
        // These placeholder files only satisfy input validation; RunJob is injected.
        var settings=new EncoderSettings{Ffmpeg=ffmpeg,Backend="CPU",Passes=1,BitrateMbps=2.3,FfmpegGuideAcknowledged=true,OutputFolder=root};
        int probes=0,notices=0;bool missing=false;
        using(var watch=new EncoderRuntimeWatch(_=>notices++,()=>{probes++;return missing?new[]{"removed-runtime"}:Array.Empty<string>();}))
        {
            watch.CheckNow();Check(probes==0&&!watch.TimerRunning,"Idle monitor has no checks or timer");
            watch.Start();Check(probes==1&&watch.TimerRunning&&!watch.RestartRequired,"Healthy runtime starts periodic monitoring");
            watch.Stop();watch.CheckNow();Check(probes==1&&!watch.TimerRunning,"Stopping encoding stops checks");
            missing=true;watch.Start();watch.CheckNow();Check(notices==1&&watch.RestartRequired&&!watch.TimerRunning,"Missing runtime warns once and preserves restart-required state");
        }
        int jobs=0;missing=false;
        var window=new EncoderWindow(new[]{first,second},settings);
        window.RuntimeWatch.Dispose();window.RuntimeWatch=new EncoderRuntimeWatch(_=>{},()=>missing?new[]{"removed-runtime"}:Array.Empty<string>());
        string journal="";
        try
        {
            window.RunJob=async (input,s,progress,token)=>
            {
                jobs++;journal=Field<EncoderQueueRecovery>(window,"recovery").FileName;
                var before=JsonSerializer.Deserialize<EncoderQueueSnapshot>(File.ReadAllText(journal))!;
                Check(before.Files.SequenceEqual(new[]{first,second}),"Full queue is durable before first job starts");
                Check(EncoderQueueRecovery.RestoreFirst()==null,"Other windows cannot restore an actively owned queue");
                missing=true;window.RuntimeWatch.CheckNow();await Task.Yield();
                Check(!token.IsCancellationRequested,"Runtime update does not cancel current job");
                return new EncodeResult(Path.ChangeExtension(input,"mp4"),"test.log",null!);
            };
            await Encode(window);
            Check(jobs==1&&Field<TextBox>(window,"status").Text.Contains("再起動が必要"),"Current job completes and next job is not started");
            Check(!Field<Button>(window,"start").IsEnabled&&!window.RuntimeWatch.TimerRunning,"Further starts disabled and timer stopped after detection");
            var saved=JsonSerializer.Deserialize<EncoderQueueSnapshot>(File.ReadAllText(journal))!;
            Check(saved.Files.SequenceEqual(new[]{second})&&saved.Settings.BitrateMbps==2.3,"Completed input excluded; remaining input and settings persisted");
        }
        finally{window.Close();}
        var resumed=new EncoderWindow(Array.Empty<string>(),settings);
        try
        {
            resumed.RestoreQueue();
            Check(Field<ObservableCollection<QueueItem>>(resumed,"queue").Select(q=>q.Path).SequenceEqual(new[]{second})&&jobs==1,
                "Fresh window restores remaining queue without automatically encoding");
            resumed.RunJob=(input,s,p,t)=>{jobs++;Check(input==second&&s.BitrateMbps==2.3,"Resume uses saved input and settings");return Task.FromResult(new EncodeResult("second.mp4","test.log",null!));};
            await Encode(resumed);
            Check(jobs==2&&!File.Exists(journal),"Resume completes only remaining input and removes finished checkpoint");
        }
        finally{resumed.Close();}
        var failed=new EncoderWindow(new[]{first,second},settings);missing=false;int failures=0;
        failed.RuntimeWatch.Dispose();failed.RuntimeWatch=new EncoderRuntimeWatch(_=>{},()=>missing?new[]{"removed-runtime"}:Array.Empty<string>());
        try
        {
            failed.RunJob=(_,_,_,_)=>{failures++;missing=true;failed.RuntimeWatch.CheckNow();throw new IOException("synthetic runtime read failure");};
            await Encode(failed);
            var state=Field<EncoderQueueRecovery>(failed,"recovery").Snapshot!;
            Check(failures==1&&state.Files.SequenceEqual(new[]{first,second}),"Failed current job remains retryable; subsequent job not started");
            Field<EncoderQueueRecovery>(failed,"recovery").Save(Array.Empty<string>(),settings);
        }
        finally{failed.Close();}
        using(var checkpoint=EncoderQueueRecovery.Create())
        {
            checkpoint.Save(new[]{first},settings);string before=File.ReadAllText(checkpoint.FileName);
            Directory.CreateDirectory(checkpoint.FileName+".tmp");bool rejected=false;
            try{checkpoint.Save(new[]{second},settings);}catch(IOException){rejected=true;}catch(UnauthorizedAccessException){rejected=true;}
            Check(rejected&&File.ReadAllText(checkpoint.FileName)==before,"Failed checkpoint write preserves previous valid queue");
            Directory.Delete(checkpoint.FileName+".tmp");checkpoint.Save(Array.Empty<string>(),settings);
        }
        Check(File.ReadAllText(first)=="synthetic first"&&File.ReadAllText(second)=="synthetic second","Input files remain untouched");
    }
}
