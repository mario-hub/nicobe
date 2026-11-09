using System.Diagnostics;
using System.IO;

namespace NicoBE;

// Event-driven while a TS is open. The sparse rescan recovers missed filesystem notifications.
internal sealed class LiveCommentSession : IDisposable
{
    readonly object gate=new();
    readonly string file;
    readonly Config config;
    readonly Action<LiveCommentBatch> publish;
    readonly Action<string> error;
    readonly CancellationTokenSource cancellation;
    readonly System.Threading.Timer work,recovery;
    readonly FileSystemWatcher tsWatcher;
    readonly Stopwatch clock=Stopwatch.StartNew();
    FileSystemWatcher? logWatcher;
    LiveCommentSource? source;
    string? logWatchPath;
    bool stopped,running,dirty,rescan,watchFailed;
    long lastStart=-1000;

    public LiveCommentSession(string file,Config config,Action<LiveCommentBatch> publish,Action<string> error,CancellationToken token)
    {
        this.file=file;this.config=config;this.publish=publish;this.error=error;
        cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        work=new(_=>Run(),null,Timeout.Infinite,Timeout.Infinite);
        recovery=new(_=>Signal(true),null,Timeout.Infinite,Timeout.Infinite);
        tsWatcher=new(Path.GetDirectoryName(file)!,Path.GetFileName(file)) {NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.FileName};
        tsWatcher.Changed+=(_,_)=>Signal(false);tsWatcher.Created+=(_,_)=>Signal(true);
        tsWatcher.Renamed+=(_,_)=>Signal(true);tsWatcher.Deleted+=(_,_)=>Signal(true);
        tsWatcher.Error+=(_,_)=>Signal(true);
        try{tsWatcher.EnableRaisingEvents=true;recovery.Change(30000,30000);Signal(true);}
        catch{Dispose();throw;}
    }
    void Signal(bool scan)
    {
        lock(gate)
        {
            if(stopped)return;
            bool pending=dirty;dirty=true;rescan|=scan;
            if(!running && !pending)work.Change((int)Math.Max(0,1000-(clock.ElapsedMilliseconds-lastStart)),Timeout.Infinite);
        }
    }
    void WatchLogs()
    {
        string? path=source!=null && Directory.Exists(source.Folder)?source.Folder:
            Directory.Exists(config.NicoJkLogFolder)?Path.GetFullPath(config.NicoJkLogFolder):null;
        lock(gate)
        {
            if(stopped || !watchFailed && path==logWatchPath)return;
            logWatcher?.Dispose();logWatcher=null;logWatchPath=null;watchFailed=false;
            if(path==null)return;
            bool channel=path==source?.Folder;
            var watcher=new FileSystemWatcher(path,channel?"*.txt":"*") {
                NotifyFilter=channel?NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.FileName:NotifyFilters.DirectoryName
            };
            watcher.Changed+=(_,_)=>Signal(false);
            watcher.Created+=(_,_)=>Signal(true);watcher.Renamed+=(_,_)=>Signal(true);watcher.Deleted+=(_,_)=>Signal(true);
            watcher.Error+=(_,_)=>{lock(gate){watchFailed=true;}Signal(true);};
            try{watcher.EnableRaisingEvents=true;logWatcher=watcher;logWatchPath=path;rescan=true;}
            catch{watcher.Dispose();throw;}
        }
    }
    void Run()
    {
        bool scan;
        lock(gate)
        {
            if(stopped || running)return;
            running=true;dirty=false;scan=rescan;rescan=false;lastStart=clock.ElapsedMilliseconds;
        }
        try
        {
            var token=cancellation.Token;token.ThrowIfCancellationRequested();
            source??=new LiveCommentSource(TsReader.Read(file,config.ServiceId,token),config);
            WatchLogs();
            lock(gate){scan|=rescan;rescan=false;}
            var batch=source.Read(scan,token);
            token.ThrowIfCancellationRequested();publish(batch);
            if(batch.More)Signal(false);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!cancellation.IsCancellationRequested)error(ex.Message);}
        finally
        {
            lock(gate)
            {
                running=false;
                if(stopped)cancellation.Dispose();
                else if(dirty)work.Change((int)Math.Max(0,1000-(clock.ElapsedMilliseconds-lastStart)),Timeout.Infinite);
            }
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(stopped)return;stopped=true;
            cancellation.Cancel();work.Dispose();recovery.Dispose();tsWatcher.Dispose();logWatcher?.Dispose();logWatcher=null;
            if(!running)cancellation.Dispose();
        }
    }
}
