using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace NicoBE;

public sealed class ResidentController : IDisposable
{
    readonly Config config;
    readonly List<MainWindow> sessions=new();
    readonly Forms.NotifyIcon tray;
    readonly Forms.ContextMenuStrip menu;
    readonly Forms.ToolStripMenuItem commentsMenu=new("コメントを表示");
    readonly PlayerWatcher watch;
    readonly DispatcherTimer idleCleanup;
    readonly Dictionary<MainWindow,(Process Process,EventHandler Handler)> exits=new();
    readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
    readonly System.Drawing.Icon icon;
    SettingsWindow? settings;
    readonly Queue<string> diagnostics=new();
    DateTime nextNotification=DateTime.MinValue;
    bool disposed;
    readonly Predicate<Process>? candidateFilter;
    internal IReadOnlyList<MainWindow> Sessions=>sessions;
    internal int IdleCollections {get;private set;}
    internal bool LastIdleTrimSucceeded {get;private set;}
    public ResidentController(Config config,bool showTray=true,Predicate<Process>? candidateFilter=null)
    {
        this.config=config;this.candidateFilter=candidateFilter;
        menu=new Forms.ContextMenuStrip {Font=new System.Drawing.Font("Yu Gothic UI",12)};
        // Populate before the icon becomes visible so the first opening has items.
        BuildMenu();
        icon=File.Exists(AppAssets.IconPath)?new System.Drawing.Icon(AppAssets.IconPath): (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        tray=new Forms.NotifyIcon {Text="NicoBE",Icon=icon,ContextMenuStrip=menu,Visible=showTray};
        tray.MouseClick+=(_,e)=>{if(e.Button==Forms.MouseButtons.Left)ShowSettings();};
        tray.BalloonTipClicked+=(_,_)=>{ShowSettings();settings?.ShowDiagnostics();};
        idleCleanup=new DispatcherTimer(DispatcherPriority.ApplicationIdle) {Interval=TimeSpan.FromSeconds(2)};
        idleCleanup.Tick+=(_,_)=>
        {
            idleCleanup.Stop();
            if(disposed || sessions.Count!=0 || settings!=null)return;
            // Once on entering idle, not a recurring trim. Freed objects are
            // collected first; cold runtime/driver pages can then return to Windows.
            GC.Collect(2,GCCollectionMode.Aggressive,blocking:true,compacting:true);
            LastIdleTrimSucceeded=Native.EmptyWorkingSet(Native.GetCurrentProcess());IdleCollections++;
        };
        watch=new PlayerWatcher(TryAttach);watch.Start();
        ScheduleIdleCleanup();
        if(showTray && !config.SetupCompleted)Dispatcher.CurrentDispatcher.BeginInvoke(()=>ShowSettings(false));
    }
    public void ShowSettings()=>ShowSettings(true);
    void ShowPlayerSettings(nint player)
    {
        ShowSettings(false,player);
        var requested=settings;
        if(requested==null)return;
        // The panel deliberately rejects mouse activation. Finish that input
        // event before explicitly activating the settings requested by the user.
        dispatcher.BeginInvoke(()=>
        {
            if(!disposed && ReferenceEquals(settings,requested) && requested.IsVisible)
                SettingsPlacement.BringToFront(requested,player);
        },DispatcherPriority.Input);
    }
    void ShowSettings(bool nearPointer,nint player=0)
    {
        if(disposed)return;
        if(settings!=null)
        {
            if(settings.WindowState==WindowState.Minimized)settings.WindowState=WindowState.Normal;
            if(player!=0)SettingsPlacement.CenterOnPlayer(settings,player);
            settings.Activate();return;
        }
        settings=new SettingsWindow(config,ApplySettings,readNgLog:()=>sessions.FirstOrDefault(s=>s.PlayerId.HasValue)?.NgSnapshot(),readDiagnostics:()=>
            diagnostics.Count==0?"エラーの記録はありません。\nログ: "+Paths.Logs:string.Join("\n\n",diagnostics.Reverse())+"\n\nログ: "+Paths.Logs);
        if(player!=0)SettingsPlacement.CenterOnPlayer(settings,player);
        else if(nearPointer)SettingsPlacement.NearPointer(settings);
        idleCleanup.Stop();
        settings.Closed+=(_,_)=>{settings=null;ScheduleIdleCleanup();};settings.Show();
    }
    internal void ApplySettings(Config value)
    {
        bool logsChanged=!string.Equals(config.NicoJkLogFolder,value.NicoJkLogFolder,StringComparison.OrdinalIgnoreCase)
            || config.CommentPreference!=value.CommentPreference;
        config.CopyFrom(value);
        commentsMenu.Checked=config.CommentsVisible;
        foreach(var session in sessions.ToArray())session.ApplySettings(logsChanged);
    }
    MainWindow CreateSession()
    {
        idleCleanup.Stop();
        var session=new MainWindow(config);session.Failure+=ReportFailure;session.SettingsRequested+=ShowPlayerSettings;session.InitializeHidden();sessions.Add(session);return session;
    }
    void ReportFailure(string message)
    {
        if(disposed)return;
        diagnostics.Enqueue($"{DateTime.Now:yyyy/MM/dd HH:mm:ss}\n{message}");
        while(diagnostics.Count>20)diagnostics.Dequeue();
        if(DateTime.UtcNow<nextNotification)return;
        nextNotification=DateTime.UtcNow.AddSeconds(30);
        tray.ShowBalloonTip(8000,"NicoBE",message[..Math.Min(message.Length,180)]+"\n設定の「診断」で詳細を確認できます。",Forms.ToolTipIcon.Warning);
    }
    void ScheduleIdleCleanup()
    {
        if(!disposed && sessions.Count==0 && settings==null){idleCleanup.Stop();idleCleanup.Start();}
    }
    void RemoveSession(MainWindow session)
    {
        if(!sessions.Remove(session))return;
        session.Failure-=ReportFailure;
        session.SettingsRequested-=ShowPlayerSettings;
        if(exits.Remove(session,out var subscription))subscription.Process.Exited-=subscription.Handler;
        session.ShutdownSession();ScheduleIdleCleanup();
    }
    bool TryAttach(uint pid,nint window)
    {
        if(disposed)return true;
        if(sessions.Any(s=>s.PlayerId==pid))return true;
        Process? candidate=null;bool retained=false;MainWindow? created=null;
        try
        {
            candidate=Process.GetProcessById(checked((int)pid));
            if(candidateFilter?.Invoke(candidate)==false)return true;
            if(PlayerLocator.Executable(candidate)==null)return true;
            // A CREATE notification can precede the player's command handler.
            // MpcClient retries the handshake for this specific process as needed.
            created=CreateSession();created.AttachTarget(candidate,window);retained=created.PlayerId==candidate.Id;
            if(!retained){RemoveSession(created);return false;}
            var session=created;
            EventHandler exited=(_,_)=>dispatcher.BeginInvoke(()=>{if(!disposed)RemoveSession(session);});
            exits[session]=(candidate,exited);candidate.Exited+=exited;candidate.EnableRaisingEvents=true;
            return true;
        }
        catch(Exception ex)
        {if(created!=null)RemoveSession(created);TraceLog.Write("detect-player "+ex.Message);ReportFailure("MPC-BEとの連携を開始できません。\n"+ex.Message);return false;}
        finally{if(!retained)candidate?.Dispose();}
    }
    void BuildMenu()
    {
        commentsMenu.Checked=config.CommentsVisible;
        commentsMenu.Click+=(_,_)=>
        {
            try
            {
                var candidate=config.Clone();candidate.CommentsVisible=!config.CommentsVisible;candidate.Save();
                config.CommentsVisible=candidate.CommentsVisible;commentsMenu.Checked=config.CommentsVisible;
                foreach(var session in sessions)session.CommentsVisible=config.CommentsVisible;
                settings?.SetCommentsVisible(config.CommentsVisible);
                TraceLog.Write($"comments-visible={config.CommentsVisible} sessions={sessions.Count}");
            }
            catch(Exception ex){MessageBox.Show("コメントの表示設定を保存できませんでした。\n"+ex.Message,"NicoBE",MessageBoxButton.OK,MessageBoxImage.Error);}
        };
        menu.Items.Add(commentsMenu);
        menu.Items.Add("設定",null,(_,_)=>ShowSettings());
        menu.Items.Add("終了",null,(_,_)=>Application.Current.Shutdown());
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;settings?.Close();watch.Dispose();idleCleanup.Stop();tray.Visible=false;tray.Dispose();menu.Dispose();icon.Dispose();
        foreach(var session in sessions.ToArray())RemoveSession(session);
    }
}
