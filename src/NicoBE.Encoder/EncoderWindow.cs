using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NicoBE.Encoder;

public sealed class QueueItem : INotifyPropertyChanged
{
    public required string Path {get;init;}
    public string Name=>System.IO.Path.GetFileName(Path);
    string state="待機";public string State {get=>state;set{state=value;PropertyChanged?.Invoke(this,new(nameof(State)));}}
    public string Error {get;set;}="";
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class EncoderWindow : Window
{
    sealed record Choice(string Value,string Label);
    readonly ObservableCollection<QueueItem> queue=new();
    readonly ComboBox codec=Select(("H264","H.264"),("H265","H.265 / HEVC"),("AV1","AV1"));
    readonly ComboBox container=Select(("MP4","MP4"),("MKV","MKV"));
    readonly ComboBox backend=Select(("CPU","CPU"),("NVENC","NVIDIA NVENC"),("QSV","Intel QSV"),("AMF","AMD AMF"));
    readonly ComboBox resolution=Select(("1920","1920 × 1080"),("1600","1600 × 900"),("1280","1280 × 720"));
    readonly ComboBox rate=Select(("Quality","品質指定"),("Bitrate","ビットレート指定"));
    readonly ComboBox speed=Select(("Fast","速度重視"),("Standard","標準"),("Quality","画質重視"));
    readonly ComboBox deinterlace=Select(("Auto","自動"),("On","有効"),("Off","無効"));
    readonly ComboBox audio=Select(("AAC128","AAC / 128 kbps"),("AAC192","AAC / 192 kbps"),("AAC256","AAC / 256 kbps"),("Copy","音声をコピー"));
    readonly ComboBox frameRate=Select(("24","24 fps"),("30","30 fps"),("60","60 fps"));
    readonly ComboBox passes=Select(("1","1パス"),("2","2パス"));
    readonly TextBlock passHelp=Note("");
    readonly CheckBox embed=new() {Content="埋め込む"};
    readonly CheckBox externalAss=new() {Content="外部ASSファイルを保存"};
    readonly CheckBox pastLogs=new() {Content="過去ログAPIでコメントを補完"};
    readonly TextBox outputFolder=new() {Height=36},logFolder=new() {Height=36};
    FrameworkElement? commentPathRow;
    FrameworkElement? qualityField,bitrateField;
    readonly Slider quality=new() {Minimum=0,Maximum=51,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=5};
    readonly AssStylePanel assStyle=new();
    readonly ExtraVideoArgumentsPanel extraArguments;
    readonly TextBox bitrate=new() {Height=36},ffmpeg=new() {Height=36};
    readonly TextBox status=SelectableText("TSファイルを追加、またはこの画面へドロップしてください。"),detail=SelectableText("");
    readonly TextBox errorText=SelectableText("",new SolidColorBrush(Color.FromRgb(255,171,166)));
    readonly TextBox warningText=SelectableText("",new SolidColorBrush(Color.FromRgb(255,211,137)));
    readonly Border warningPanel=new() {Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0),Padding=new Thickness(10),CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.FromRgb(156,119,56)),Background=new SolidColorBrush(Color.FromRgb(48,40,27))};
    readonly Border errorPanel=new() {Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0),Padding=new Thickness(10),CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.FromRgb(190,91,89)),Background=new SolidColorBrush(Color.FromRgb(55,32,36))};
    readonly ProgressBar progress=new() {Height=6,Minimum=0,Maximum=1,Foreground=SettingsTheme.Accent,Background=SettingsTheme.Line,Margin=new Thickness(0,10,0,8)};
    readonly Button start=new() {Content="エンコード開始",MinWidth=156},cancel=new() {Content="中止",IsEnabled=false,MinWidth=90};
    readonly StackPanel settingsPanel=new();
    readonly StackPanel basicPanel=new();
    readonly List<TextBlock> saveMessages=new();
    readonly StackPanel queueActions=new() {Orientation=Orientation.Horizontal};
    readonly Stopwatch elapsed= new();
    readonly TextBox elapsedText=SelectableText("経過時間  00:00:00");
    readonly DispatcherTimer elapsedTimer=new() {Interval=TimeSpan.FromMilliseconds(250)};
    internal TabControl Tabs {get;}=new();
    readonly Grid layout=new();
    readonly DockPanel outer=new() {Margin=new Thickness(20)};
    readonly ScrollViewer basicScroll=new();
    readonly FrameworkElement ffmpegSection;
    readonly Action acknowledgeGuide;
    bool ffmpegGuideAcknowledged;
    internal FirstRunGuide? Guide {get;private set;}
    CancellationTokenSource? cancellation;
    bool closeAfterStop;
    internal EncoderRuntimeWatch RuntimeWatch {get;set;}
    EncoderQueueRecovery? recovery;
    bool queueSaveFailed;
    internal Func<string,EncoderSettings,IProgress<EncodeProgress>,CancellationToken,Task<EncodeResult>> RunJob {get;set;}=
        (input,settings,progress,token)=>EncoderJob.Run(input,settings,progress,token);
    public EncoderWindow(string[] files,EncoderSettings? initialSettings=null,Action? acknowledgeGuide=null)
    {
        RuntimeWatch=new EncoderRuntimeWatch(message=>Dispatcher.BeginInvoke(()=>ShowRuntimeWarning(message)));
        this.acknowledgeGuide=acknowledgeGuide??(()=>EncoderSettings.AcknowledgeFfmpegGuide());
        Title="NicoBE Encoder";Width=900;Height=Math.Min(880,SystemParameters.WorkArea.Height-40);MinWidth=800;MinHeight=680;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Yu Gothic UI");FontSize=16;AllowDrop=true;SettingsTheme.Apply(this);
        var passStyle=new Style(typeof(ComboBox),(Style)FindResource(typeof(ComboBox)));
        var disabledPass=new Trigger {Property=IsEnabledProperty,Value=false};disabledPass.Setters.Add(new Setter(OpacityProperty,.45));passStyle.Triggers.Add(disabledPass);passes.Style=passStyle;
        ToolTipService.SetShowOnDisabled(passes,true);
        string icon=Path.Combine(Paths.ExecutableDirectory,"assets","nicobe.ico");if(File.Exists(icon))Icon=BitmapFrame.Create(new Uri(icon));
        Content=layout;layout.Children.Add(outer);
        outer.Children.Add(Tabs);
        var operations=new DockPanel();Tabs.Items.Add(new TabItem {Header="操作",Content=operations});
        Tabs.Items.Add(new TabItem {Header="エンコード設定",Content=SettingsTab(settingsPanel)});
        Tabs.Items.Add(new TabItem {Header="ASS書式設定",Content=SettingsTab(assStyle)});
        Tabs.Items.Add(new TabItem {Header="基本設定",Content=SettingsTab(basicPanel)});
        Tabs.Items.Add(LicensePanel.Create("NicoBE Encoder"));
        var footer=new StackPanel {Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(footer,Dock.Bottom);operations.Children.Add(footer);
        footer.Children.Add(elapsedText);footer.Children.Add(progress);footer.Children.Add(status);footer.Children.Add(detail);
        errorText.MaxHeight=110;errorPanel.Child=errorText;footer.Children.Add(errorPanel);
        warningText.MaxHeight=130;warningPanel.Child=warningText;footer.Children.Add(warningPanel);
        var footerButtons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        cancel.Margin=new Thickness(0,0,10,0);cancel.Click+=(_,_)=>cancellation?.Cancel();footerButtons.Children.Add(cancel);
        start.SetResourceReference(StyleProperty,"PrimaryButton");start.Click+=async (_,_)=>await Encode();footerButtons.Children.Add(start);footer.Children.Add(footerButtons);
        DockPanel.SetDock(queueActions,Dock.Top);operations.Children.Add(queueActions);
        var grid=new DataGrid {ItemsSource=queue,AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,SelectionMode=DataGridSelectionMode.Single,Margin=new Thickness(0,10,0,0),RowHeaderWidth=0,EnableColumnVirtualization=false,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        grid.Columns.Add(new DataGridTextColumn {Header="入力TS",Binding=new Binding(nameof(QueueItem.Name)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        var stateStyle=new Style(typeof(TextBlock));
        var failed=new DataTrigger {Binding=new Binding(nameof(QueueItem.State)),Value="失敗"};failed.Setters.Add(new Setter(TextBlock.ForegroundProperty,errorText.Foreground));failed.Setters.Add(new Setter(TextBlock.FontWeightProperty,FontWeights.Bold));stateStyle.Triggers.Add(failed);
        grid.Columns.Add(new DataGridTextColumn {Header="状態",Binding=new Binding(nameof(QueueItem.State)),Width=130,ElementStyle=stateStyle});
        var remove=new FrameworkElementFactory(typeof(Button));remove.SetValue(ContentControl.ContentProperty,"×");remove.SetValue(FrameworkElement.ToolTipProperty,"一覧から削除（元ファイルは削除しません）");
        remove.SetValue(Control.PaddingProperty,new Thickness(6,0,6,0));remove.SetValue(FrameworkElement.MinWidthProperty,30.0);remove.SetValue(FrameworkElement.MinHeightProperty,30.0);
        remove.SetValue(FrameworkElement.WidthProperty,30.0);remove.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        remove.SetBinding(UIElement.IsEnabledProperty,new Binding(nameof(IsEnabled)) {Source=start});
        remove.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,e)=>{if(cancellation==null && sender is Button {DataContext:QueueItem item})queue.Remove(item);e.Handled=true;}));
        var removeCell=new Style(typeof(DataGridCell));removeCell.BasedOn=(Style)FindResource(typeof(DataGridCell));removeCell.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(5,4,5,4)));
        grid.Columns.Add(new DataGridTemplateColumn {Header="",Width=50,MinWidth=50,CanUserResize=false,CanUserSort=false,CanUserReorder=false,CellStyle=removeCell,CellTemplate=new DataTemplate {VisualTree=remove}});operations.Children.Add(grid);
        grid.SelectionChanged+=(_,_)=>{if(grid.SelectedItem is QueueItem item)ShowError(item.Error.Length>0?item.Name+"\n"+item.Error:"");};
        queueActions.Children.Add(ActionButton("TSを追加",()=>
        {
            var dialog=new OpenFileDialog {Title="エンコードするTSを選択",Filter="録画TS|*.ts;*.m2ts;*.mts",Multiselect=true};if(dialog.ShowDialog(this)==true)AddFiles(dialog.FileNames);
        }));
        queueActions.Children.Add(ActionButton("リストをクリア",()=>{queue.Clear();ShowError("");ShowWarnings(Array.Empty<string>());status.Text="一覧をクリアしました。";status.Foreground=SettingsTheme.Muted;detail.Text="";progress.Value=0;}));
        queueActions.Children.Add(new TextBlock {Text="TSをこの画面へドロップしても追加できます。",FontSize=14,Foreground=SettingsTheme.Muted,VerticalAlignment=VerticalAlignment.Center});
        var video=new StackPanel();var picture=new StackPanel();
        video.Children.Add(Pair(Field("映像形式",codec),Field("処理方式",backend),bottomMargin:0));
        video.Children.Add(Pair(Field("解像度",resolution),Field("フレームレート",frameRate),bottomMargin:0));
        video.Children.Add(Field("インターレース解除",deinterlace));
        video.Children.Add(Field("保存形式",container));
        picture.Children.Add(Field("画質の指定",rate));
        var qualityRow=new DockPanel();var qualityNumber=new TextBlock {MinWidth=32,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(qualityNumber,Dock.Right);qualityRow.Children.Add(qualityNumber);qualityRow.Children.Add(quality);
        qualityRow.Height=36;quality.ValueChanged+=(_,_)=>qualityNumber.Text=quality.Value.ToString("0");qualityField=Field("品質値",qualityRow);picture.Children.Add(qualityField);
        var bitrateRow=new DockPanel();var mbps=Note("Mbps");mbps.Margin=new Thickness(8,0,0,0);mbps.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(mbps,Dock.Right);bitrateRow.Children.Add(mbps);bitrateRow.Children.Add(bitrate);bitrateField=Field("ビットレート",bitrateRow);picture.Children.Add(bitrateField);
        picture.Children.Add(Field("処理速度",speed));
        picture.Children.Add(Field("パス数",passes));
        settingsPanel.Children.Add(Pair(Section("映像",video),Section("画質・速度",picture),3,2));
        var sound=new StackPanel();sound.Children.Add(Field("音声形式・ビットレート",audio));
        var audioNote=Note("日英の二重音声は別トラックに分離し、日本語を既定にします。音声コピー指定時も、分離対象はAAC 128 kbpsで保存します。");
        audioNote.Margin=new Thickness(0,4,0,14);sound.Children.Add(audioNote);
        var comments=new StackPanel();embed.Content="コメントデータを格納する";embed.Margin=new Thickness(0,4,0,10);comments.Children.Add(embed);
        externalAss.Margin=new Thickness(0,0,0,6);comments.Children.Add(externalAss);
        pastLogs.Margin=new Thickness(0,0,0,6);pastLogs.ToolTip="共有キャッシュを使い、不足分をインターネットから取得します。NicoJKログがなくても利用できます。";comments.Children.Add(pastLogs);
        embed.ToolTip="MP4はオリジナル形式、MKVはASS字幕を動画内に保存します。";
        externalAss.ToolTip="動画と同名の.assファイルを同じフォルダに保存します。埋め込みとは独立して選べます。";
        var assNote=Note("文字サイズなどは「ASS書式設定」で変更できます。");assNote.Margin=new Thickness(0,6,0,14);comments.Children.Add(assNote);
        settingsPanel.Children.Add(Pair(Section("音声",sound),Section("コメント",comments)));
        extraArguments=new ExtraVideoArgumentsPanel(ReadSettings,this);settingsPanel.Children.Add(Section("FFmpeg追加パラメータ",extraArguments,collapsible:true));
        basicPanel.Children.Add(PathSection("保存先","エンコードした動画を保存するフォルダ。同名のファイルには連番を付けます。",FolderField(outputFolder,"動画の出力先を選択")));
        commentPathRow=FolderField(logFolder,"コメント埋め込み用のNicoJKログフォルダを選択");
        basicPanel.Children.Add(PathSection("コメントログ","NicoJKのjk1、jk4などを含む親フォルダ。コメントを保存する場合に使用します。",commentPathRow));
        embed.Checked+=(_,_)=>UpdateEmbedControls();embed.Unchecked+=(_,_)=>UpdateEmbedControls();
        externalAss.Checked+=(_,_)=>UpdateEmbedControls();externalAss.Unchecked+=(_,_)=>UpdateEmbedControls();
        container.SelectionChanged+=(_,_)=>UpdateEmbedControls();
        rate.SelectionChanged+=(_,_)=>UpdateRateControls();
        backend.SelectionChanged+=(_,_)=>{UpdateQualityRange();UpdatePassControls();};
        codec.SelectionChanged+=(_,_)=>{UpdateQualityRange();UpdatePassControls();};
        var toolRow=new DockPanel();
        var download=ActionButton("ダウンロード",DownloadFfmpeg);download.Margin=new Thickness(12,0,0,0);
        download.ToolTip="FFmpegをダウンロード・展開し、パスを自動設定します。";
        var browse=ActionButton("参照",()=>
        {
            var dialog=new OpenFileDialog {Filter="FFmpeg|ffmpeg.exe",Title="ffmpeg.exeを選択"};if(dialog.ShowDialog(this)==true)ffmpeg.Text=dialog.FileName;
        });browse.Margin=new Thickness(10,0,0,0);browse.MinWidth=76;DockPanel.SetDock(browse,Dock.Right);toolRow.Children.Add(browse);toolRow.Children.Add(ffmpeg);
        ffmpegSection=PathSection("FFmpeg","ffmpeg.exeを指定します。未導入ならダウンロードで配置・設定できます。",toolRow,download);basicPanel.Children.Add(ffmpegSection);
        foreach(var pathBox in new[]{outputFolder,logFolder,ffmpeg})
        {pathBox.FontWeight=FontWeights.Normal;pathBox.SetBinding(ToolTipProperty,new Binding(nameof(TextBox.Text)) {Source=pathBox});}
        elapsedTimer.Tick+=(_,_)=>UpdateElapsed();Closed+=(_,_)=>{elapsedTimer.Stop();RuntimeWatch.Dispose();recovery?.Dispose();};
        try{LoadSettings(initialSettings??EncoderSettings.Load());}catch(Exception ex){LoadSettings(new());status.Text="前回の設定を読み込めませんでした: "+ex.Message;}
        Loaded+=(_,_)=>Dispatcher.BeginInvoke(()=>{if(IsVisible)ShowFirstRunGuide();},DispatcherPriority.Loaded);
        Loaded+=async (_,_)=>await CheckGpu();
        backend.SelectionChanged+=async (_,_)=>{if(IsLoaded)await CheckGpu();};
        codec.SelectionChanged+=async (_,_)=>{if(IsLoaded)await CheckGpu();};
        ffmpeg.LostKeyboardFocus+=async (_,_)=>await CheckGpu();
        PreviewDragOver+=(_,e)=>{e.Effects=cancellation==null && e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        Drop+=(_,e)=>{if(cancellation==null && e.Data.GetData(DataFormats.FileDrop) is string[] dropped)AddFiles(dropped);e.Handled=true;};
        Closing+=(_,e)=>{if(cancellation!=null){e.Cancel=true;closeAfterStop=true;cancellation.Cancel();status.Text="処理を中止して終了しています…";}};
        if(initialSettings==null)RestoreQueue();
        queue.CollectionChanged+=(_,_)=>SaveRecovery();
        AddFiles(files);
        if(initialSettings==null)EncoderWindowPlacement.Attach(this);
    }
    internal void RestoreQueue()
    {
        EncoderQueueRecovery? restored=null;
        try
        {
            restored=EncoderQueueRecovery.RestoreFirst();if(restored?.Snapshot is not {} state)return;
            LoadSettings(state.Settings);
            // Keep missing inputs visible so disconnected drives are not silently lost.
            foreach(string path in state.Files)queue.Add(new(){Path=path});
            recovery=restored;restored=null;
            status.Text=$"未完了のキュー {queue.Count}件と設定を復元しました。「エンコード開始」で再開できます。";
        }
        catch(Exception ex){ShowError("保存キューを復元できませんでした。\n"+ex.Message);}
        finally{restored?.Dispose();}
    }
    void SaveRecovery()
    {
        if(recovery?.Snapshot is not {} state)return;
        try{recovery.Save(queue.Select(q=>q.Path),state.Settings);}
        catch(Exception ex)
        {queueSaveFailed=true;ShowError("残りのキューを保存できませんでした。次の処理を停止します。\n"+ex.Message);}
    }
    void ShowRuntimeWarning(string message)
    {
        warningText.Text=message+"\n未完了のキューは再起動後に復元します。";
        warningPanel.Visibility=Visibility.Visible;
    }
    internal void ShowFirstRunGuide()
    {
        if(ffmpegGuideAcknowledged || Guide!=null)return;
        Tabs.SelectedIndex=3;layout.UpdateLayout();ffmpegSection.BringIntoView();basicScroll.UpdateLayout();
        Guide=new FirstRunGuide(ffmpegSection,ffmpegSection,()=>
        {
            // Acknowledging the guide does not save uncommitted form edits.
            acknowledgeGuide();ffmpegGuideAcknowledged=true;
            Guide!.Accept.IsDefault=false;layout.Children.Remove(Guide);Guide.Detach();Guide=null;
            outer.IsHitTestVisible=true;KeyboardNavigation.SetTabNavigation(outer,KeyboardNavigationMode.Continue);ffmpeg.Focus();
        },"FFmpegの準備","「ダウンロード」を押すと、自動で導入・設定できます。\nすでにお持ちの場合は、ffmpeg.exeへのパスを入力するか、\n「参照」から選択してください。\n同じフォルダにffprobe.exeも必要です。");
        outer.IsHitTestVisible=false;KeyboardNavigation.SetTabNavigation(outer,KeyboardNavigationMode.None);
        layout.Children.Add(Guide);layout.UpdateLayout();Guide.Accept.Focus();
    }
    void AddFiles(IEnumerable<string> files)
    {
        foreach(string file in files)
            if(File.Exists(file) && new[]{".ts",".m2ts",".mts"}.Contains(Path.GetExtension(file).ToLowerInvariant()) && !queue.Any(q=>q.Path.Equals(Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase)))queue.Add(new() {Path=Path.GetFullPath(file)});
    }
    void LoadSettings(EncoderSettings s)
    {
        ffmpegGuideAcknowledged=s.FfmpegGuideAcknowledged;
        ffmpeg.Text=s.Ffmpeg;codec.SelectedValue=s.Codec;backend.SelectedValue=s.Backend;resolution.SelectedValue=s.Width.ToString(CultureInfo.InvariantCulture);
        rate.SelectedValue=s.RateControl;quality.Value=s.Quality;bitrate.Text=s.BitrateMbps.ToString(CultureInfo.CurrentCulture);speed.SelectedValue=s.Speed;deinterlace.SelectedValue=s.Deinterlace;audio.SelectedValue=s.Audio;
        frameRate.SelectedValue=s.FrameRate.ToString(CultureInfo.InvariantCulture);outputFolder.Text=s.OutputFolder;logFolder.Text=s.NicoJkLogFolder;embed.IsChecked=s.EmbedComments;
        passes.SelectedValue=s.Passes.ToString(CultureInfo.InvariantCulture);container.SelectedValue=s.Container;assStyle.Load(s.AssStyle);externalAss.IsChecked=s.SaveExternalAss;
        pastLogs.IsChecked=s.UsePastLogComments;
        extraArguments.Load(s);
        UpdateQualityRange();UpdateRateControls();UpdateEmbedControls();
    }
    void UpdateEmbedControls()
    {
        pastLogs.IsEnabled=embed.IsChecked==true || externalAss.IsChecked==true;
        assStyle.SetAssEnabled(externalAss.IsChecked==true || embed.IsChecked==true && (string?)container.SelectedValue=="MKV");
        if(commentPathRow!=null){commentPathRow.IsEnabled=embed.IsChecked==true || externalAss.IsChecked==true;commentPathRow.Opacity=commentPathRow.IsEnabled?1:.45;}
    }
    async Task CheckGpu()
    {
        string selectedBackend=(string)backend.SelectedValue;
        if(cancellation!=null || !EncoderCapabilities.NeedsProbe((string)codec.SelectedValue,selectedBackend))return;
        string path=ffmpeg.Text.Trim().Trim('"'),selectedCodec=(string)codec.SelectedValue;
        if(!File.Exists(path))return; // Check after FFmpeg has been configured.
        bool available=await EncoderCapabilities.GpuAvailable(path,selectedCodec,selectedBackend);
        if(!available && cancellation==null && path==ffmpeg.Text.Trim().Trim('"') && selectedCodec==(string?)codec.SelectedValue && (string?)backend.SelectedValue==selectedBackend)
        {
            if(extraArguments.EnabledSwitch.IsChecked==true && !string.IsNullOrWhiteSpace(extraArguments.Input.Text))
            {status.Text=$"{selectedCodec}の{selectedBackend}を利用できません。追加パラメータが有効なため、処理方式を自動変更しません。";return;}
            backend.SelectedValue="CPU";status.Text=$"{selectedCodec}の{selectedBackend}を利用できないため、処理方式をCPUに変更しました。";
        }
    }
    void UpdateQualityRange()
    {
        string format=(string?)codec.SelectedValue??"H265",engine=(string?)backend.SelectedValue??"CPU";
        int min=EncoderSettings.QualityMinimum(format,engine),max=EncoderSettings.QualityMaximum(format,engine);
        quality.Maximum=max;quality.Minimum=min;quality.Value=Math.Clamp(quality.Value,min,max);
        quality.ToolTip=$"品質値 {min}～{max}。小さいほど高画質。方式間で同じ数値の画質は一致しません。"+(engine=="NVENC"?" 0は自動です。":"");
    }
    FrameworkElement FolderField(TextBox box,string title)
    {
        var panel=new DockPanel();var browse=ActionButton("参照",()=>
        {
            var dialog=new OpenFolderDialog {Title=title,InitialDirectory=Directory.Exists(box.Text)?box.Text:EncoderPaths.Root};if(dialog.ShowDialog(this)==true)box.Text=dialog.FolderName;
        });browse.Margin=new Thickness(10,0,0,0);browse.MinWidth=76;DockPanel.SetDock(browse,Dock.Right);panel.Children.Add(browse);panel.Children.Add(box);return panel;
    }
    void UpdateRateControls()
    {
        quality.IsEnabled=(string?)rate.SelectedValue=="Quality";bitrate.IsEnabled=!quality.IsEnabled;
        if(qualityField!=null)qualityField.Visibility=quality.IsEnabled?Visibility.Visible:Visibility.Collapsed;
        if(bitrateField!=null)bitrateField.Visibility=bitrate.IsEnabled?Visibility.Visible:Visibility.Collapsed;
        UpdatePassControls();
    }
    void UpdatePassControls()
    {
        string? selected=(string?)backend.SelectedValue;
        passes.IsEnabled=selected=="NVENC" || selected=="CPU" && (string?)rate.SelectedValue=="Bitrate";
        if(!passes.IsEnabled)passes.SelectedValue="1";
        passHelp.Text=selected switch
        {
            "NVENC"=>"2パスは各フレームを2段階で処理します（フル解像度）。",
            "CPU" when passes.IsEnabled=>"2パスは全編を解析してからエンコードします。",
            "CPU"=>"CPUの2パスはビットレート指定で選べます。",
            _=>"この処理方式は1パスのみ対応しています。"
        };
        passes.ToolTip=passHelp.Text;
    }
    async void DownloadFfmpeg()
    {
        if(MessageBox.Show(this,"FFmpeg（Windows 64-bit / essentials）をダウンロードします。\n\n配布元: https://www.gyan.dev/ffmpeg/builds/\n保存先: "+FfmpegInstaller.InstallRoot+"\n\nダウンロード・展開後、FFmpegのパスを自動設定して保存します。続けますか？","FFmpegのダウンロード",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        var dialog=new FfmpegDownloadWindow {Owner=this};
        if(dialog.ShowDialog()!=true || dialog.InstalledPath==null)return;
        ffmpeg.Text=dialog.InstalledPath;
        try{EncoderSettings.SaveFfmpegPath(ffmpeg.Text);}
        catch(Exception ex){MessageBox.Show(this,"ダウンロードは完了しましたが、パスを保存できませんでした。\n"+ex.Message,"NicoBE Encoder",MessageBoxButton.OK,MessageBoxImage.Error);}
        await CheckGpu();
    }
    FrameworkElement SettingsTab(FrameworkElement panel)
    {
        var layout=new DockPanel();
        var footer=new DockPanel {Margin=new Thickness(0,12,0,0)};
        DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        var save=ActionButton("保存",SaveSettings);save.MinWidth=100;save.Margin=new Thickness(16,0,0,0);
        save.SetResourceReference(StyleProperty,"PrimaryButton");
        save.SetBinding(IsEnabledProperty,new Binding(nameof(IsEnabled)) {Source=start});
        DockPanel.SetDock(save,Dock.Right);footer.Children.Add(save);
        var message=Note("すべての設定を保存します。エンコード開始時にも自動保存します。");
        message.Margin=new Thickness(0);message.VerticalAlignment=VerticalAlignment.Center;saveMessages.Add(message);footer.Children.Add(message);
        var scroll=ReferenceEquals(panel,basicPanel)?basicScroll:new ScrollViewer();
        scroll.Content=panel;scroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;scroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;layout.Children.Add(scroll);
        return layout;
    }
    void SaveSettings()
    {
        if(cancellation!=null)return;
        try
        {
            ReadSettings().Save(requireFiles:false);
            foreach(var message in saveMessages){message.Text="設定を保存しました。";message.Foreground=SettingsTheme.Muted;}
        }
        catch(Exception ex)
        {
            foreach(var message in saveMessages){message.Text="保存できませんでした: "+ex.Message;message.Foreground=errorText.Foreground;}
        }
    }
    EncoderSettings ReadSettings()
    {
        if(!double.TryParse(bitrate.Text,out double mbps))
        {if((string?)rate.SelectedValue=="Bitrate")throw new Exception("ビットレートを数値で入力してください。");mbps=1.5;}
        if((string?)rate.SelectedValue=="Quality" && (!double.IsFinite(mbps)||mbps<.5||mbps>100))mbps=1.5;
        int width=int.Parse((string)resolution.SelectedValue,CultureInfo.InvariantCulture);
        var settings=new EncoderSettings {Ffmpeg=ffmpeg.Text.Trim().Trim('"'),FfmpegGuideAcknowledged=ffmpegGuideAcknowledged,Codec=(string)codec.SelectedValue,Backend=(string)backend.SelectedValue,Width=width,Height=width*9/16,
            RateControl=(string)rate.SelectedValue,Quality=(int)quality.Value,BitrateMbps=mbps,Speed=(string)speed.SelectedValue,Deinterlace=(string)deinterlace.SelectedValue,Audio=(string)audio.SelectedValue,
            Passes=int.Parse((string)passes.SelectedValue,CultureInfo.InvariantCulture),Container=(string)container.SelectedValue,AssStyle=assStyle.Read(),
            FrameRate=int.Parse((string)frameRate.SelectedValue,CultureInfo.InvariantCulture),OutputFolder=outputFolder.Text.Trim().Trim('"'),NicoJkLogFolder=logFolder.Text.Trim().Trim('"'),EmbedComments=embed.IsChecked==true,SaveExternalAss=externalAss.IsChecked==true,UsePastLogComments=pastLogs.IsChecked==true};
        extraArguments.Store(settings);return settings;
    }
    async Task Encode()
    {
        if(cancellation!=null)return;
        var warnings=new List<string>();
        try
        {
            var pending=queue.Where(q=>q.State!="完了").ToArray();if(pending.Length==0){status.Text="エンコードするTSを追加してください。";return;}
            var settings=ReadSettings();settings.Validate();
            queueSaveFailed=false;recovery??=EncoderQueueRecovery.Create();recovery.Save(queue.Select(q=>q.Path),settings);
            RuntimeWatch.Start();
            if(RuntimeWatch.RestartRequired){ShowRuntimeWarning(RuntimeWatch.Reason!);return;}
            cancellation=new();start.IsEnabled=false;cancel.IsEnabled=true;settingsPanel.IsEnabled=false;assStyle.IsEnabled=false;basicPanel.IsEnabled=false;queueActions.IsEnabled=false;ShowError("");ShowWarnings(warnings);status.Foreground=SettingsTheme.Muted;
            status.Text="エンコーダーを確認しています…";
            if(await EncoderCapabilities.Resolve(settings,cancellation.Token)){backend.SelectedValue=settings.Backend;passes.SelectedValue=settings.Passes.ToString(CultureInfo.InvariantCulture);quality.Value=settings.Quality;}
            settings.Save();
            recovery.Save(queue.Select(q=>q.Path),settings);
            elapsed.Restart();UpdateElapsed();elapsedTimer.Start();progress.Value=0;detail.Text="";
            int completed=0,failed=0;
            foreach(var item in pending)
            {
                RuntimeWatch.CheckNow();
                if(cancellation.IsCancellationRequested||RuntimeWatch.RestartRequired||queueSaveFailed)break;
                item.Error="";bool itemFinished=false;
                var reporter=new Progress<EncodeProgress>(p=>{if(itemFinished)return;if(p.Command!=null)extraArguments.LastCommand=p.Command;item.State=p.Stage;status.Text=$"{Array.IndexOf(pending,item)+1}/{pending.Length}　{p.Stage} — {item.Name}";detail.Text=p.Detail;progress.Value=p.Fraction;});
                try
                {
                    var result=await RunJob(item.Path,settings,reporter,cancellation.Token);itemFinished=true;item.State="完了";completed++;queue.Remove(item);detail.Text="保存先: "+result.Output;
                    if(result.Warnings.Count>0)warnings.Add(item.Name+"\n"+string.Join("\n",result.Warnings)+"\n保存先: "+result.Output);
                }
                catch(OperationCanceledException ex){item.State="中止";item.Error=ex.Message;ShowError(item.Name+"\n"+ex.Message);break;}
                catch(Exception ex){item.State="失敗";item.Error=ex.Message;failed++;ShowError(item.Name+"\n"+ex.Message);}
                finally{itemFinished=true;}
            }
            RuntimeWatch.CheckNow();
            status.Text=$"{(RuntimeWatch.RestartRequired?"再起動が必要なため停止しました":queueSaveFailed?"キュー保存エラーで停止しました":cancellation.IsCancellationRequested?"中止しました":"処理が終了しました")}　完了 {completed}件 / 失敗 {failed}件 / 警告 {warnings.Count}件";
            status.Foreground=failed>0?errorText.Foreground:warnings.Count>0?warningText.Foreground:SettingsTheme.Muted;
        }
        catch(Exception ex){status.Text="エンコードを開始できませんでした。";status.Foreground=errorText.Foreground;ShowError(ex.Message);}
        finally
        {
            RuntimeWatch.Stop();
            if(RuntimeWatch.RestartRequired)warnings.Add(RuntimeWatch.Reason!+"\n未完了のキューは再起動後に復元します。");
            if(warnings.Count>0)ShowWarnings(warnings);
            elapsed.Stop();elapsedTimer.Stop();UpdateElapsed();
            cancellation?.Dispose();cancellation=null;start.IsEnabled=!RuntimeWatch.RestartRequired;cancel.IsEnabled=false;settingsPanel.IsEnabled=true;assStyle.IsEnabled=true;basicPanel.IsEnabled=true;queueActions.IsEnabled=true;if(closeAfterStop)Close();
        }
    }
    void ShowError(string text){errorText.Text=text;errorPanel.Visibility=text.Length>0?Visibility.Visible:Visibility.Collapsed;}
    void ShowWarnings(IReadOnlyList<string> warnings)
    {warningText.Text=warnings.Count==0?"":$"警告（{warnings.Count}件）\n\n"+string.Join("\n\n",warnings);warningPanel.Visibility=warnings.Count>0?Visibility.Visible:Visibility.Collapsed;}
    void UpdateElapsed(){var time=elapsed.Elapsed;elapsedText.Text=$"経過時間  {(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";}
    internal void PreviewFailure()
    {
        var item=new QueueItem {Path="サンプル.ts",State="失敗",Error="コメント同期の検証に失敗しました。\n動画は削除せず保持しました: 出力先\\サンプル_同期確認失敗.mp4\n処理ログ: encoder\\logs\\sample.log"};
        queue.Add(item);ShowError(item.Name+"\n"+item.Error);status.Text="処理が終了しました　完了 0件 / 失敗 1件";status.Foreground=errorText.Foreground;
        for(int i=1;i<=25;i++)queue.Add(new() {Path=$"録画番組_{i:00}.ts"});
        elapsedText.Text="経過時間  00:12:34";
        ShowWarnings(new[]{"コメントなしの録画.ts\n対象コメントが0件のため、コメントを保存せずに動画を出力しました。\n保存先: 出力先\\コメントなしの録画.mp4"});
        status.Text="処理が終了しました　完了 1件 / 失敗 1件 / 警告 1件";
        detail.Text="保存先: 出力先\\コメントなしの録画.mp4";
    }
    static ComboBox Select(params (string Value,string Label)[] choices)=>new() {ItemsSource=choices.Select(c=>new Choice(c.Value,c.Label)).ToArray(),DisplayMemberPath="Label",SelectedValuePath="Value",SelectedIndex=0,Height=36};
    static FrameworkElement Field(string label,FrameworkElement control)
    {
        var panel=new StackPanel {Margin=new Thickness(0,0,0,14)};
        panel.Children.Add(new TextBlock {Text=label,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,7)});panel.Children.Add(control);return panel;
    }
    static FrameworkElement Section(string title,UIElement content,bool collapsible=false)
    {
        var panel=new StackPanel();
        var heading=new TextBlock {Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)};
        if(collapsible)
        {
            content.Visibility=Visibility.Collapsed;
            var row=new DockPanel();var toggle=new Button {Content="開く ▼",FontSize=14,MinWidth=80,Padding=new Thickness(10,2,10,2),Margin=new Thickness(12,0,0,12),VerticalAlignment=VerticalAlignment.Top,ToolTip=title+"の内容を開く／閉じる"};
            toggle.Click+=(_,_)=>{bool open=content.Visibility!=Visibility.Visible;content.Visibility=open?Visibility.Visible:Visibility.Collapsed;toggle.Content=open?"閉じる ▲":"開く ▼";};
            DockPanel.SetDock(toggle,Dock.Right);row.Children.Add(toggle);row.Children.Add(heading);panel.Children.Add(row);
        }
        else panel.Children.Add(heading);
        panel.Children.Add(content);
        var border=new Border {Child=panel,BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Padding=new Thickness(16,14,16,2)};
        border.SetResourceReference(BackgroundProperty,"SurfaceBrush");return border;
    }
    static FrameworkElement Pair(FrameworkElement left,FrameworkElement right,double leftWidth=1,double rightWidth=1,double bottomMargin=14)
    {
        var grid=new Grid {Margin=new Thickness(0,0,0,bottomMargin)};grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(leftWidth,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(14)});grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(rightWidth,GridUnitType.Star)});
        grid.Children.Add(left);Grid.SetColumn(right,2);grid.Children.Add(right);return grid;
    }
    static FrameworkElement PathSection(string title,string description,FrameworkElement control,Button? action=null)
    {
        var content=new StackPanel();var heading=new DockPanel {MinHeight=28};
        if(action!=null){DockPanel.SetDock(action,Dock.Right);heading.Children.Add(action);}
        heading.Children.Add(new TextBlock {Text=title,FontSize=18,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        content.Children.Add(heading);var help=Note(description);help.Margin=new Thickness(0,6,0,12);content.Children.Add(help);content.Children.Add(control);
        var card=new Border {Child=content,Padding=new Thickness(16,12,16,16),Margin=new Thickness(0,0,0,12),CornerRadius=new CornerRadius(6),BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(1)};
        card.SetResourceReference(BackgroundProperty,"SurfaceBrush");return card;
    }
    static TextBlock Note(string text)=>new() {Text=text,Foreground=SettingsTheme.Muted,FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)};
    static TextBox SelectableText(string text,Brush? foreground=null)
    {
        var box=new TextBox {Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,
            FontSize=14,Foreground=foreground??SettingsTheme.Muted,Background=Brushes.Transparent,
            BorderThickness=new Thickness(0),Padding=new Thickness(0),Margin=new Thickness(0,4,0,0),
            VerticalContentAlignment=VerticalAlignment.Top,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,IsInactiveSelectionHighlightEnabled=true};
        var menu=new ContextMenu();
        menu.Items.Add(new MenuItem {Header="コピー",Command=ApplicationCommands.Copy,CommandTarget=box});
        menu.Items.Add(new MenuItem {Header="すべて選択",Command=ApplicationCommands.SelectAll,CommandTarget=box});
        box.ContextMenu=menu;return box;
    }
    static Button ActionButton(string title,Action action){var b=new Button {Content=title,Margin=new Thickness(0,0,8,0)};b.Click+=(_,_)=>action();return b;}
}
