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
    internal event Action<string,bool>? SourceChanged;
    internal event Action<string,bool>? CommonChanged;
    internal event Action<bool>? FetchRequested;
    internal event Action? SpeedChanging;
    internal event Action<double>? SpeedChanged;
    internal event Action? SpeedChangeFinished;
    PanelCommentState? commentState;
    internal void SetCommentState(PanelCommentState value){commentState=value;window?.SetCommentState(value);}
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
        SpeedChanging?.Invoke();
        speedRevision++;pendingSpeedCommands++;
        try
        {
            if(!await send(MpcClient.SetSpeed,speed.ToString("0.##",CultureInfo.InvariantCulture)) || disposed || mine!=generation)return false;
            SelectedSpeed=speed;window?.SelectSpeed(speed);SpeedChanged?.Invoke(speed);return true;
        }
        finally{pendingSpeedCommands--;nextSpeedRead=lastTick+.5;SpeedChangeFinished?.Invoke();}
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
        if(viewport.Width<320*scale || viewport.Height<140*scale){Hide();return;}
        var pointer=new Point(cursor.X,cursor.Y);
        var corner=ActivationBounds(viewport,scale);
        bool inPanel=window?.IsVisible==true && GetWindowRect(window.Handle,out var panelRect)
            && new Rect(panelRect.Left,panelRect.Top,panelRect.Width,panelRect.Height).Contains(pointer);
        // Do not react through an unrelated window covering the player.
        nint root=Native.GetAncestor(hit,2);
        bool overPlayer=root==player || root==window?.Handle || IsCommentOverlay(root,player);
        if(window?.MenuOpen==true){overPlayer=true;inPanel=true;}
        if(!hover.Update(overPlayer && corner.Contains(pointer),overPlayer && inPanel,now))
        {if(window?.IsVisible==true)window.Hide();return;}
        if(window==null)
        {
            window=new PlaybackPanelWindow(ChangeSpeedAsync,JumpAsync,()=>openSettings?.Invoke());
            window.SourceChanged+=(source,on)=>SourceChanged?.Invoke(source,on);
            window.CommonChanged+=(name,on)=>CommonChanged?.Invoke(name,on);
            window.FetchRequested+=force=>FetchRequested?.Invoke(force);
            if(commentState!=null)window.SetCommentState(commentState);
        }
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
        // Approximate the toolbar and two button rows (including wrapping) without creating
        // or measuring a hidden WPF window on every polling tick.
        double width=Math.Min(PlaybackPanelWindow.PreferredWidth,viewport.Width/scale-24);
        int columns=Math.Max(1,(int)((width-88)/54));
        double height=30+30*(Math.Ceiling(430/Math.Max(240,width-86))+Math.Ceiling(6d/columns)+Math.Ceiling(7d/columns));
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
    internal const double PreferredWidth=544;
    internal event Action<string,bool>? SourceChanged;
    internal event Action<string,bool>? CommonChanged;
    internal event Action<bool>? FetchRequested;
    readonly Dictionary<string,CheckBox> commentSwitches=new();
    readonly TextBlock fetchStatus=new(){FontSize=14,Width=60,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock spinner=new(){Text="◌",FontSize=16,Width=14,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center,RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform()};
    Button? fetchButton;
    Button? refreshMenu;
    readonly Popup sourcePopup=new(){Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true,Focusable=false};
    internal bool MenuOpen=>sourcePopup.IsOpen;
    bool updatingComments,fetchBusy;
    internal static readonly int[] Jumps={-30,-10,-5,5,10,30};
    internal static readonly double[] Speeds={.5,1,1.25,1.5,1.75,2,2.5};
    internal readonly Dictionary<double,Button> SpeedButtons=new();
    internal readonly Dictionary<int,Button> JumpButtons=new();
    internal Button SettingsButton {get;}
    internal Button CommentOn {get;}
    internal Button CommentOff {get;}
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
        WindowStartupLocation=WindowStartupLocation.Manual;Left=Top=-32000;Width=PreferredWidth;Height=120;
        ShowInTaskbar=false;ShowActivated=false;Focusable=false;AllowsTransparency=true;
        FontFamily=new FontFamily("Yu Gothic UI");FontSize=14;
        SettingsTheme.Apply(this);Background=Brushes.Transparent;
        var body=new StackPanel();
        var frame=new Border {Background=new SolidColorBrush(Color.FromArgb(246,28,32,38)),BorderBrush=SettingsTheme.Line,
            BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(3),Padding=new Thickness(12,8,12,8),Child=body};Content=frame;
        var toolbar=Row(body,"コメント");
        var masterGroup=new UniformGrid{Columns=2};
        CommentOn=MakeSegment("オン");CommentOff=MakeSegment("オフ");
        CommentOff.BorderThickness=new Thickness(0);
        CommentOn.Click+=(_,_)=>CommonChanged?.Invoke("master",true);
        CommentOff.Click+=(_,_)=>CommonChanged?.Invoke("master",false);
        masterGroup.Children.Add(CommentOn);masterGroup.Children.Add(CommentOff);
        toolbar.Children.Add(SegmentFrame(masterGroup,74));
        var sources=MakeButton("表示元 ▾");sources.Width=72;sources.ToolTip="NicoJK保存ログ・MP4内オリジナルログ・過去ログAPI・ASS字幕の選択";
        toolbar.Children.Add(sources);sourcePopup.PlacementTarget=sources;
        sources.Click+=(_,_)=>sourcePopup.IsOpen=!sourcePopup.IsOpen;
        var menu=new StackPanel();
        foreach(var item in new[]{("saved","NicoJK保存ログ"),("embedded","MP4内オリジナルログ"),("past","過去ログAPI"),("ass","ASS字幕")})
        {
            var toggle=MakeSwitch(item.Item2);toggle.Margin=new Thickness(4,3,4,5);commentSwitches[item.Item1]=toggle;menu.Children.Add(toggle);
            string key=item.Item1;toggle.Click+=(_,_)=>{if(!updatingComments)SourceChanged?.Invoke(key,toggle.IsChecked==true);};
        }
        menu.Children.Add(new Border{BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(0,1,0,0),Margin=new Thickness(0,3,0,6)});
        refreshMenu=MakeButton("過去ログを再取得");refreshMenu.Width=152;refreshMenu.Click+=(_,_)=>{sourcePopup.IsOpen=false;FetchRequested?.Invoke(true);};menu.Children.Add(refreshMenu);
        sourcePopup.Child=new Border{Child=menu,Background=new SolidColorBrush(Color.FromRgb(28,32,38)),BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(8)};
        body.Children.Add(sourcePopup);
        var fetchState=new StackPanel{Orientation=Orientation.Horizontal,Height=28,Margin=new Thickness(6,0,6,0)};
        fetchState.Children.Add(spinner);fetchState.Children.Add(fetchStatus);toolbar.Children.Add(fetchState);
        fetchButton=MakeButton("過去ログ取得");fetchButton.Width=108;fetchButton.SetResourceReference(BackgroundProperty,"PrimaryBrush");fetchButton.SetResourceReference(BorderBrushProperty,"AccentBrush");fetchButton.Click+=(_,_)=>FetchRequested?.Invoke(false);toolbar.Children.Add(fetchButton);
        var automatic=MakeSwitch("自動");commentSwitches["auto"]=automatic;toolbar.Children.Add(automatic);
        automatic.Margin=new Thickness(2,0,6,0);automatic.ToolTip="保存済みの実況コメントがない場合のみ自動取得します。";
        automatic.Click+=(_,_)=>{if(!updatingComments)CommonChanged?.Invoke("auto",automatic.IsChecked==true);};
        SettingsButton=MakeButton("");SettingsButton.Width=28;SettingsButton.Background=Brushes.Transparent;SettingsButton.BorderThickness=new Thickness(0);SettingsButton.Margin=new Thickness(0);
        SettingsButton.Content=new TextBlock {Text="\uE713",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=17};
        SettingsButton.ToolTip="設定を開く";
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton,"設定を開く");
        SettingsButton.Click+=(_,_)=>openSettings?.Invoke();
        var topRow=(Grid)toolbar.Parent;topRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        Grid.SetColumn(SettingsButton,2);SettingsButton.VerticalAlignment=VerticalAlignment.Top;topRow.Children.Add(SettingsButton);
        AddDivider(body);var seekRow=SegmentRow(body,"秒送り");
        foreach(int seconds in Jumps)
        {
            var button=MakeSegment(seconds.ToString("+0;−0",CultureInfo.InvariantCulture));
            button.ToolTip=seconds<0?$"{Math.Abs(seconds)}秒戻す":$"{seconds}秒送る";
            button.Click+=async (_,_)=>await Execute(()=>jump(seconds));seekRow.Children.Add(button);JumpButtons.Add(seconds,button);
        }
        AddDivider(body);var speedRow=SegmentRow(body,"速度");
        foreach(double speed in Speeds)
        {
            var button=MakeSegment(speed.ToString("0.0#",CultureInfo.InvariantCulture)+"×");
            button.ToolTip=$"{speed.ToString("0.##",CultureInfo.InvariantCulture)}倍速に変更";
            button.Click+=async (_,_)=>await Execute(()=>changeSpeed(speed),()=>SelectSpeed(speed));
            speedRow.Children.Add(button);SpeedButtons.Add(speed,button);
        }
        JumpButtons[30].BorderThickness=new Thickness(0);SpeedButtons[2.5].BorderThickness=new Thickness(0);
        IsVisibleChanged+=(_,_)=>{AnimateSpinner();if(!IsVisible)sourcePopup.IsOpen=false;};spinner.Visibility=Visibility.Hidden;
        error.SetResourceReference(TextBlock.ForegroundProperty,"ErrorBrush");body.Children.Add(error);
        Closed+=(_,_)=>CancelPending();
        SourceInitialized+=(_,_)=>
        {
            Handle=new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(Handle,-20,Native.GetWindowLong(Handle,-20)|(nint)0x08000080); // no activation; tool window
            HwndSource.FromHwnd(Handle)!.AddHook(Hook);
        };
    }
    static readonly ControlTemplate CompactToggle=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="rail" Width="16" Height="16" CornerRadius="2" BorderBrush="#83909E" BorderThickness="1" Background="Transparent" VerticalAlignment="Center">
              <Path x:Name="check" Data="M 2 6 L 5 9 L 11 2" Stroke="White" StrokeThickness="2" Visibility="Collapsed" VerticalAlignment="Center" HorizontalAlignment="Center"/>
            </Border>
            <ContentPresenter Margin="5,0,0,0" VerticalAlignment="Center"/>
          </StackPanel>
          <ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="rail" Property="Background" Value="#008A98"/><Setter TargetName="check" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    CheckBox MakeSwitch(string label)=>new(){Content=label,Template=CompactToggle,FontSize=14,Height=28,Focusable=false,IsTabStop=false,Margin=new Thickness(0,0,5,2),VerticalAlignment=VerticalAlignment.Center};
    internal void SetCommentState(PanelCommentState value)
    {
        updatingComments=true;
        try
        {
            // Auto is a global preference, not a per-file acquisition action.
            foreach(var (name,on,available) in new[]{("saved",value.Saved,value.SavedAvailable),("embedded",value.Embedded,value.EmbeddedAvailable),("past",value.Past,value.PastAvailable),("ass",value.Ass,value.AssAvailable),("auto",value.Auto,true)})
            {var toggle=commentSwitches[name];toggle.IsChecked=on;toggle.IsEnabled=available;toggle.Opacity=available?1:.45;if(name!="auto")toggle.ToolTip=available?(name=="saved"?"設定したNicoJKログフォルダのコメント":name=="embedded"?"MP4コンテナ内のオリジナル形式コメント":name=="ass"?"識別できた実況ASS。通常字幕は対象外です。":"コメントの表示切り替え"):"利用できるコメントがありません。";}
            CommentOn.Background=value.Master?(Brush)FindResource("PrimaryBrush"):Brushes.Transparent;
            CommentOff.Background=value.Master?Brushes.Transparent:(Brush)FindResource("PrimaryBrush");
            fetchStatus.Text=value.Status;fetchStatus.ToolTip=value.Status+"\n"+value.Detail;fetchButton!.IsEnabled=value.CanFetch;fetchButton.ToolTip=value.Detail;
            refreshMenu!.IsEnabled=value.CanRefresh;
            if(fetchBusy!=value.Busy){fetchBusy=value.Busy;AnimateSpinner();}
        }
        finally{updatingComments=false;}
    }
    void AnimateSpinner()
    {
        var rotation=(RotateTransform)spinner.RenderTransform;
        spinner.Visibility=fetchBusy?Visibility.Visible:Visibility.Hidden;
        rotation.BeginAnimation(RotateTransform.AngleProperty,fetchBusy&&IsVisible?new System.Windows.Media.Animation.DoubleAnimation(0,360,TimeSpan.FromSeconds(1)){RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever}:null);
    }
    static WrapPanel Row(Panel parent,string label)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(60)});row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,FontSize=14,Margin=new Thickness(0,0,2,2)});
        var buttons=new WrapPanel();Grid.SetColumn(buttons,1);row.Children.Add(buttons);parent.Children.Add(row);return buttons;
    }
    Button MakeButton(string text)=>new() {Style=(Style)FindResource("PlaybackPanelButton"),Content=text,Width=57,Height=28,MinHeight=28,Padding=new Thickness(2,1,2,1),
        Margin=new Thickness(0,0,4,0),Background=Brushes.Transparent,FontSize=14,Focusable=false,IsTabStop=false};
    static readonly ControlTemplate SegmentTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter TargetName="chrome" Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    Button MakeSegment(string label)
    {
        var button=MakeButton(label);button.Template=SegmentTemplate;button.Width=double.NaN;
        button.Margin=new Thickness(0);button.BorderThickness=new Thickness(0,0,1,0);
        return button;
    }
    static Border SegmentFrame(UIElement content,double width=double.NaN)=>new(){Child=content,Width=width,BorderBrush=new SolidColorBrush(Color.FromRgb(108,121,133)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(3),Margin=new Thickness(0,0,4,0),ClipToBounds=true};
    static void AddDivider(Panel parent)=>parent.Children.Add(new Border{Height=6});
    sealed class Segments:UniformGrid
    {
        protected override Size MeasureOverride(Size available)
        {
            Columns=double.IsFinite(available.Width)?Math.Min(Math.Max(1,Children.Count),Math.Max(1,(int)(available.Width/54))):Math.Max(1,Children.Count);
            return base.MeasureOverride(available);
        }
    }
    static Segments SegmentRow(Panel parent,string label)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(60)});row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock{Text=label,FontSize=14,VerticalAlignment=VerticalAlignment.Center});
        var group=new Segments();var border=SegmentFrame(group);border.Margin=new Thickness(0);Grid.SetColumn(border,1);row.Children.Add(border);parent.Children.Add(row);return group;
    }
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
            if(active)button.SetResourceReference(BackgroundProperty,"PrimaryBrush");else button.Background=Brushes.Transparent;
            button.SetResourceReference(BorderBrushProperty,active?"AccentBrush":"LineBrush");
            button.FontWeight=active?FontWeights.Bold:FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,active?"選択中":"未選択");
        }
    }
    internal void Place(nint player,Rect video,double scale)
    {
        if(owner!=player){new WindowInteropHelper(this).Owner=player;owner=player;}
        new WindowInteropHelper(this).EnsureHandle();
        double desiredWidth=Math.Min(PreferredWidth,video.Width/scale-24);
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
