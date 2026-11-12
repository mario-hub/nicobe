using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;

namespace NicoBE;

// Gate-controlled background completions make races reproducible without real media or HTTP.
internal static class PlaybackLifecycleTest
{
    internal static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    internal static void Wait(Task task)
    {
        task=task.WaitAsync(TimeSpan.FromSeconds(10));
        if(!task.IsCompleted)
        {
            var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;
            task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    sealed class Handler(Func<CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage _,CancellationToken token)=>send(token);}
    static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static int Run()
    {
        if(ReleaseTestEnvironment.Root==null){Console.Error.WriteLine("Requires an isolated release-test copy and marker.");return 1;}
        var checks=new List<string>();var prior=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        void Check(bool value,string text){if(!value)throw new Exception(text);checks.Add(text);}
        try
        {
            string root=Path.Combine(Paths.Root,"temp","test-results","lifecycle");Directory.CreateDirectory(root);
            var start=DateTimeOffset.FromUnixTimeSeconds(1600000000);
            CommentMediaContext Context(string name)=>new(new(name,1,1,1,start,start.AddMinutes(1),0),"jk1",true,
                new(){new(1,name,new(){["date"]="1600000001",["user_id"]=name})},new(),null,null);
            var service=new PastLogService(new PastLogCache(Path.Combine(root,"cache")));
            var releaseA=Gate();var releaseB=Gate();var enteredA=Gate();var enteredB=Gate();
            CancellationToken tokenA=default,tokenB=default;
            var displayed=new List<string>();int states=0,subtitles=0;
            PlaybackCommentSession Session(string name,Func<string,Config,CancellationToken,CommentMediaContext> load)=>
                new(name,new(){AutoFetchPastLogs=false},(comments,_,_)=>displayed.AddRange(comments.Select(c=>c.Text)),_=>states++,_=>subtitles++,service,loadContext:load);
            using var a=Session("A.mp4",(file,_,token)=>{tokenA=token;enteredA.SetResult();releaseA.Task.GetAwaiter().GetResult();return Context(file);});
            using var b=Session("B.mp4",(file,_,token)=>{tokenB=token;enteredB.SetResult();releaseB.Task.GetAwaiter().GetResult();return Context(file);});
            using var c=Session("C.mp4",(file,_,_)=>Context(file));
            try
            {
                var workA=a.StartAsync();Wait(enteredA.Task);a.Dispose();
                var workB=b.StartAsync();Wait(enteredB.Task);b.Dispose();
                Wait(c.StartAsync());int currentStates=states,currentSubtitles=subtitles;
                // Complete in reverse order, after the newest media has already published.
                releaseB.SetResult();Wait(workB);releaseA.SetResult();Wait(workA);
                Check(tokenA.IsCancellationRequested&&tokenB.IsCancellationRequested,"A/B media preparation canceled on replacement");
                Check(displayed.Count>0&&displayed.All(x=>x=="C.mp4")&&states==currentStates&&subtitles==currentSubtitles,
                    "Late A/B completion cannot replace C comments, panel state or subtitle selection");
                Check(Field<CommentMediaContext?>(a,"context")==null&&Field<CommentMediaContext?>(b,"context")==null,"Disposed sessions do not retain late media contexts");
                var watcher=Field<FileSystemWatcher>(c,"watcher");
                // Queue a notification before disposal, then drain it after disposal.
                typeof(PlaybackCommentSession).GetMethod("CacheChanged",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,new object[]{"jk1"});
                c.Dispose();c.Dispose();Wait(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle).Task);
                Check(!watcher.EnableRaisingEvents&&!Field<DispatcherTimer>(c,"autoTimer").IsEnabled,"Cache watcher and timer stop on disposal (including repeated dispose)");
                Check(states==currentStates,"Queued old cache notification cannot publish after disposal");
            }
            finally{releaseA.TrySetResult();releaseB.TrySetResult();}

            var entered=Gate();var exited=Gate();int requests=0,publications=0;
            var shared=new PastLogService(new PastLogCache(Path.Combine(root,"shared")),()=>new(new Handler(async token=>
            {
                Interlocked.Increment(ref requests);entered.TrySetResult();
                try{await Task.Delay(Timeout.Infinite,token);return new(HttpStatusCode.OK);}
                finally{exited.TrySetResult();}
            })),TimeSpan.Zero);
            PlaybackCommentSession Shared()=>new("same.mp4",new(){AutoFetchPastLogs=false},(_,_,_)=>publications++,_=>publications++,_=>{},shared,loadContext:(file,_,_)=>Context(file));
            using var first=Shared();using var second=Shared();Wait(first.StartAsync());Wait(second.StartAsync());
            var fetch1=first.FetchAsync();Wait(entered.Task);var fetch2=second.FetchAsync();
            first.Dispose();Wait(fetch1);
            Check(requests==1&&!exited.Task.IsCompleted,"Same-media API request shared; closing one player preserves the other request");
            int before=publications;second.Dispose();Wait(fetch2);Wait(exited.Task);
            Wait(Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle).Task);
            Check(publications==before,"Closing last player cancels API and suppresses all late UI updates");
            PanelCommentState? failedState=null;
            using var failed=new PlaybackCommentSession("invalid.mp4",new(),(_,_,_)=>{},s=>failedState=s,_=>{},service,
                loadContext:(_,_,_)=>throw new InvalidDataException("MP4のサイズが不正です。"));
            Wait(failed.StartAsync());
            Check(failedState is {Status:"読取失敗",CanFetch:false,Busy:false}&&failedState.Detail.Contains("MP4のサイズが不正"),
                "Initial read failure shows short failure status and error detail, not checking forever");
            Paths.WriteJson("playback-lifecycle-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("playback-lifecycle-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
}
