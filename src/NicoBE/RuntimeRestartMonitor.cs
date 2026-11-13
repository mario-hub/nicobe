using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NicoBE;

// One monitor per resident process. No file checks or timer ticks while idle.
internal sealed class RuntimeRestartMonitor : IDisposable
{
    readonly DispatcherTimer timer;
    readonly Action<string> notify;
    readonly Func<string[]> runtimeFiles;
    readonly Func<string,bool> exists;
    string[]? files;
    bool active,notified,disposed,checkFailureLogged;
    internal bool TimerRunning=>timer.IsEnabled;
    internal static readonly TimeSpan Interval=TimeSpan.FromSeconds(30);

    internal RuntimeRestartMonitor(Action<string> notify,Func<string[]>? runtimeFiles=null,Func<string,bool>? exists=null)
    {
        this.notify=notify;this.runtimeFiles=runtimeFiles??LoadedRuntimeFiles;this.exists=exists??File.Exists;
        timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=Interval};
        timer.Tick+=(_,_)=>CheckNow();
    }
    static string[] LoadedRuntimeFiles()=>new[]{typeof(object).Assembly.Location,typeof(Window).Assembly.Location}
        .Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal void SetActive(bool value)
    {
        if(disposed || active==value)return;
        active=value;
        if(!active){timer.Stop();return;}
        CheckNow();
        if(!notified)timer.Start();
    }
    internal void CheckNow()
    {
        if(disposed || !active || notified)return;
        try
        {
            files??=runtimeFiles();
            var missing=files.Where(p=>!exists(p)).ToArray();
            if(missing.Length==0)return;
            notified=true;timer.Stop();
            TraceLog.Write("runtime-restart-required missing="+string.Join("; ",missing));
            notify("NicoBEの再起動が必要です。\n使用中の.NETのファイルが見つかりません（.NET更新など）。NicoBEを終了して起動し直してください。MPC-BEを閉じる必要はありません。\n不足ファイル: "+string.Join("; ",missing));
        }
        catch(Exception ex)
        {
            // A diagnostic failure must not stop player attachment or playback.
            if(!checkFailureLogged){checkFailureLogged=true;TraceLog.Write("runtime-check-failed "+ex);}
        }
    }
    public void Dispose(){if(disposed)return;disposed=true;active=false;timer.Stop();files=null;}
}
