using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NicoBE;

public sealed class MainWindow : Window
{
    readonly Config config;
    readonly Stopwatch wall=Stopwatch.StartNew();
    readonly TextBlock status=new(),fileLabel=new(),details=new(),position=new();
    readonly CheckBox showComments=new() {Content="コメントを表示",IsChecked=true,FontSize=16,Margin=new Thickness(0,0,18,0)};
    readonly DispatcherTimer poll;
    readonly CommentEngine engine;
    readonly OverlayWindow overlay;
    readonly PlaybackPanel playbackPanel;
    readonly PlaybackSpeedMemory speedMemory;
    MpcClient? mpc;
    readonly SubtitleControl subtitles;
    CancellationTokenSource? preparation;
    PlaybackCommentSession? commentSession;
    double lastPanelUpdate;
    int generation;
    string? currentFile;
    double lastPositionWall=double.NegativeInfinity,lastStats;
    bool closing;
    readonly HashSet<string> reportedErrors=new();
    int gpuRecoveryAttempts;
    double nextGpuRecovery,gpuHealthySince;
    internal event Action<string>? Failure;
    internal event Action<nint>? SettingsRequested;
    internal event Action<string,bool>? CommonSettingRequested;
    void ReportFailure(string category,string message)
    {
        status.Text=message;
        if(!reportedErrors.Add(category))return;
        TraceLog.Write(category+" "+message);
        Failure?.Invoke($"{SessionTitle} (MPC-BE PID {PlayerId})\n{message}");
    }
    bool? knownPlaying;
    double? inferencePosition;
    double lastInferredMovement;
    public int? PlayerId=>mpc?.PlayerProcessId;
    public string SessionTitle=>currentFile==null?"ファイル未選択":Path.GetFileName(currentFile);
    internal bool CommentsReady=>engine.Ready;
    internal NgLogSnapshot NgSnapshot()=>new(SessionTitle,engine.MediaPosition,engine.Ready,engine.NearbyComments(engine.MediaPosition));
    internal double MediaPosition=>engine.MediaPosition;
    internal bool Playing=>engine.Playing;
    internal IReadOnlyList<ActiveComment> VisibleComments=>engine.Active;
    internal bool Control(uint command,string value="")=>mpc?.Send(command,value)??false;
    public bool CommentsVisible
    {
        get=>showComments.IsChecked==true;
        set
        {
            bool changed=CommentsVisible!=value;
            showComments.IsChecked=value;
            if(closing)return;
            // A tray click must update the native overlay immediately, including
            // while playback is paused or MPC-BE does not have foreground focus.
            if(changed && value)engine.RefreshSettings(force:true);
            PublishScene();
        }
    }
    public MainWindow(Config config)
    {
        this.config=config;engine=new(config);overlay=new(engine,config);
        playbackPanel=new((command,value)=>mpc?.SendControlAsync(command,value)??Task.FromResult(false),()=>SettingsRequested?.Invoke(mpc?.PlayerWindow??0)) {Enabled=config.PlaybackPanelVisible};
        speedMemory=new(speed=>mpc?.SendControlAsync(MpcClient.SetSpeed,speed.ToString("0.########",CultureInfo.InvariantCulture))??Task.FromResult(false),
            message=>ReportFailure("playback-speed",message)) {Enabled=config.KeepPlaybackSpeed};
        playbackPanel.SpeedChanging+=speedMemory.UserCommandStarted;
        playbackPanel.SpeedChanged+=speedMemory.UserSpeedChanged;
        playbackPanel.SpeedChangeFinished+=speedMemory.UserCommandFinished;
        subtitles=new((command,value)=>mpc?.Send(command,value)??false);
        WireCommentPanel();
        CommentsVisible=config.CommentsVisible;
        Title="NicoBE — MPC-BE 実況コメント";Width=760;Height=490;MinWidth=650;MinHeight=440;
        FontFamily=new FontFamily("Yu Gothic UI");FontSize=16;Background=new SolidColorBrush(Color.FromRgb(245,247,250));Icon=AppAssets.WindowIcon;
        var root=new StackPanel {Margin=new Thickness(24)};Content=root;
        root.Children.Add(new TextBlock {Text="NicoBE",FontSize=28,FontWeight=FontWeights.Bold});
        var controls=new StackPanel {Orientation=Orientation.Horizontal};controls.Children.Add(showComments);
        controls.Children.Add(new TextBlock {Text="再生速度",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
        var speed=new ComboBox {Width=95,FontSize=16,ItemsSource=PlaybackPanelWindow.Speeds.Select(s=>s.ToString("0.0#",CultureInfo.InvariantCulture)+"×").ToArray(),SelectedIndex=1};
        speed.SelectionChanged+=async (_,_)=>{if(speed.SelectedIndex>=0)await playbackPanel.ChangeSpeedAsync(PlaybackPanelWindow.Speeds[speed.SelectedIndex]);};
        controls.Children.Add(speed);root.Children.Add(controls);
        position.Margin=new Thickness(0,14,0,8);root.Children.Add(position);
        status.Text="待機中";status.FontWeight=FontWeights.SemiBold;status.TextWrapping=TextWrapping.Wrap;root.Children.Add(status);
        details.FontSize=14;details.TextWrapping=TextWrapping.Wrap;details.Margin=new Thickness(0,8,0,0);
        details.Text=$"設定: {Paths.Config}\n描画速度: {config.ScrollPixelsPerSecond:0} px/秒（再生倍率とは独立）";root.Children.Add(details);
        poll=new DispatcherTimer(TimeSpan.FromMilliseconds(config.PollIntervalMs),DispatcherPriority.Background,Poll,Dispatcher);
        SourceInitialized+=(_,_)=>
        {
            mpc=new(new WindowInteropHelper(this).Handle,Dispatcher);mpc.Message+=OnMessage;
        };
        Closing+=(_,e)=>{if(!closing){e.Cancel=true;Hide();}};
        Closed+=(_,_)=>
        {
            closing=true;StopLiveComments();preparation?.Cancel();preparation?.Dispose();preparation=null;poll.Stop();
            speedMemory.Dispose();playbackPanel.Dispose();subtitles.Restore();engine.Reset();overlay.Close();currentFile=null;
            if(mpc!=null){mpc.Message-=OnMessage;mpc.Dispose();mpc=null;}
        };
    }
    public void InitializeHidden()=>new WindowInteropHelper(this).EnsureHandle();
    public void AttachTarget(Process target,nint knownWindow=0){InitializeHidden();mpc!.Attach(target,knownWindow);}
    public void ShowDetails(){if(closing)return;Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();}
    public void ShutdownSession(){closing=true;Close();}
    public void ApplySettings(bool reloadLogs)
    {
        if(closing)return;
        playbackPanel.Enabled=config.PlaybackPanelVisible;
        speedMemory.Enabled=config.KeepPlaybackSpeed;
        CommentsVisible=config.CommentsVisible;
        overlay.RefreshFont();
        engine.RefreshSettings();
        commentSession?.SettingsChanged();
        if(reloadLogs && currentFile!=null)_=PrepareCommentsAsync(currentFile);
        else PublishScene();
    }
    async Task PrepareCommentsAsync(string file)
    {
        file=Path.GetFullPath(file);var selected=currentFile==file?commentSession?.Selection:null;
        overlay.Follow(mpc?.PlayerWindow??0,false);
        int mine=++generation;engine.Reset();lastPositionWall=double.NegativeInfinity;
        reportedErrors.Clear();StopLiveComments();
        if(currentFile==file)subtitles.Restore();subtitles.Reset();
        currentFile=file;PublishScene();
        if(!File.Exists(file)||!new[]{".ts",".m2ts",".mts",".mp4",".mkv"}.Contains(Path.GetExtension(file).ToLowerInvariant()))return;
        if(knownPlaying.HasValue)engine.SetPlaying(knownPlaying.Value,wall.Elapsed.TotalSeconds);
        var session=new PlaybackCommentSession(file,config,(comments,reset,append)=>
        {
            if(closing||mine!=generation)return;
            if(append)engine.AppendComments(comments);else if(reset)engine.SetComments(comments);else engine.UpdateComments(comments);
            PublishScene();
        },value=>
        {
            if(closing||mine!=generation)return;
            if(subtitles.CommentDisplayedByPlayer)value=value with{Status="MPC-BEで実況字幕を表示中",Detail=value.Detail+"\n二重表示を避けるためNicoBEの描画を保留しています。"};
            else if(subtitles.AwaitingSuppression)value=value with{Status="字幕の切り替え待ち"};
            playbackPanel.SetCommentState(value);status.Text=value.Status;details.Text=value.Detail;
        },subtitles.SetSource,selection:selected,position:()=>engine.MediaPosition);
        commentSession=session;await session.StartAsync();
    }
    void OnMessage(uint code,string value)
    {
        if(closing)return;
        double now=wall.Elapsed.TotalSeconds;
        switch(code)
        {
            case MpcClient.SubtitleTracks:
                subtitles.Receive(value);break;
            case MpcClient.Connect:
                speedMemory.Reset();
                playbackPanel.Reset();
                knownPlaying=null;inferencePosition=null;lastInferredMovement=now;engine.SetPlaying(false,now);
                TraceLog.Write("connected "+value);mpc!.Send(MpcClient.GetNowPlaying);mpc.Send(MpcClient.GetPosition);break;
            case MpcClient.PlayMode:
                if(value=="0")speedMemory.PlaybackResumed();
                knownPlaying=value=="0";engine.SetPlaying(knownPlaying.Value,now);TraceLog.Write($"playmode pid={PlayerId} "+value);break;
            case MpcClient.Position:
                if(double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double pos))
                {
                    // Attach does not report the existing play state. Infer only until
                    // the first mode notification, without issuing play/pause commands.
                    if(!knownPlaying.HasValue && inferencePosition is {} before)
                    {
                        if(pos>before+.005){lastInferredMovement=now;engine.SetPlaying(true,now);}
                        else if(now-lastInferredMovement>.4)engine.SetPlaying(false,now);
                    }
                    inferencePosition=pos;engine.OnPosition(pos,now);lastPositionWall=now;
                }break;
            case MpcClient.Seek:
                if(double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double seek)) {engine.OnSeek(seek,now);TraceLog.Write($"seek {seek:F3}");}break;
            case MpcClient.NowPlaying:
                var fields=MpcClient.SplitFields(value);
                if(fields.Length>=4 && Path.IsPathFullyQualified(fields[3]) && File.Exists(fields[3]))OnPlayerFile(fields[3]);
                else mpc!.Send(MpcClient.GetPlaylist);
                break;
            case MpcClient.Playlist:
                var entries=MpcClient.SplitFields(value);
                if(entries.Length>1 && int.TryParse(entries[^1],out int index) && index>=0 && index<entries.Length-1 && File.Exists(entries[index]))OnPlayerFile(entries[index]);
                break;
            case MpcClient.State:
                if(value is "0" or "1" or "3")speedMemory.BeginFileChange();
                if(value is "0" or "1" or "3")
                {
                    // Some opens announce Loading before Closing/Closed. Stop
                    // displaying the previous file as soon as loading begins.
                    overlay.Follow(mpc?.PlayerWindow??0,false);generation++;
                    playbackPanel.Reset();StopLiveComments();subtitles.Reset();preparation?.Cancel();
                    currentFile=null;knownPlaying=null;inferencePosition=null;lastPositionWall=double.NegativeInfinity;engine.Reset();
                }
                break;
            case MpcClient.Disconnect:
                speedMemory.Reset();
                playbackPanel.Reset();StopLiveComments();subtitles.Reset();generation++;preparation?.Cancel();currentFile=null;knownPlaying=null;inferencePosition=null;engine.Reset();status.Text="MPC-BEは終了しました。";break;
            case MpcClient.End:
                speedMemory.PlaybackEnded();
                engine.SetPlaying(false,now);engine.Active.Clear();status.Text="再生終了。";break;
        }
        PublishScene();
    }
    void OnPlayerFile(string file)
    {
        speedMemory.FileLoaded(Path.GetFullPath(file));
        if(!string.Equals(Path.GetFullPath(file),currentFile,StringComparison.OrdinalIgnoreCase)){playbackPanel.Reset();_=PrepareCommentsAsync(file);}
    }
    void Poll(object? sender,EventArgs e)
    {
        if(mpc==null)return;
        mpc.Watch();
        _=speedMemory.TickAsync(mpc.PlayerWindow,wall.Elapsed.TotalSeconds);
        // Speed changes can delay position replies; that must not hide the controls.
        playbackPanel.Tick(mpc.PlayerWindow,currentFile!=null,wall.Elapsed.TotalSeconds);
        mpc.Send(MpcClient.GetPosition);
        PublishScene();
        var gpu=overlay.GetStats();var display=overlay.GetDisplayStats();
        position.Text=$"{(engine.Playing?"再生中":"停止 / 一時停止")}　{TimeSpan.FromSeconds(Math.Max(0,engine.MediaPosition)):hh\\:mm\\:ss}　表示 {engine.Active.Count} 件　混雑時省略 {engine.Dropped:N0} 件\nGPU描画 {gpu.Fps:F1} FPS / 目標{display.TargetFps:F2}　文字準備待ち {gpu.Pending} 件";
        double now=wall.Elapsed.TotalSeconds;
        if(gpu.Error!=0)
        {
            gpuHealthySince=now;
            if(gpuRecoveryAttempts<3 && now>=nextGpuRecovery)
            {
                gpuRecoveryAttempts++;nextGpuRecovery=now+5;
                try{overlay.RecreateRenderer();engine.RefreshSettings(force:true);PublishScene();TraceLog.Write("GPU renderer recovered");}
                catch(Exception ex){TraceLog.Write("GPU recovery failed "+ex.Message);}
                if(overlay.GetStats().Error!=0 && gpuRecoveryAttempts==3)
                    ReportFailure("gpu-error",$"GPU描画を復旧できません (0x{gpu.Error:X8})。NicoBEを再起動してください。");
            }
        }
        else if(gpuRecoveryAttempts>0 && now-gpuHealthySince>=30)
        {gpuRecoveryAttempts=0;reportedErrors.Remove("gpu-error");}
        if(now-lastStats>=5 && mpc.Connected)
        {
            lastStats=now;var pacing=overlay.GetPacingStats();TraceLog.Write($"sync pid={PlayerId} media={engine.MediaPosition:F3} motion={engine.MotionSeconds:F3} playing={engine.Playing} active={engine.Active.Count} dropped={engine.Dropped} gpuFps={gpu.Fps:F2} refreshHz={display.RefreshHz:F3} targetFps={display.TargetFps:F3} divisor={display.Divisor} outputSync={pacing.OutputSync} frameMeanMs={pacing.MeanMs:F3} frameP95Ms={pacing.P95Ms:F3} frameMaxMs={pacing.MaxMs:F3} presentBusy={pacing.Busy} cpuMs={gpu.CpuMs:F3} cacheMs={gpu.CacheMs:F3} pending={gpu.Pending} error=0x{gpu.Error:X8}");
        }
    }
    void WireCommentPanel()
    {
        playbackPanel.SourceChanged+=(source,on)=>commentSession?.Choose(source,on);
        playbackPanel.CommonChanged+=(name,on)=>CommonSettingRequested?.Invoke(name,on);
        playbackPanel.FetchRequested+=force=>{if(commentSession!=null)_=commentSession.FetchAsync(force);};
    }
    void StopLiveComments(){commentSession?.Dispose();commentSession=null;}
    void PublishScene()
    {
        double now=wall.Elapsed.TotalSeconds;engine.Tick(now);
        overlay.Redraw(Math.Max(0,.65-(now-lastPositionWall)));
        subtitles.Poll(engine.Ready,now);
        if(now-lastPanelUpdate>=1){lastPanelUpdate=now;commentSession?.Publish();}
        overlay.Follow(mpc?.PlayerWindow??0,engine.Ready && CommentsVisible && !subtitles.CommentDisplayedByPlayer && !subtitles.AwaitingSuppression && now-lastPositionWall<2);
    }
}
