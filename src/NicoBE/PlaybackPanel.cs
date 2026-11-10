using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace NicoBE;

// One controller per MPC-BE session; uses the session's existing polling tick.
// The WPF window is created only when its activation corner is first entered.
internal sealed class PlaybackPanel : IDisposable
{
    readonly Func<uint,string,Task<bool>> send;
    readonly Action? openSettings;
    readonly PanelHover hover=new();
    PlaybackPanelWindow? window;
    nint owner;
    bool disposed;
    bool enabled=true;
    int generation;
    readonly MpcPlaybackRate playbackRate=new();
    bool readingSpeed;
    int speedRevision,pendingSpeedCommands;
    double nextSpeedRead,lastTick;
    internal bool Enabled
    {
        get=>enabled;
        set{enabled=value;if(!value){generation++;speedRevision++;hover.Reset();window?.Close();window=null;}}
    }
    internal double? SelectedSpeed {get;private set;}
    internal PlaybackPanel(Func<uint,string,Task<bool>> send,Action? openSettings=null){this.send=send;this.openSettings=openSettings;}
    internal PlaybackPanel(Func<uint,string,bool> send,Action? openSettings=null):this((command,value)=>Task.FromResult(send(command,value)),openSettings){}

    internal async Task<bool> ChangeSpeedAsync(double speed)
    {
        if(disposed || !PlaybackPanelWindow.Speeds.Contains(speed))return false;
        int mine=generation;
        speedRevision++;pendingSpeedCommands++;
        try
        {
            if(!await send(MpcClient.SetSpeed,speed.ToString("0.##",CultureInfo.InvariantCulture)) || disposed || mine!=generation)return false;
            SelectedSpeed=speed;window?.SelectSpeed(speed);return true;
        }
        finally{pendingSpeedCommands--;nextSpeedRead=lastTick+.5;}
    }
    internal Task<bool> JumpAsync(int seconds)=>!disposed && PlaybackPanelWindow.Jumps.Contains(seconds)
        ?send(MpcClient.JumpSeconds,seconds.ToString(CultureInfo.InvariantCulture)):Task.FromResult(false);
    internal void Reset()
    {
        generation++;speedRevision++;nextSpeedRead=0;playbackRate.Reset();SelectedSpeed=null;window?.CancelPending();window?.SelectSpeed(null);Hide();
    }
    void Hide(){hover.Reset();if(window?.IsVisible==true)window.Hide();}
    internal void Tick(nint player,bool loaded,double now)
    {
        if(disposed || !enabled)return;
        if(!GetCursorPos(out var cursor)){Hide();return;}
        TickAt(player,loaded,now,cursor,WindowFromPoint(cursor));
    }
    // Pointer inputs can be supplied by the off-screen regression test without
    // moving the user's cursor or activating any application.
    internal void TickAt(nint player,bool loaded,double now,Native.Point cursor,nint hit)
    {
        if(disposed)return;
        if(!enabled)return;
        lastTick=now;
        if(player!=owner){window?.Close();window=null;owner=player;Reset();}
        if(!loaded || player==0 || !Native.IsWindow(player) || !Native.IsWindowVisible(player)
            || Native.IsIconic(player) || !IsWindowEnabled(player)){Hide();return;}
        nint video=Native.GetDlgItem(player,59648);var origin=new Native.Point();
        if(video==0 || !Native.GetClientRect(video,out var client) || !Native.ClientToScreen(video,ref origin)){Hide();return;}
        double scale=Math.Max(96,GetDpiForWindow(player))/96d;
        var viewport=new Rect(origin.X,origin.Y,client.Width,client.Height);
        // Keep buttons readable; very small video surfaces cannot contain the panel.
        if(viewport.Width<320*scale || viewport.Height<210*scale){Hide();return;}
        var pointer=new Point(cursor.X,cursor.Y);
        var corner=ActivationBounds(viewport,scale);
        bool inPanel=window?.IsVisible==true && GetWindowRect(window.Handle,out var panelRect)
            && new Rect(panelRect.Left,panelRect.Top,panelRect.Width,panelRect.Height).Contains(pointer);
        // Do not react through an unrelated window covering the player.
        nint root=Native.GetAncestor(hit,2);
        bool overPlayer=root==player || root==window?.Handle || IsCommentOverlay(root,player);
        if(!hover.Update(overPlayer && corner.Contains(pointer),overPlayer && inPanel,now))
        {if(window?.IsVisible==true)window.Hide();return;}
        window??=new PlaybackPanelWindow(ChangeSpeedAsync,JumpAsync,()=>openSettings?.Invoke());
        window.SelectSpeed(SelectedSpeed);
        window.Place(player,viewport,scale);
        if(window.IsVisible && now>=nextSpeedRead && !readingSpeed && pendingSpeedCommands==0)
        {nextSpeedRead=now+.5;_=RefreshSpeedAsync(player);}
    }
    internal async Task RefreshSpeedAsync(nint player)
    {
        if(readingSpeed || pendingSpeedCommands!=0 || disposed || !enabled)return;
        readingSpeed=true;int revision=speedRevision;
        try
        {
            string text=await MpcPlaybackRate.ReadAsync(player);
            if(disposed || !enabled || owner!=player || revision!=speedRevision)return;
            if(playbackRate.Parse(text) is {} rate){SelectedSpeed=rate;window?.SelectSpeed(rate);}
        }
        catch(Exception ex){TraceLog.Write("panel-rate-read "+ex.Message);}
        finally{readingSpeed=false;}
    }
    internal static bool IsCommentOverlay(nint candidate,nint player)
    {
        if(candidate==0 || Native.GetWindow(candidate,4)!=player)return false;
        Native.GetWindowThreadProcessId(candidate,out uint process);
        if(process!=Environment.ProcessId)return false;
        var name=new StringBuilder(64);Native.GetClassName(candidate,name,name.Capacity);
        return name.ToString()=="NicoBEGpuOverlay";
    }
    internal static Rect ActivationBounds(Rect viewport,double scale)
    {
        // Approximate the two button rows (including wrapping) without creating
        // or measuring a hidden WPF window on every polling tick.
        double width=Math.Min(548,viewport.Width/scale-24);
        int columns=Math.Max(1,(int)((width-66)/68));
        double height=20+84*Math.Ceiling(7d/columns);
        return PlaybackPanelWindow.Bounds(viewport,new Size(width*scale,height*scale),12*scale);
    }
    public void Dispose(){if(disposed)return;disposed=true;generation++;window?.Close();window=null;hover.Reset();}
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.Point point);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window,out Native.Rect rect);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(nint window);
}

internal sealed class PanelHover
{
    internal bool Visible {get;private set;}
    internal bool Update(bool corner,bool panel,double now)
    {
        return Visible=Visible?panel:corner;
    }
    internal void Reset(){Visible=false;}
}

internal sealed class PlaybackPanelWindow : Window
{
    internal static readonly int[] Jumps={-30,-10,-5,5,10,30};
    internal static readonly double[] Speeds={.5,1,1.25,1.5,1.75,2,2.5};
    internal readonly Dictionary<double,Button> SpeedButtons=new();
    internal readonly Dictionary<int,Button> JumpButtons=new();
    internal Button SettingsButton {get;}
    readonly TextBlock error=new() {Text="MPC-BEの応答を確認できませんでした。",FontSize=14,TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=new Thickness(4,4,4,0)};
    int requestGeneration;
    bool busy;
    Rect previous=Rect.Empty;
    double? selectedSpeed;
    bool selectionInitialized;
    nint owner;
    internal nint Handle {get;private set;}
    internal PlaybackPanelWindow(Func<double,Task<bool>> changeSpeed,Func<int,Task<bool>> jump,Action? openSettings=null)
    {
        Title="NicoBE 操作パネル";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.Manual;Left=Top=-32000;Width=548;Height=112;
        ShowInTaskbar=false;ShowActivated=false;Focusable=false;AllowsTransparency=true;
        FontFamily=new FontFamily("Yu Gothic UI");FontSize=16;
        SettingsTheme.Apply(this);Background=Brushes.Transparent;
        var body=new StackPanel();
        var frame=new Border {Background=new SolidColorBrush(Color.FromArgb(246,28,32,38)),BorderBrush=SettingsTheme.Line,
            BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Padding=new Thickness(10,8,6,8),Child=body};Content=frame;
        var seekRow=Row(body,"移動");
        foreach(int seconds in Jumps)
        {
            var button=MakeButton(seconds.ToString("+0;−0",CultureInfo.InvariantCulture)+"秒");
            button.ToolTip=seconds<0?$"{Math.Abs(seconds)}秒戻す":$"{seconds}秒送る";
            button.Click+=async (_,_)=>await Execute(()=>jump(seconds));seekRow.Children.Add(button);JumpButtons.Add(seconds,button);
        }
        SettingsButton=MakeButton("");
        SettingsButton.Content=new TextBlock {Text="\uE713",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=20};
        SettingsButton.ToolTip="設定を開く";
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton,"設定を開く");
        SettingsButton.Click+=(_,_)=>openSettings?.Invoke();seekRow.Children.Add(SettingsButton);
        var speedRow=Row(body,"速度");
        foreach(double speed in Speeds)
        {
            var button=MakeButton(speed.ToString("0.0#",CultureInfo.InvariantCulture)+"×");
            button.ToolTip=$"{speed.ToString("0.##",CultureInfo.InvariantCulture)}倍速に変更";
            button.Click+=async (_,_)=>await Execute(()=>changeSpeed(speed),()=>SelectSpeed(speed));
            speedRow.Children.Add(button);SpeedButtons.Add(speed,button);
        }
        error.SetResourceReference(TextBlock.ForegroundProperty,"ErrorBrush");body.Children.Add(error);
        Closed+=(_,_)=>CancelPending();
        SourceInitialized+=(_,_)=>
        {
            Handle=new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(Handle,-20,Native.GetWindowLong(Handle,-20)|(nint)0x08000080); // no activation; tool window
            HwndSource.FromHwnd(Handle)!.AddHook(Hook);
        };
    }
    static WrapPanel Row(Panel parent,string label)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(48)});row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,FontSize=16,Margin=new Thickness(2,0,6,4)});
        var buttons=new WrapPanel();Grid.SetColumn(buttons,1);row.Children.Add(buttons);parent.Children.Add(row);return buttons;
    }
    Button MakeButton(string text)=>new() {Style=(Style)FindResource("PlaybackPanelButton"),Content=text,Width=64,Height=38,MinHeight=38,Padding=new Thickness(3,4,3,4),
        Margin=new Thickness(0,0,4,4),FontSize=16,Focusable=false,IsTabStop=false};
    void Feedback(bool success)=>error.Visibility=success?Visibility.Collapsed:Visibility.Visible;
    void SetBusy(bool value)
    {
        busy=value;
        // Execute guards repeated clicks. Keep the button appearance unchanged:
        // the disabled style faded the entire panel on every speed/seek request.
    }
    internal void CancelPending(){requestGeneration++;SetBusy(false);error.Visibility=Visibility.Collapsed;}
    async Task Execute(Func<Task<bool>> command,Action? onSuccess=null)
    {
        if(busy)return;
        int mine=++requestGeneration;SetBusy(true);error.Visibility=Visibility.Collapsed;
        try
        {
            bool success=await command();
            if(mine!=requestGeneration)return;
            if(success)onSuccess?.Invoke();
            Feedback(success);
        }
        catch(Exception ex){TraceLog.Write("panel-command "+ex);if(mine==requestGeneration)Feedback(false);}
        finally{if(mine==requestGeneration)SetBusy(false);}
    }
    internal void SelectSpeed(double? selected)
    {
        if(selectionInitialized && selectedSpeed==selected)return;
        selectedSpeed=selected;selectionInitialized=true;
        foreach(var (speed,button) in SpeedButtons)
        {
            bool active=selected==speed;
            button.SetResourceReference(BackgroundProperty,active?"PrimaryBrush":"InputBrush");
            button.SetResourceReference(BorderBrushProperty,active?"AccentBrush":"LineBrush");
            button.FontWeight=active?FontWeights.Bold:FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,active?"選択中":"未選択");
        }
    }
    internal void Place(nint player,Rect video,double scale)
    {
        if(owner!=player){new WindowInteropHelper(this).Owner=player;owner=player;}
        new WindowInteropHelper(this).EnsureHandle();
        double desiredWidth=Math.Min(548,video.Width/scale-24);
        Width=desiredWidth;
        ((FrameworkElement)Content).Measure(new Size(desiredWidth,double.PositiveInfinity));
        Height=((FrameworkElement)Content).DesiredSize.Height;
        if(Height*scale>video.Height-24*scale){Hide();return;}
        var bounds=Bounds(video,new Size(Width*scale,Height*scale),12*scale);
        if(bounds!=previous || !IsVisible)
        {
            // Position the hidden HWND before its first Show; no centered flash.
            Native.SetWindowPos(Handle,InsertAfter(),(int)bounds.X,(int)bounds.Y,(int)Math.Ceiling(bounds.Width),(int)Math.Ceiling(bounds.Height),0x0210);
            previous=bounds;
        }
        if(!IsVisible)Show();
    }
    internal static Rect Bounds(Rect video,Size size,double inset)=>new(video.Left+inset,video.Bottom-size.Height-inset,size.Width,size.Height);
    nint InsertAfter()
    {
        // Above this player's comment layer, but below unrelated applications.
        nint after=Native.GetWindow(owner,3);
        while(after!=0 && (after==Handle || PlaybackPanel.IsCommentOverlay(after,owner)))after=Native.GetWindow(after,3);
        // Never promote a normal panel/owner into the topmost band.
        if((Native.GetWindowLong(owner,-20)&8)==0 && after!=0 && (Native.GetWindowLong(after,-20)&8)!=0)return 0;
        return after;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WindowPosition {public nint Window,After;public int X,Y,Width,Height;public uint Flags;}
    nint Hook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x0021){handled=true;return 3;} // MA_NOACTIVATE; still deliver the click
        if(message==0x0046 && lParam!=0 && owner!=0 && Native.IsWindow(owner))
        {
            var position=Marshal.PtrToStructure<WindowPosition>(lParam);
            // WPF Show also changes z-order. Constrain that operation before the
            // window becomes visible, avoiding a flash over the foreground app.
            position.Flags|=0x0210; // SWP_NOACTIVATE | SWP_NOOWNERZORDER
            if((position.Flags&0x0040)!=0){position.After=InsertAfter();position.Flags&=~0x0004u;}
            Marshal.StructureToPtr(position,lParam,false);
        }
        return 0;
    }
}
