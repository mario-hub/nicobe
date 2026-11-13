using System.IO;
using System.Windows;

namespace NicoBE.Encoder;

// Shared by GUI and CLI. Only runs during encoding; notification never cancels a job.
public sealed class EncoderRuntimeWatch : IDisposable
{
    readonly object gate=new();
    readonly Func<string[]> missingFiles;
    readonly Action<string> notify;
    Timer? timer;
    bool active,disposed;
    string? reason;
    public string? Reason {get {lock(gate)return reason;}}
    public bool RestartRequired=>Reason!=null;
    internal bool TimerRunning {get {lock(gate)return timer!=null;}}
    public const string RestartMessage=".NETの更新などで使用中のファイルが見つかりません。現在の1件の完了を試みたあと、次の処理を止めます。NicoBE Encoderを再起動してください。";
    public EncoderRuntimeWatch(Action<string> notify):this(notify,()=>new[]{typeof(object).Assembly.Location,typeof(Window).Assembly.Location}
        .Where(p=>!string.IsNullOrEmpty(p)&&!File.Exists(p)).ToArray()){}
    internal EncoderRuntimeWatch(Action<string> notify,Func<string[]> missingFiles){this.notify=notify;this.missingFiles=missingFiles;}
    public void Start()
    {
        lock(gate)
        {
            if(disposed)return;
            active=true;
            if(reason==null && timer==null)timer=new Timer(_=>CheckNow(),null,TimeSpan.FromSeconds(30),TimeSpan.FromSeconds(30));
        }
        CheckNow();
    }
    public void CheckNow()
    {
        string? message=null;
        lock(gate)
        {
            if(disposed||!active||reason!=null)return;
            try
            {
                var missing=missingFiles();if(missing.Length==0)return;
                reason=message=RestartMessage+"\n不足ファイル: "+string.Join("; ",missing);
                timer?.Dispose();timer=null;
                SafeFileLog.Append(Path.Combine(EncoderPaths.Logs,"encoder-error.log"),$"{DateTimeOffset.Now:O} runtime-restart-required {message}{Environment.NewLine}");
            }
            catch(Exception ex)
            {SafeFileLog.Append(Path.Combine(EncoderPaths.Logs,"encoder-error.log"),$"{DateTimeOffset.Now:O} runtime-check-failed {ex}{Environment.NewLine}");}
        }
        if(message!=null)
        {
            try{notify(message);}
            catch(Exception ex){SafeFileLog.Append(Path.Combine(EncoderPaths.Logs,"encoder-error.log"),"runtime-notification-failed "+ex+Environment.NewLine);}
        }
    }
    public void Stop(){lock(gate){active=false;timer?.Dispose();timer=null;}}
    public void Dispose(){lock(gate){disposed=true;Stop();}}
}
