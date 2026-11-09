using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace NicoBE;

// No periodic enumeration. The retry timer runs only while a newly-created
// player window is being initialized, and stops after at most ten seconds.
internal sealed class PlayerWatcher : IDisposable
{
    const uint Create=0x8000,Destroy=0x8001,Show=0x8002;
    readonly Native.WinEventProc callback;
    readonly Func<uint,nint,bool> found;
    readonly DispatcherTimer retry;
    readonly Dictionary<nint,(uint Pid,double Since)> pending=new();
    readonly Dictionary<nint,uint> accepted=new();
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly StringBuilder className=new(64);
    readonly bool measure;
    nint hook;
    bool disposed;
    public long Notifications {get;private set;}
    public long CallbackTicks {get;private set;}
    public int InitialEnumerations {get;private set;}
    public int PendingCount=>pending.Count;
    public bool RetryActive=>retry.IsEnabled;

    public PlayerWatcher(Func<uint,nint,bool> found,bool measure=false)
    {
        this.found=found;this.measure=measure;callback=OnEvent;
        retry=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(250)};
        retry.Tick+=Drain;
        // OUTOFCONTEXT | SKIPOWNPROCESS; register before enumeration to avoid a gap.
        hook=Native.SetWinEventHook(Create,Show,0,callback,0,0,2);
        if(hook==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"MPC-BEの起動通知を登録できません。");
    }
    public void Start()
    {
        if(disposed || InitialEnumerations!=0)return;
        InitialEnumerations++;
        Native.EnumWindows((hwnd,_)=>{Consider(hwnd);return true;},0);
        Drain(null,EventArgs.Empty);
    }
    void OnEvent(nint unused,uint type,nint hwnd,int objectId,int childId,uint threadId,uint time)
    {
        long began=measure?Stopwatch.GetTimestamp():0;
        if(measure)Notifications++;
        try
        {
            if(disposed || hwnd==0 || objectId!=0 || childId!=0)return;
            if(type==Destroy){pending.Remove(hwnd);accepted.Remove(hwnd);if(pending.Count==0)retry.Stop();return;}
            if(type==Create){pending.Remove(hwnd);accepted.Remove(hwnd);}
            Consider(hwnd);
        }
        catch(Exception error){TraceLog.Write("player-notification "+error.Message);}
        finally{if(measure)CallbackTicks+=Stopwatch.GetTimestamp()-began;}
    }
    void Consider(nint hwnd)
    {
        if(accepted.ContainsKey(hwnd) || pending.ContainsKey(hwnd))return;
        className.Clear();
        if(Native.GetClassName(hwnd,className,className.Capacity)==0 || className.ToString()!="MPC-BE" || Native.GetAncestor(hwnd,2)!=hwnd)return;
        Native.GetWindowThreadProcessId(hwnd,out uint pid);
        if(pid==0)return;
        pending[hwnd]=(pid,clock.Elapsed.TotalSeconds);retry.Start();
    }
    void Drain(object? sender,EventArgs args)
    {
        if(disposed)return;
        foreach(var item in pending.ToArray())
        {
            Native.GetWindowThreadProcessId(item.Key,out uint pid);
            if(pid!=item.Value.Pid || !Native.IsWindow(item.Key)){pending.Remove(item.Key);continue;}
            if(found(pid,item.Key)){accepted[item.Key]=pid;pending.Remove(item.Key);}
            else if(clock.Elapsed.TotalSeconds-item.Value.Since>=10)pending.Remove(item.Key);
        }
        if(pending.Count==0)retry.Stop();
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;retry.Stop();retry.Tick-=Drain;
        if(hook!=0){Native.UnhookWinEvent(hook);hook=0;}
        pending.Clear();accepted.Clear();GC.KeepAlive(callback);
    }
}
