using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace NicoBE;

public sealed class PastLogHttpException(string message,DateTimeOffset? retry=null,bool permanent=false):IOException(message)
{public DateTimeOffset? Retry {get;}=retry;public bool Permanent {get;}=permanent;}

public sealed class PastLogClient:IDisposable
{
    public const int MaxBytes=64*1024*1024;
    readonly HttpClient client;
    public PastLogClient(HttpMessageHandler? handler=null)
    {
        client=new(handler ?? (ReleaseTestEnvironment.Root!=null ? new ReleaseTestEnvironment.OfflineHandler() : new HttpClientHandler{AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate}));
        client.Timeout=TimeSpan.FromSeconds(60);
        string version=typeof(Config).Assembly.GetName().Version?.ToString(3)??"1.0.2";
        string product=typeof(Config).Assembly.GetName().Name?.Contains("Encoder")==true?"NicoBE-Encoder":"NicoBE";
        client.DefaultRequestHeaders.UserAgent.ParseAdd(product+"/"+version+" (+https://github.com/mario-hub/nicobe)");
    }
    public async Task<List<SourceComment>> GetAsync(string channel,PastLogRange range,CancellationToken token)
    {
        PastLogCache.CheckChannel(channel);
        long end=Math.Min((range.End+999999)/1000000,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        string url=$"https://jikkyo.tsukumijima.net/api/kakolog/{channel}?starttime={range.Start/1000000}&endtime={end}&format=xml";
        using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)
        {
            var retry=response.Headers.RetryAfter;
            DateTimeOffset? until=retry?.Date??(retry?.Delta is {} delta?DateTimeOffset.UtcNow+delta:null);
            if(response.StatusCode==HttpStatusCode.TooManyRequests&&until==null)until=DateTimeOffset.UtcNow.AddMinutes(5);
            bool permanent=(int)response.StatusCode is >=400 and <500 && response.StatusCode!=HttpStatusCode.TooManyRequests && response.StatusCode!=HttpStatusCode.RequestTimeout;
            throw new PastLogHttpException($"過去ログAPI: HTTP {(int)response.StatusCode}",until,permanent);
        }
        if(response.Content.Headers.ContentLength>MaxBytes)throw new InvalidDataException("過去ログAPIの応答が64 MiBを超えています。");
        using var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);using var output=new MemoryStream();
        byte[] buffer=new byte[65536];int size;
        while((size=await input.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
        {if(output.Length+size>MaxBytes)throw new InvalidDataException("過去ログAPIの応答が64 MiBを超えています。");await output.WriteAsync(buffer.AsMemory(0,size),token).ConfigureAwait(false);}
        try{return PastLogCache.Parse(output.ToArray(),token);}
        catch(System.Xml.XmlException ex){throw new InvalidDataException("過去ログAPIのXMLを読み取れません。キャッシュは更新していません。",ex);}
    }
    public void Dispose()=>client.Dispose();
}

public sealed record PastLogResult(PastLogSnapshot Snapshot,string? Warning,DateTimeOffset RetryUtc,bool Permanent=false);
public sealed class PastLogService
{
    public static PastLogService Shared {get;}=new(new PastLogCache(StorageLayout.PastLogCacheDirectory));
    public PastLogCache Cache {get;}
    readonly Func<PastLogClient> clientFactory;
    readonly TimeSpan requestGap;
    readonly object gate=new();
    readonly Dictionary<string,Job> jobs=new(StringComparer.Ordinal);
    public event Action<string>? Changed;
    sealed class Job
    {
        public readonly CancellationTokenSource Cancellation=new();
        public Task<PastLogResult> Task=null!;
        public int Users;
    }
    sealed record Schedule(DateTimeOffset NextUtc,int Failures=0,bool Permanent=false,string? Error=null);
    public PastLogService(PastLogCache cache,Func<PastLogClient>? factory=null,TimeSpan? gap=null)
    {Cache=cache;clientFactory=factory??(()=>new());requestGap=gap??TimeSpan.FromSeconds(5);}
    public async Task<PastLogResult> FetchAsync(string channel,PastLogRange range,long zero,bool force,CancellationToken token,long? priority=null)
    {
        PastLogCache.CheckChannel(channel);
        range=range with{End=Math.Min(range.End,CommentSources.Microseconds(DateTimeOffset.UtcNow))};
        if(!range.Valid)throw new InvalidDataException("過去ログの取得時間を確定できません。");
        string key=$"{channel}:{range.Start}:{range.End}:{zero}:{force}";Job job;
        lock(gate)
        {
            if(!jobs.TryGetValue(key,out job!)||job.Cancellation.IsCancellationRequested)
            {
                job=new();jobs[key]=job;
                job.Task=Task.Run(()=>FetchCore(channel,range,zero,force,job.Cancellation.Token,priority));
                var captured=job;
                _=job.Task.ContinueWith(_=>{lock(gate){if(jobs.GetValueOrDefault(key)==captured)jobs.Remove(key);if(captured.Users==0)captured.Cancellation.Dispose();}},TaskScheduler.Default);
            }
            job.Users++;
        }
        try{return await job.Task.WaitAsync(token).ConfigureAwait(false);}
        finally
        {
            lock(gate)
            {job.Users--;if(job.Users==0){if(!job.Task.IsCompleted)job.Cancellation.Cancel();else job.Cancellation.Dispose();}}
        }
    }
    public DateTimeOffset NextAllowed(string channel)
    {return ReadSchedule(Path.Combine(Cache.Root,"control",channel+".json")).NextUtc;}
    async Task<PastLogResult> FetchCore(string channel,PastLogRange range,long zero,bool force,CancellationToken token,long? priority)
    {
        string control=Path.Combine(Cache.Root,"control");Directory.CreateDirectory(control);
        string statePath=Path.Combine(control,channel+".json");
        string? warning=null;DateTimeOffset retryUtc=default;bool permanent=false,ownsCooldown=false;
        var snapshot=Cache.Read(channel,range,zero,token);
        var missing=force?new List<PastLogRange>{range}:snapshot.Missing(range,true);
        if(priority is {} point)
        {
            long block=point/PastLogCache.Block*PastLogCache.Block;
            missing=missing.SelectMany(r=>new[]{new PastLogRange(r.Start,Math.Min(r.End,block)),
                new(Math.Max(r.Start,block),Math.Min(r.End,block+PastLogCache.Block)),new(Math.Max(r.Start,block+PastLogCache.Block),r.End)})
                .Where(r=>r.Valid).OrderBy(r=>r.Start<=point&&r.End>point?0:1).ThenBy(r=>r.Start).ToList();
        }
        using var client=clientFactory();
        foreach(var gap in missing)
        for(long begin=gap.Start;begin<gap.End;)
        {
            token.ThrowIfCancellationRequested();long end=Math.Min(begin+60*PastLogCache.Minute,gap.End);
            var request=new PastLogRange(begin,end);begin=end;
            using var channelLock=await LockAsync(Path.Combine(control,channel+".lock"),token).ConfigureAwait(false);
            var existing=Cache.Read(channel,request,zero,token);
            if(!force&&existing.Stable)continue;
            var now=DateTimeOffset.UtcNow;var state=ReadSchedule(statePath);
            bool recent=request.End>CommentSources.Microseconds(now.AddMinutes(-15));
            if(state.Permanent&&!force){warning=state.Error;permanent=true;break;}
            if(!ownsCooldown&&state.NextUtc>now&&(recent||force||state.Failures>0))
            {retryUtc=state.NextUtc;warning=state.Error;break;}
            FileStream? network=null;
            try
            {
                while(true)
                {
                    network=await LockAsync(Path.Combine(control,"network.lock"),token).ConfigureAwait(false);
                    var global=ReadSchedule(Path.Combine(control,"network.json"));
                    if(global.NextUtc<=DateTimeOffset.UtcNow)break;
                    network.Dispose();network=null;
                    if(global.NextUtc-DateTimeOffset.UtcNow>TimeSpan.FromSeconds(30)){retryUtc=global.NextUtc;break;}
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((global.NextUtc-DateTimeOffset.UtcNow).TotalMilliseconds,50,30000)),token).ConfigureAwait(false);
                }
                if(network==null)break;
                now=DateTimeOffset.UtcNow;
                // Persist before sending; another process/restart cannot reset the recent-range cooldown.
                if(recent||force){PastLogCache.AtomicJson(statePath,new Schedule(now.AddMinutes(5)));ownsCooldown=true;}
                var comments=await client.GetAsync(channel,request,token).ConfigureAwait(false);
                Cache.Save(channel,request,comments,DateTimeOffset.UtcNow,token);
                PastLogCache.AtomicJson(statePath,new Schedule(recent||force?now.AddMinutes(5):default));
                retryUtc=recent||force?now.AddMinutes(5):retryUtc;
                if(Changed is {} handlers)foreach(Action<string> handler in handlers.GetInvocationList())
                    try{handler(channel);}catch(InvalidOperationException){}
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex) when(ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                int failures=state.Failures+1;permanent=ex is PastLogHttpException{Permanent:true};
                retryUtc=DateTimeOffset.UtcNow.AddMinutes(Math.Min(30,5*Math.Pow(2,Math.Min(failures-1,3))));
                if(ex is PastLogHttpException{Retry:{} specified} && specified>retryUtc)retryUtc=specified;
                warning=ex is OperationCanceledException?"過去ログAPIの通信がタイムアウトしました。":ex.Message;
                PastLogCache.AtomicJson(statePath,new Schedule(retryUtc,failures,permanent,warning));
                if(ex is PastLogHttpException {Retry:not null})PastLogCache.AtomicJson(Path.Combine(control,"network.json"),new Schedule(retryUtc));
                break;
            }
            finally
            {
                if(network!=null)
                {
                    try
                    {
                        string path=Path.Combine(control,"network.json");var global=ReadSchedule(path);var next=DateTimeOffset.UtcNow+requestGap;
                        if(global.NextUtc<next)PastLogCache.AtomicJson(path,new Schedule(next));
                    }
                    finally{network.Dispose();}
                }
            }
        }
        return new(Cache.Read(channel,range,zero,token),warning,retryUtc,permanent);
    }
    static Schedule ReadSchedule(string path)
    {
        try{return File.Exists(path)?JsonSerializer.Deserialize<Schedule>(File.ReadAllText(path))??new(default):new(default);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){return new(default);}
    }
    static async Task<FileStream> LockAsync(string path,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException ex) when((ex.HResult&0xffff) is 32 or 33){await Task.Delay(500,token).ConfigureAwait(false);}
        }
    }
}
