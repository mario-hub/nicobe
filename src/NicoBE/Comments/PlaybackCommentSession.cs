using System.IO;
using System.Windows.Threading;

namespace NicoBE;

internal sealed record PanelCommentState(bool Master,bool Auto,bool SavedAvailable,bool PastAvailable,bool AssAvailable,
    bool Saved,bool Past,bool Ass,bool Eligible,bool Busy,bool CanFetch,string Status,string Detail,bool CanRefresh)
{
    public bool EmbeddedAvailable {get;init;}
    public bool Embedded {get;init;}
}

internal sealed class PlaybackCommentSession:IDisposable
{
    readonly string file;
    readonly Config config;
    readonly Dispatcher dispatcher;
    readonly Action<IReadOnlyList<Comment>,bool,bool> display;
    readonly Action<PanelCommentState> state;
    readonly Action<AssSubtitle?> subtitle;
    readonly PastLogService service;
    readonly Func<double>? position;
    readonly Func<string,Config,CancellationToken,CommentMediaContext> loadContext;
    readonly CancellationTokenSource lifetime=new();
    readonly Dictionary<string,SourceComment> saved=new(StringComparer.Ordinal);
    // Published indexes are immutable; background work may keep an older snapshot.
    Dictionary<string,List<SourceComment>> apiMatches=new(StringComparer.Ordinal);
    Task liveTail=Task.CompletedTask;
    CancellationTokenSource? renderCancellation;
    int renderRevision,rendering;
    readonly CommentSourceSelection selection;
    internal CommentSourceSelection Selection=>selection;
    readonly DispatcherTimer autoTimer;
    CommentMediaContext? context;
    PastLogSnapshot? past;
    LiveCommentSession? live;
    FileSystemWatcher? watcher;
    CancellationTokenSource? autoRequest;
    bool disposed,busy,permanent,cacheReading,cacheDirty,sceneReady,sceneHadComments,resetRequested;
    bool savedAvailable,embeddedAvailable,pastAvailable,localReady;
    string? warning,localWarning;
    DateTimeOffset nextUtc;
    long cacheReadEnd;
    (bool Saved,bool Embedded,bool Past,bool Ass) previous;
    public PlaybackCommentSession(string file,Config config,Action<IReadOnlyList<Comment>,bool,bool> display,
        Action<PanelCommentState> state,Action<AssSubtitle?> subtitle,PastLogService? service=null,CommentSourceSelection? selection=null,Func<double>? position=null,
        Func<string,Config,CancellationToken,CommentMediaContext>? loadContext=null)
    {
        this.file=file;this.config=config;this.display=display;this.state=state;this.subtitle=subtitle;
        this.selection=selection??new();this.position=position;
        this.loadContext=loadContext??CommentMediaContext.Load;
        this.service=service??PastLogService.Shared;dispatcher=Dispatcher.CurrentDispatcher;
        autoTimer=new DispatcherTimer(DispatcherPriority.Background,dispatcher);autoTimer.Tick+=(_,_)=>{autoTimer.Stop();_=FetchAsync(false,true);};
        this.service.Changed+=CacheChanged;
    }
    public async Task StartAsync()
    {
        Publish();
        try
        {
            var loaded=await Task.Run(()=>loadContext(file,config.Clone(),lifetime.Token),lifetime.Token);
            if(disposed)return;
            bool hasEmbedded=await Task.Run(()=>loaded.Embedded.Any(c=>{lifetime.Token.ThrowIfCancellationRequested();return LogReader.ForPlayback(c)!=null;}),lifetime.Token);
            if(disposed)return;
            embeddedAvailable=hasEmbedded;
            context=loaded;
            localReady=!context.Growing;
            await AddSavedAsync(context.Saved);if(disposed)return;subtitle(context.Subtitle);
            if(context.Eligible){WatchCache();await ReadCacheAsync();}
            // Reading the initial cache may already have published the scene.
            // Do not reconstruct it a second time when startup completes.
            if(!sceneReady)await RenderAsync(false);
            Schedule();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!disposed){warning=ex.Message;Publish();}}
        if(disposed)return;
        if(Path.GetExtension(file).ToLowerInvariant() is ".ts" or ".m2ts" or ".mts")
        {
            live=new(file,config.Clone(),batch=>dispatcher.BeginInvoke(()=>OnLive(batch)),message=>dispatcher.BeginInvoke(()=>
            {if(!disposed){localWarning=message;localReady=true;Schedule();Publish();}}),lifetime.Token);
        }
    }
    long Zero=>context?.Recording is {} r?CommentSources.Microseconds(r.Start):0;
    async Task AddSavedAsync(IEnumerable<SourceComment> values)
    {
        long zero=Zero;var token=lifetime.Token;
        var prepared=await Task.Run(()=>values.Select(c=>
        {token.ThrowIfCancellationRequested();return (Key:CommentSources.Identity(c,zero),Value:c,Available:LogReader.ForPlayback(c)!=null);}).ToArray(),token);
        if(disposed)return;
        foreach(var c in prepared)if(saved.TryAdd(c.Key,c.Value))savedAvailable|=c.Available;
    }
    void OnLive(LiveCommentBatch batch)
    {
        if(!disposed)liveTail=ApplyLiveAsync(liveTail,batch);
    }
    async Task ApplyLiveAsync(Task previousBatch,LiveCommentBatch batch)
    {
        try
        {
        await previousBatch;if(disposed)return;
        long zero=CommentSources.Microseconds(batch.Recording.Start);var token=lifetime.Token;
        var prepared=await Task.Run(()=>batch.Added.Where(c=>c.Original!=null).Select(c=>
        {token.ThrowIfCancellationRequested();return (Key:CommentSources.Identity(c.Original!,zero),Comment:c);}).ToArray(),token);
        if(disposed)return;
        bool changed=context?.Recording?.End!=batch.Recording.End;
        context=(context??new(batch.Recording,batch.Channel,true,new(),new(),null,null)) with{Recording=batch.Recording,Channel=batch.Channel,Unavailable=null};
        var newlyAdded=new List<Comment>();
        foreach(var entry in prepared)
        {
            var c=entry.Comment;
            if(saved.TryAdd(entry.Key,c.Original!)){newlyAdded.Add(c);savedAvailable=true;}
        }
        // Wait for the initial chunked scan; a slow log read must not trigger an API request.
        localReady|=!batch.More;
        Schedule();
        localWarning=batch.Warning;
        if(watcher==null)WatchCache();
        // Do not rebuild all retained comments on each TS write notification.
        if(newlyAdded.Count>0)
        {
            bool before=selection.Saved;SelectAutomatic();
            var current=(selection.Saved,selection.Embedded,selection.Past,selection.Ass);
            if(rendering>0||!sceneReady||before!=selection.Saved||current!=previous||selection.Embedded)await RenderAsync(false);
            else if(selection.Saved)
            {
                var index=apiMatches;bool usePast=selection.Past;int revision=renderRevision;
                var unique=await Task.Run(()=>newlyAdded.Where(c=>
                {token.ThrowIfCancellationRequested();return !usePast||!index.TryGetValue(CommentSources.CrossKey(c.Original!),out var matches)||!matches.Any(a=>CommentSources.Equivalent(c.Original!,a,zero));}).ToList(),token);
                if(disposed)return;
                if(revision!=renderRevision||rendering>0)await RenderAsync(false);
                else if(unique.Count>0){display(unique,false,true);sceneHadComments=true;}
            }
        }
        if(disposed)return;
        if(changed)
        {
            // TS grows every second; only read the new cache time span, not the full retained history.
            _=ReadExtensionAsync();Schedule();
        }
        else Publish();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!disposed){localWarning=ex.Message;Publish();}}
    }
    async Task ReadExtensionAsync()
    {
        if(disposed||context is not {Recording:{} r,Channel:{} channel})return;
        var full=PastLogRange.For(r);
        if(past==null){await ReadCacheAsync();return;}
        past=past with{Range=full};
        // Keep the final cache block in memory. A growing TS does not reread XML on every write.
        if(full.End>cacheReadEnd)await ReadCacheAsync();
        else{Publish();Schedule();}
    }
    void WatchCache()
    {
        if(context?.Channel is not {} channel||watcher!=null)return;
        try
        {
            string path=service.Cache.ChannelDirectory(channel);Directory.CreateDirectory(path);
            watcher=new(path,"*.json"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
            watcher.Changed+=(_,_)=>CacheChanged(channel);watcher.Created+=(_,_)=>CacheChanged(channel);watcher.Renamed+=(_,_)=>CacheChanged(channel);
            watcher.Error+=(_,_)=>CacheChanged(channel);watcher.EnableRaisingEvents=true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){warning="キャッシュを利用できません: "+ex.Message;}
    }
    void CacheChanged(string channel)
    {
        if(disposed)return;
        dispatcher.BeginInvoke(()=>{if(!disposed&&context?.Channel==channel)_=ReadCacheAsync();});
    }
    async Task ReadCacheAsync()
    {
        if(disposed||context is not {Eligible:true,Recording:{} r,Channel:{} channel})return;
        if(cacheReading){cacheDirty=true;return;}
        cacheReading=true;
        try
        {
            do
            {
                cacheDirty=false;var range=PastLogRange.For(context!.Recording!);long zero=Zero;
                long readEnd=(range.End+PastLogCache.Block-1)/PastLogCache.Block*PastLogCache.Block;
                var result=await Task.Run(()=>service.Cache.Read(channel,new(range.Start,readEnd),zero,lifetime.Token),lifetime.Token);
                if(disposed)return;
                var current=context?.Recording is {} latest?PastLogRange.For(latest):range;
                cacheDirty|=current.End>readEnd;cacheReadEnd=readEnd;
                await SetPastAsync(result with{Range=current});if(disposed)return;
                await RenderAsync(false);Schedule();
            }while(cacheDirty);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!disposed){warning=ex.Message;Publish();}}
        finally{cacheReading=false;}
    }
    bool SavedAvailable=>savedAvailable;
    bool PastAvailable=>pastAvailable;
    bool CanAutoFetch=>config.AutoFetchPastLogs&&localReady&&!SavedAvailable&&!embeddedAvailable&&context?.Ass.Count==0;
    async Task SetPastAsync(PastLogSnapshot value)
    {
        var token=lifetime.Token;
        var prepared=await Task.Run(()=>
        {
            var index=new Dictionary<string,List<SourceComment>>(StringComparer.Ordinal);bool available=false;
            foreach(var c in value.Comments)
            {
                token.ThrowIfCancellationRequested();string key=CommentSources.CrossKey(c);
                if(!index.TryGetValue(key,out var matches))index[key]=matches=new();matches.Add(c);
                if(!available)available=LogReader.ForPlayback(c)!=null;
            }
            return (index,available);
        },token);
        if(disposed)return;
        past=context?.Recording is {} latest?value with{Range=PastLogRange.For(latest)}:value;
        apiMatches=prepared.index;pastAvailable=prepared.available;
    }
    void SelectAutomatic()=>selection.Automatic(SavedAvailable,PastAvailable,context?.Ass.Count>0,past?.Stable==true,config.CommentPreference,embeddedAvailable);
    async Task RenderAsync(bool reset)
    {
        if(disposed)return;SelectAutomatic();
        resetRequested|=reset;
        var current=(selection.Saved,selection.Embedded,selection.Past,selection.Ass);
        // Copy references only on the UI thread; hashing, deduplication, sorting
        // and parsing the whole recording must not stall playback-position polls.
        var local=current.Saved?saved.Values.ToArray():Array.Empty<SourceComment>();
        var embedded=current.Embedded?context?.Embedded??new():new List<SourceComment>();
        var remote=current.Past?past?.Comments??new():new List<SourceComment>();
        var ass=context?.Ass??new();long zero=Zero;int mine=++renderRevision;
        renderCancellation?.Cancel();
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        renderCancellation=cancellation;var token=cancellation.Token;rendering++;
        try
        {
        IReadOnlyList<Comment> comments=await Task.Run(()=>current.Ass?ass:
            CommentSources.Merge(embedded.Count>0?CommentSources.Merge(local,embedded,zero,token):local,remote,zero,token).Select(c=>
            {token.ThrowIfCancellationRequested();return LogReader.ForPlayback(c);}).Where(c=>c!=null).Cast<Comment>().ToList(),token);
        if(disposed||mine!=renderRevision)return;
        // Adding NicoJK/API data changes the future timeline, not the positions
        // of comments already moving in wall time. ASS is a different layout;
        // switching it, a manual source change, or the first data still resets.
        reset|=resetRequested || !sceneReady || current.Ass!=previous.Ass || (!sceneHadComments && comments.Count>0);
        display(comments,reset,false);previous=current;sceneReady=true;
        resetRequested=false;
        sceneHadComments=reset?comments.Count>0:sceneHadComments||comments.Count>0;Publish();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!disposed&&mine==renderRevision){warning=ex.Message;Publish();}}
        finally{rendering--;if(ReferenceEquals(renderCancellation,cancellation))renderCancellation=null;}
    }
    public Task Choose(string source,bool on){if(disposed)return Task.CompletedTask;selection.Choose(source,on);return RenderAsync(true);}
    public void SettingsChanged()
    {
        if(disposed)return;
        Schedule();
        Publish();
    }
    public async Task FetchAsync(bool force=false,bool automatic=false)
    {
        if(disposed||busy||context is not {Eligible:true,Recording:{} recording,Channel:{} channel})return;
        if(automatic&&(!CanAutoFetch||permanent))return;
        if(nextUtc>DateTimeOffset.UtcNow){Schedule();Publish();return;}
        busy=true;warning=null;Publish();autoTimer.Stop();
        using var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if(automatic)autoRequest=request;
        try
        {
            long? priority=position==null?null:Zero+(long)(Math.Max(0,position())*1000000);
            var result=await service.FetchAsync(channel,PastLogRange.For(recording),Zero,force,request.Token,priority);
            if(disposed)return;
            warning=result.Warning;nextUtc=result.RetryUtc;permanent=result.Permanent;
            await ReadCacheAsync();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!disposed){warning=ex.Message;nextUtc=DateTimeOffset.UtcNow.AddMinutes(5);}}
        finally
        {
            busy=false;if(ReferenceEquals(autoRequest,request))autoRequest=null;
            if(!disposed){Publish();Schedule();}
        }
    }
    void Schedule()
    {
        autoTimer.Stop();
        if(!CanAutoFetch){autoRequest?.Cancel();return;}
        if(disposed||busy||permanent||context?.Eligible!=true||past?.Stable==true)return;
        var shared=service.NextAllowed(context.Channel!);if(shared>nextUtc)nextUtc=shared;
        autoTimer.Interval=TimeSpan.FromMilliseconds(Math.Clamp((nextUtc-DateTimeOffset.UtcNow).TotalMilliseconds,1000,TimeSpan.FromDays(1).TotalMilliseconds));
        autoTimer.Start();
    }
    public void Publish()
    {
        if(disposed)return;
        bool eligible=context?.Eligible==true,waiting=nextUtc>DateTimeOffset.UtcNow;
        string text=context==null?(warning!=null?"読取失敗":"確認中"):!eligible?"対象外":busy?"取得中…":warning!=null?"取得失敗":
            past?.Stable==true?(PastAvailable?"取得済み":"コメントなし"):past?.Complete==true?(PastAvailable?"取得済み（更新待ち）":"コメントなし（更新待ち）"):
            past?.ContiguousEnd>past?.Range.Start?DateTimeOffset.FromUnixTimeMilliseconds(past!.ContiguousEnd/1000).ToLocalTime().ToString("MM/dd HH:mm")+"まで取得済み":past?.Coverage.Count>0?"一部取得済み":"未取得";
        if(waiting&&!busy&&eligible)text+=" · 再取得待ち";
        string detail=warning??context?.Unavailable??"自動取得はNicoJK・動画内・外部ASSの実況コメントがない場合のみ。全プレイヤー共通です。";
        if(warning!=null&&past!=null)detail+="\nキャッシュ: "+(past.Complete?"対象時間帯を取得済み":past.Coverage.Count>0?"一部取得済み":"未取得");
        if(localWarning!=null)detail+="\n保存ログ: "+localWarning;
        if(waiting)detail+="\n次回取得可能: "+nextUtc.ToLocalTime().ToString("HH:mm:ss");
        state(new(config.CommentsVisible,config.AutoFetchPastLogs,SavedAvailable,PastAvailable,context?.Ass.Count>0,
            selection.Saved,selection.Past,selection.Ass,eligible,busy,eligible&&!busy&&!waiting&&past?.Stable!=true,text,detail,eligible&&!busy&&!waiting)
            {EmbeddedAvailable=embeddedAvailable,Embedded=selection.Embedded});
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;autoTimer.Stop();service.Changed-=CacheChanged;watcher?.Dispose();live?.Dispose();
        lifetime.Cancel();renderRevision++;saved.Clear();apiMatches=new(StringComparer.Ordinal);past=null;context=null;
    }
}
