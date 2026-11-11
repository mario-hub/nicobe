using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;

namespace NicoBE;

internal static class CommentSceneTest
{
    sealed class NoStartup : IStartupRegistration
    {public string? Read()=>null;public void Write(string? value)=>throw new Exception("Unexpected startup registration");}
    internal static int RunContinuity()
    {
        if(ReleaseTestEnvironment.Root==null){Console.Error.WriteLine("Requires an isolated release-test copy and marker.");return 1;}
        var checks=new List<string>();var prior=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        void Check(bool value,string text){if(!value)throw new Exception(text);checks.Add(text);}
        try
        {
            var start=DateTimeOffset.FromUnixTimeSeconds(1600000000);
            var recording=new RecordingInfo("continuity.mp4",1,1,1,start,start.AddMinutes(1),0);
            var range=PastLogRange.For(recording);
            SourceComment Chat(int second)=>new(second,"comment "+second,new(){["date"]=(1600000000+second).ToString(),["user_id"]="u",["no"]=second.ToString()});
            foreach(bool paused in new[]{false,true})foreach(double rate in new[]{.5,1,2.5})
            {
                var config=new Config{AutoFetchPastLogs=false,AccelerateLongComments=false};var engine=new CommentEngine(config);
                int measurements=0,resets=0,commits=0;
                engine.Measure=_=>{measurements++;return (100,40,new object());};
                engine.OnPosition(2,0);engine.SetPlaying(!paused,0);
                var service=new PastLogService(new PastLogCache(Path.Combine(Paths.Root,"temp","test-results",$"continuity-{paused}-{rate}")));
                using var session=new PlaybackCommentSession(recording.Path,config,(values,reset,append)=>
                {if(reset){resets++;engine.SetComments(values);}else if(append)engine.AppendComments(values);else engine.UpdateComments(values);},_=>{},_=>{},service);
                Field(session,"context",new CommentMediaContext(recording,"jk1",true,new(),new(),null,null));
                Call(session,"AddSaved",(object)new[]{Chat(1)});Call(session,"Render",false);
                var original=engine.Active.Single();
                var editor=new SettingsWindow(config,c=>{config.CopyFrom(c);engine.RefreshSettings();session.SettingsChanged();},new NoStartup(),persist:_=>commits++);
                try
                {
                    for(int i=1;i<=4;i++)
                    {
                        double oldX=engine.X(original),oldMotion=engine.MotionSeconds;
                        engine.OnPosition(paused?2:2+i*.1*rate,i*.1);
                        double x=engine.X(original),y=engine.Y(original),motion=engine.MotionSeconds;
                        editor.Apply(false);
                        var snapshot=new PastLogSnapshot(new(){Chat(1),Chat(10)},new(){new(range.Start,range.End,start.AddDays(1),true)},range);
                        var pending=Invoke(session,"SetPastAsync",snapshot);
                        editor.Apply(false);Wait(pending);Call(session,"Render",false);editor.Apply(false);
                        Check(ReferenceEquals(original,engine.Active.Single())&&engine.X(original)==x&&engine.Y(original)==y&&engine.MotionSeconds==motion,
                            $"Apply before/during/after fetched completion preserves active identity, XY and clock: paused={paused}, rate={rate}, iteration={i}");
                        Check(paused?x==oldX:Math.Abs((oldX-x)-(motion-oldMotion)*config.ScrollPixelsPerSecond)<.001,
                            "Motion continues at wall-clock speed, or remains frozen while paused");
                    }
                    Check(commits==12&&resets==1&&measurements==1,"Repeated actual SettingsWindow.Apply persists but does not reconstruct or remeasure");
                    if(!paused){engine.OnPosition(10,2);Check(engine.Active.Count(c=>c.Comment.Text=="comment 10")==1,"Merged future comment appears exactly once");}
                }
                finally{editor.Close();}
            }
            Paths.WriteJson("scene-continuity-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("scene-continuity-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
    static void Field(PlaybackCommentSession session,string name,object value)=>
        typeof(PlaybackCommentSession).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(session,value);
    static Task Invoke(PlaybackCommentSession session,string name,params object[] args)=>
        (Task)typeof(PlaybackCommentSession).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(session,args)!;
    static void Wait(Task task)
    {
        if(!task.IsCompleted)
        {
            var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();
            task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    static void Call(PlaybackCommentSession session,string name,params object[] args)
    {
        if(name=="OnLive")
        {
            typeof(PlaybackCommentSession).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(session,args);
            Wait((Task)typeof(PlaybackCommentSession).GetField("liveTail",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!);
        }
        else Wait(Invoke(session,name+"Async",args));
    }
    internal static void Verify(string root,Action<bool,string> check)
    {
        var original=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try{VerifyCore(root,check);}finally{SynchronizationContext.SetSynchronizationContext(original);}
    }
    static void VerifyCore(string root,Action<bool,string> check)
    {
        VerifySeparatedSources(root,check);
        VerifyAutomaticFetch(root,check);
        var start=DateTimeOffset.FromUnixTimeSeconds(1600000000);
        var recording=new RecordingInfo("mock.ts",1,1,1,start,start.AddMinutes(1),0);
        long zero=CommentSources.Microseconds(start);var range=PastLogRange.For(recording);
        SourceComment Chat(int second,string thread="lv1")=>new(second,"comment "+second,
            new(){["thread"]=thread,["no"]=second.ToString(),["date"]=(1600000000+second).ToString(),["user_id"]="user"});
        PastLogSnapshot Snapshot(bool complete,params SourceComment[] comments)=>new(comments.ToList(),
            new(){new(range.Start,complete?range.End:range.Start+30_000_000,start.AddDays(1),true)},range);
        foreach(bool apiFirst in new[]{false,true})foreach(bool paused in new[]{false,true})
        {
            var config=new Config {AccelerateLongComments=false};var engine=new CommentEngine(config);
            engine.Measure=_=>(100,40,new object());engine.OnPosition(2,0);engine.SetPlaying(!paused,0);
            var service=new PastLogService(new PastLogCache(Path.Combine(root,$"scene-{apiFirst}-{paused}")));
            int resets=0;PanelCommentState? state=null;
            using var session=new PlaybackCommentSession("mock.ts",config,(comments,reset,append)=>
            {if(reset){resets++;engine.SetComments(comments);}else if(append)engine.AppendComments(comments);else engine.UpdateComments(comments);},
                s=>state=s,_=>{},service);
            Field(session,"context",new CommentMediaContext(recording,"jk1",true,new(),new(),null,null));
            if(apiFirst)Call(session,"SetPast",Snapshot(false,Chat(1)));
            else Call(session,"AddSaved",(object)new[]{Chat(1,"ch1_1600000000")});
            Call(session,"Render",false);
            var original=engine.Active.Single();
            engine.OnPosition(paused?2:4,.5);double x=engine.X(original),y=engine.Y(original);
            if(apiFirst)
                Call(session,"OnLive",new LiveCommentBatch(recording,"jk1",new(){LogReader.ForPlayback(Chat(1,"ch1_1600000000"))!},1,0,false,null));
            else {Call(session,"SetPast",Snapshot(false,Chat(1)));Call(session,"Render",false);}
            Call(session,"SetPast",Snapshot(true,Chat(1),Chat(3),Chat(6)));Call(session,"Render",false);
            check(resets==1 && ReferenceEquals(engine.Active.Single(),original) && engine.X(original)==x && engine.Y(original)==y,
                $"automatic source addition and fetched status preserve position, apiFirst={apiFirst}, paused={paused}");
            check(state?.Status=="取得済み" && engine.CommentCount==3,"fetched status and deduplicated future timeline update together");
            Call(session,"Render",false);Call(session,"Render",false);
            check(resets==1 && ReferenceEquals(engine.Active.Single(),original),"repeated cache notifications do not recreate active comments");
            if(!paused)
            {
                engine.OnPosition(6,1);
                check(engine.Active.Count(c=>c.Comment.Text=="comment 6")==1 && engine.Active.All(c=>c.Comment.Text!="comment 3"),
                    "future comments appear once; late historical comments wait for a seek");
            }
            Wait(session.Choose("saved",false));
            check(resets==2 && engine.Active.All(c=>!ReferenceEquals(c,original)),"manual source changes still reconstruct immediately");
        }
        {
            var engine=new CommentEngine(new());engine.OnPosition(2,0);
            var service=new PastLogService(new PastLogCache(Path.Combine(root,"scene-initial")));
            int resets=0;
            using var session=new PlaybackCommentSession("mock.ts",new(),(comments,reset,_)=>
            {if(reset){resets++;engine.SetComments(comments);}else engine.UpdateComments(comments);},_=>{},_=>{},service);
            var context=new CommentMediaContext(recording,"jk1",false,new(),new(),null,null);
            Field(session,"context",context);Call(session,"Render",false);
            Call(session,"AddSaved",(object)new[]{Chat(1)});Call(session,"Render",false);
            check(engine.Active.Count==1 && resets==2,"first nonempty data restores opening comments after an empty cache");
        }
        {
            var config=new Config {CommentPreference="ASS"};var service=new PastLogService(new PastLogCache(Path.Combine(root,"scene-ass")));
            var resets=new List<bool>();
            using var session=new PlaybackCommentSession("mock.mkv",config,(_,reset,_)=>resets.Add(reset),_=>{},_=>{},service);
            Field(session,"context",new CommentMediaContext(recording,"jk1",false,new(),new(){new(1,"ASS","#FFFFFF",1,CommentPosition.Scroll)},null,null));
            Call(session,"Render",false);Call(session,"SetPast",Snapshot(true,Chat(1)));Call(session,"Render",false);
            check(resets.SequenceEqual(new[]{true,true}) && !session.Selection.Ass,"automatic ASS replacement still clears the incompatible old source");
        }
        {
            var service=new PastLogService(new PastLogCache(Path.Combine(root,"scene-background")));
            int publications=0,lastCount=0;var dispatcher=Dispatcher.CurrentDispatcher;int uiThread=Environment.CurrentManagedThreadId;
            using var session=new PlaybackCommentSession("mock.ts",new(),(comments,_,_)=>
            {check(Environment.CurrentManagedThreadId==uiThread,"scene publication stays on the UI thread");publications++;lastCount=comments.Count;},_=>{},_=>{},service);
            Field(session,"context",new CommentMediaContext(recording,"jk1",false,new(),new(),null,null));
            using var release=new ManualResetEventSlim();
            IEnumerable<SourceComment> Deferred()
            {
                dispatcher.BeginInvoke(()=>release.Set());
                if(!release.Wait(TimeSpan.FromSeconds(3)))throw new Exception("Saved-log indexing blocked the UI dispatcher");
                yield return Chat(1);
            }
            Call(session,"AddSaved",Deferred());
            check(release.IsSet,"saved-log preparation leaves the UI dispatcher responsive");
            var many=Enumerable.Range(1,30000).Select(i=>Chat(i)).ToArray();
            Call(session,"AddSaved",(object)many);
            bool responsive=false;var operation=Invoke(session,"RenderAsync",false);
            dispatcher.BeginInvoke(()=>responsive=!operation.IsCompleted);Wait(operation);
            check(responsive && lastCount==30000,"large comment merge runs off the UI thread");
            int before=publications;
            operation=Invoke(session,"RenderAsync",false);
            session.Dispose();Wait(operation);
            check(publications==before,"closing a file discards its pending background scene");
        }
    }
    sealed class FetchHandler(Func<CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(token);}
    static void VerifySeparatedSources(string root,Action<bool,string> check)
    {
        string folder=Path.Combine(root,"separated");Directory.CreateDirectory(Path.Combine(folder,"logs","jk1"));
        string file=Path.Combine(folder,"comments.mp4");File.WriteAllBytes(file,Array.Empty<byte>());
        var start=DateTimeOffset.FromUnixTimeSeconds(1600000000);
        new Mp4RecordingMetadata(1,start,start,1,1,"synthetic.ts",60,0,0).Append(file);
        SourceComment Chat(int second,string text)=>new(second,text,new(){["date"]=(1600000000+second).ToString(),["no"]=second.ToString(),["user_id"]="u",["thread"]="lv1"});
        new Mp4CommentData(1,"jk1",new(){Chat(1,"shared"),Chat(3,"embedded-only")}).Append(file,CancellationToken.None);
        File.WriteAllText(Path.Combine(folder,"logs","jk1","1600000000.txt"),
            "<chat date=\"1600000001\" no=\"1\" user_id=\"u\" thread=\"lv1\">shared</chat>\n<chat date=\"1600000002\" no=\"2\" user_id=\"u\" thread=\"lv1\">local-only</chat>\n");
        var config=new Config{NicoJkLogFolder=Path.Combine(folder,"logs"),AutoFetchPastLogs=true,ChannelOverrides=new(){["1:1"]="jk1"}};
        var context=CommentMediaContext.Load(file,config,CancellationToken.None);
        check(context.Saved.Select(c=>c.Text).SequenceEqual(new[]{"shared","local-only"})&&context.Embedded.Select(c=>c.Text).SequenceEqual(new[]{"shared","embedded-only"}),"MP4 loader keeps NicoJK folder logs separate from original container data");
        int requests=0;var service=new PastLogService(new PastLogCache(Path.Combine(folder,"cache")),()=>new(new FetchHandler(_=>
        {requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<packet/>")});})),TimeSpan.Zero);
        IReadOnlyList<Comment> shown=Array.Empty<Comment>();PanelCommentState? state=null;
        using var session=new PlaybackCommentSession(file,config,(comments,_,_)=>shown=comments,s=>state=s,_=>{},service);
        Wait(session.StartAsync());
        check(state is {SavedAvailable:true,EmbeddedAvailable:true,Saved:true,Embedded:true}&&shown.Count==3,"Both original sources auto-select and identical comments render once");
        Wait(session.Choose("saved",false));check(shown.Select(c=>c.Text).SequenceEqual(new[]{"shared","embedded-only"})&&state is {Saved:false,Embedded:true},"NicoJK OFF leaves only MP4 originals");
        Wait(session.Choose("embedded",false));check(shown.Count==0,"Both source switches can be OFF independently");
        Wait(session.FetchAsync(automatic:true));check(requests==0,"Hidden existing original sources still prevent automatic API fetch");
        Wait(session.Choose("saved",true));check(shown.Select(c=>c.Text).SequenceEqual(new[]{"shared","local-only"}),"MP4 OFF leaves only NicoJK logs");
        Wait(session.Choose("embedded",true));check(shown.Count==3,"Re-enabling MP4 does not duplicate shared comments");
        var chosen=new CommentSourceSelection();chosen.Automatic(false,false,true,false,"Original",true);
        check(chosen.Embedded&&!chosen.Ass,"Embedded source participates in original-vs-ASS preference");
        chosen.Choose("ass",true);check(!chosen.Embedded&&!chosen.Saved&&!chosen.Past,"ASS excludes every original source");
        chosen.Choose("embedded",true);check(chosen.Embedded&&!chosen.Ass,"MP4 source excludes ASS");
        var only=config.Clone();only.NicoJkLogFolder="";
        using var embeddedOnly=new PlaybackCommentSession(file,only,(_,_,_)=>{},s=>state=s,_=>{},service);
        Wait(embeddedOnly.StartAsync());Wait(embeddedOnly.FetchAsync(automatic:true));
        check(state is {SavedAvailable:false,EmbeddedAvailable:true,Saved:false,Embedded:true}&&requests==0,"MP4-only availability and automatic-fetch suppression work without NicoJK folder");
    }
    static void VerifyAutomaticFetch(string root,Action<bool,string> check)
    {
        check(new Config().AutoFetchPastLogs,"Automatic past logs default to enabled");
        var start=DateTimeOffset.FromUnixTimeSeconds(1600000000);
        var recording=new RecordingInfo("mock.ts",1,1,1,start,start.AddMinutes(1),0);
        var chat=new SourceComment(1,"local",new(){["date"]="1600000001"});
        foreach(string kind in new[]{"saved","ass","loading","off","empty","ordinary-subtitle"})
        {
            int requests=0;
            var service=new PastLogService(new PastLogCache(Path.Combine(root,"auto-"+kind)),()=>new(new FetchHandler(_=>
            {requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<packet></packet>")});})),TimeSpan.Zero);
            var config=new Config{AutoFetchPastLogs=kind!="off"};
            using var session=new PlaybackCommentSession("mock.ts",config,(_,_,_)=>{},_=>{},_=>{},service);
            Field(session,"context",new CommentMediaContext(recording,"jk1",false,new(),kind=="ass"?new(){new(1,"ASS","#FFFFFF",1,CommentPosition.Scroll)}:new(),null,null));
            Field(session,"localReady",kind!="loading");
            if(kind=="saved")
            {
                Call(session,"AddSaved",(object)new[]{chat});
                Wait(session.Choose("saved",false)); // Hidden comments still prevent automatic fetching.
            }
            Wait(session.FetchAsync(automatic:true));
            check(requests==(kind is "empty" or "ordinary-subtitle"?1:0),"Automatic fetch policy: "+kind);
            if(kind is "saved" or "ass" or "off")
            {
                Wait(session.FetchAsync());check(requests==1,"Manual fetch remains available: "+kind);
            }
        }
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingService=new PastLogService(new PastLogCache(Path.Combine(root,"auto-arriving")),()=>new(new FetchHandler(async token=>
        {
            ready.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,token);throw new Exception("Unexpected request completion");}
            finally{canceled.TrySetResult();}
        })),TimeSpan.Zero);
        using var pending=new PlaybackCommentSession("mock.ts",new(),(_,_,_)=>{},_=>{},_=>{},pendingService);
        Field(pending,"context",new CommentMediaContext(recording,"jk1",true,new(),new(),null,null));Field(pending,"localReady",true);
        var fetch=pending.FetchAsync(automatic:true);Wait(ready.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Call(pending,"OnLive",new LiveCommentBatch(recording,"jk1",new(){LogReader.ForPlayback(chat)!},1,0,false,null));
        Wait(fetch.WaitAsync(TimeSpan.FromSeconds(3)));Wait(canceled.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        check(true,"Arriving NicoJK comments cancel this session's automatic request");
    }
}
