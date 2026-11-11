namespace NicoBE;

// One instance per player. Uses the existing session tick; no idle timer or disk state.
internal sealed class PlaybackSpeedMemory : IDisposable
{
    readonly Func<double,Task<bool>> send;
    readonly Func<nint,Task<string>> read;
    readonly Action<string> warning;
    readonly MpcPlaybackRate parser=new();
    bool enabled=true,disposed,busy,changing,ended;
    string? file;
    double? remembered,restore;
    double nextRead,lastTick,restoreStarted=double.NaN;
    int revision,attempts,userCommands;
    internal double? Remembered=>remembered;
    internal bool Enabled
    {
        get=>enabled;
        set
        {
            if(enabled==value)return;
            enabled=value;revision++;remembered=restore=null;nextRead=0;attempts=0;
            restoreStarted=double.NaN;parser.Reset();
        }
    }
    internal PlaybackSpeedMemory(Func<double,Task<bool>> send,Action<string> warning,Func<nint,Task<string>>? read=null)
    {this.send=send;this.warning=warning;this.read=read??MpcPlaybackRate.ReadAsync;}
    internal void Reset()
    {
        revision++;file=null;changing=ended=false;remembered=restore=null;nextRead=0;
        attempts=0;restoreStarted=double.NaN;parser.Reset();
    }
    internal void BeginFileChange()
    {
        ended=false;
        if(disposed||changing)return;
        revision++;changing=true;restore=enabled?remembered:null;
        nextRead=0;attempts=0;restoreStarted=double.NaN;
    }
    internal void FileLoaded(string path)
    {
        if(disposed)return;
        if(!changing && string.Equals(file,path,StringComparison.OrdinalIgnoreCase))return;
        if(file!=null && !changing)BeginFileChange();
        revision++;file=path;changing=ended=false;nextRead=0;restoreStarted=double.NaN;
    }
    internal void PlaybackEnded(){BeginFileChange();ended=true;}
    internal void PlaybackResumed()
    {
        // Seeking/replaying the same file after its end is not a file switch.
        if(!ended)return;
        revision++;changing=ended=false;restore=null;nextRead=0;
    }
    // A deliberate panel operation supersedes an automatic restore, even while
    // a previous asynchronous status read/command is still completing.
    internal void UserCommandStarted(){revision++;restore=null;userCommands++;}
    internal void UserCommandFinished(){userCommands--;nextRead=lastTick+.5;}
    internal void UserSpeedChanged(double speed)
    {if(enabled && !disposed && Valid(speed)){remembered=speed;restore=null;}}
    internal async Task TickAsync(nint player,double now)
    {
        lastTick=now;
        if(disposed||!enabled||changing||file==null||player==0||busy||userCommands!=0||now<nextRead)return;
        busy=true;nextRead=now+.5;int mine=revision;
        try
        {
            string text=await read(player);
            if(disposed||!enabled||mine!=revision)return;
            double? actual=parser.Parse(text);
            if(restore is not {} target)
            {if(actual is {} value)remembered=value;return;}
            if(actual==null && attempts==0)return;
            if(double.IsNaN(restoreStarted))restoreStarted=now;
            if(actual is {} current && (Same(current,target)||!Same(current,1)))
            {
                // A non-default rate different from ours can be a user change
                // made in MPC-BE after opening the new file. Respect that value.
                remembered=current;restore=null;return;
            }
            if(now-restoreStarted>=5 || attempts>=3)
            {
                restore=null;if(actual is {} value)remembered=value;
                warning("再生速度を引き継げませんでした。MPC-BEの再生速度を確認してください。");return;
            }
            // A parsed playing/paused rate proves the new media is ready. Avoid
            // sending commands against loading/error/menu-hint status text.
            if(actual==null)return;
            attempts++;
            bool sent=await send(target);
            if(disposed||!enabled||mine!=revision)return;
            if(!sent)nextRead=now+1;
            // Verify on the next read: delivery is not proof of a changed rate.
        }
        catch(Exception ex)
        {
            if(!disposed && mine==revision)
            {restore=null;nextRead=now+1;warning("再生速度の確認に失敗しました: "+ex.Message);}
        }
        finally{busy=false;}
    }
    static bool Valid(double value)=>double.IsFinite(value)&&value>0;
    static bool Same(double a,double b)=>Math.Abs(a-b)<.005;
    public void Dispose(){disposed=true;Reset();}
}
