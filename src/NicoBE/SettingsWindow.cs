using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace NicoBE;

public sealed class SettingsWindow : Window
{
    readonly Config original;
    readonly IStartupRegistration startup;
    readonly Action<Config> apply;
    readonly Action<Config>? persist;
    readonly Config liveConfig;
    readonly Grid layout=new();
    readonly DockPanel outer=new() {Margin=new Thickness(20,16,20,16),LastChildFill=true};
    readonly ScrollViewer generalScroll;
    readonly FrameworkElement autoLogRow,logFolderRow;
    internal FirstRunGuide? Guide {get;private set;}
    bool originalStartup;
    readonly NgSettingsPanel ng;
    readonly TextBox logPath=new() {Height=36};
    readonly ComboBox preference=new() {Height=36,ItemsSource=new[]{"オリジナル優先","ASS優先"}};
    readonly ComboBox font=new() {FontSize=16,MaxDropDownHeight=320,IsTextSearchEnabled=true,Height=36};
    sealed record FontChoice(string Source,string Label);
    sealed record WeightChoice(int Value,string Label);
    readonly ComboBox weight=new() {Height=36,DisplayMemberPath=nameof(WeightChoice.Label),SelectedValuePath=nameof(WeightChoice.Value),ItemsSource=new[]{new WeightChoice(400,"細い"),new WeightChoice(700,"標準"),new WeightChoice(900,"太い")}};
    readonly ComboBox outline=new() {Height=36,DisplayMemberPath=nameof(WeightChoice.Label),SelectedValuePath=nameof(WeightChoice.Value),ItemsSource=new[]{new WeightChoice(0,"なし"),new WeightChoice(1,"とても細い"),new WeightChoice(2,"細い"),new WeightChoice(3,"標準"),new WeightChoice(4,"太い"),new WeightChoice(5,"とても太い")}};
    readonly ComboBox shadow=new() {Height=36,DisplayMemberPath=nameof(WeightChoice.Label),SelectedValuePath=nameof(WeightChoice.Value),ItemsSource=new[]{new WeightChoice(0,"なし"),new WeightChoice(1,"とても小さい"),new WeightChoice(2,"小さい"),new WeightChoice(3,"標準"),new WeightChoice(4,"大きい"),new WeightChoice(5,"とても大きい")}};
    readonly CheckBox autostart=new() {Content="オフ",FontSize=16};
    readonly CheckBox accelerateLongComments=new() {Content="オン",FontSize=16};
    readonly CheckBox colorEmoji=new() {Content="オン",FontSize=16};
    readonly CheckBox autoPastLogs=new() {Content="自動取得",FontSize=16};
    readonly CheckBox externalAssPrefix=new() {Content="読み込む",FontSize=16};
    readonly CheckBox playbackPanelVisible=new() {Content="オン",FontSize=16};
    readonly CheckBox keepPlaybackSpeed=new() {Content="オン",FontSize=16};
    readonly CheckBox commentsVisible=new() {Content="表示",FontSize=16};
    readonly Slider size=new(),spacing=new(),speed=new(),advance=new(),opacity=new(),fixedTime=new(),maximumComments=new();
    readonly TextBlock error=new() {FontSize=14,TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0)};
    internal TabControl Tabs {get;}=new();
    readonly TabItem diagnosticsTab=new() {Header="診断"};
    readonly TextBox diagnosticsText=new() {IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalContentAlignment=VerticalAlignment.Top,Padding=new Thickness(10),FontSize=16};
    internal void ShowDiagnostics()=>Tabs.SelectedItem=diagnosticsTab;
    public SettingsWindow(Config current,Action<Config> apply,IStartupRegistration? startup=null,Func<NgLogSnapshot?>? readNgLog=null,Func<string>? readDiagnostics=null,Action<Config>? persist=null)
    {
        original=current.Clone();liveConfig=current;this.apply=apply;this.persist=persist;this.startup=startup??new WindowsStartupRegistration();
        originalStartup=string.Equals(this.startup.Read(),SettingsCommit.StartupCommand,StringComparison.OrdinalIgnoreCase);
        Title="NicoBE — 設定";Width=800;Height=Math.Min(760,SystemParameters.WorkArea.Height-40);MinWidth=720;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        FontFamily=new FontFamily("Yu Gothic UI");FontSize=16;Icon=AppAssets.WindowIcon;
        SettingsTheme.Apply(this);error.SetResourceReference(TextBlock.ForegroundProperty,"ErrorBrush");
        Content=layout;layout.Children.Add(outer);
        var footer=new Border {BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,14,0,0),Margin=new Thickness(0,12,0,0)};
        DockPanel.SetDock(footer,Dock.Bottom);outer.Children.Add(footer);
        var actions=new DockPanel();footer.Child=actions;
        var buttons=new StackPanel {Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Right);actions.Children.Add(buttons);
        var cancel=new Button {Content="キャンセル",MinWidth=108,Margin=new Thickness(0,0,10,0),IsCancel=true};cancel.Click+=(_,_)=>Close();buttons.Children.Add(cancel);
        var applyButton=new Button {Content="反映",MinWidth=92,Margin=new Thickness(0,0,10,0),ToolTip="設定を保存して反映します。設定画面は開いたままにします。"};applyButton.Click+=(_,_)=>Apply(false);buttons.Children.Add(applyButton);
        var save=new Button {Content="反映して閉じる",MinWidth=140,IsDefault=true};save.SetResourceReference(StyleProperty,"PrimaryButton");save.Click+=(_,_)=>Apply(true);buttons.Children.Add(save);
        var reset=new Button {Content="初期値に戻す",HorizontalAlignment=HorizontalAlignment.Left,ToolTip="文字の太さ・文字サイズ・行間・文字速度・長文加速・カラー絵文字・時間調整を初期値に戻します。"};
        reset.Click+=(_,_)=>{accelerateLongComments.IsChecked=true;colorEmoji.IsChecked=true;weight.SelectedValue=700;size.Value=100;spacing.Value=Config.DefaultLineSpacingPercent;speed.Value=Config.DefaultScrollSpeedPercent;advance.Value=0;};actions.Children.Add(reset);
        DockPanel.SetDock(error,Dock.Bottom);outer.Children.Add(error);outer.Children.Add(Tabs);
        var body=new StackPanel {Margin=new Thickness(0,0,6,0)};
        var scroll=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        generalScroll=scroll;
        Tabs.Items.Add(new TabItem {Header="一般設定",Content=scroll});
        ng=new NgSettingsPanel(current,readNgLog??(()=>null));
        var ngTab=new TabItem {Header="NG設定",Content=ng};Tabs.Items.Add(ngTab);
        var diagnosticBody=new DockPanel();
        var refresh=new Button {Content="更新",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,0,12)};
        void RefreshDiagnostics()=>diagnosticsText.Text=readDiagnostics?.Invoke()??"エラーの記録はありません。\nログ: "+Paths.Logs;
        refresh.Click+=(_,_)=>RefreshDiagnostics();DockPanel.SetDock(refresh,Dock.Top);diagnosticBody.Children.Add(refresh);diagnosticBody.Children.Add(diagnosticsText);
        diagnosticsTab.Content=diagnosticBody;Tabs.Items.Add(diagnosticsTab);
        var licenseTab=LicensePanel.Create("NicoBE");Tabs.Items.Add(licenseTab);
        Tabs.SelectionChanged+=(_,e)=>
        {
            if(!ReferenceEquals(e.Source,Tabs))return;
            reset.Visibility=ngTab.IsSelected || licenseTab.IsSelected || diagnosticsTab.IsSelected?Visibility.Hidden:Visibility.Visible;
            save.Visibility=licenseTab.IsSelected || diagnosticsTab.IsSelected?Visibility.Collapsed:Visibility.Visible;
            applyButton.Visibility=save.Visibility;
            cancel.Content=licenseTab.IsSelected || diagnosticsTab.IsSelected?"閉じる":"キャンセル";
            if(ngTab.IsSelected)ng.Capture();
            if(diagnosticsTab.IsSelected)RefreshDiagnostics();
        };
        commentsVisible.Checked+=(_,_)=>commentsVisible.Content="表示";commentsVisible.Unchecked+=(_,_)=>commentsVisible.Content="非表示";
        SetCommentsVisible(current.CommentsVisible);
        body.Children.Add(Row("コメントを表示",commentsVisible,"接続中のすべてのプレイヤーに適用します。"));
        autostart.Checked+=(_,_)=>autostart.Content="オン";autostart.Unchecked+=(_,_)=>autostart.Content="オフ";autostart.IsChecked=originalStartup;
        body.Children.Add(Row("Windows起動時に常駐",autostart,"オンにして保存すると、サインイン時に自動起動します。"));
        accelerateLongComments.Checked+=(_,_)=>accelerateLongComments.Content="オン";accelerateLongComments.Unchecked+=(_,_)=>accelerateLongComments.Content="オフ";
        accelerateLongComments.IsChecked=current.AccelerateLongComments;
        body.Children.Add(Row("長文コメントを加速",accelerateLongComments,"1文字につき2％加速、最大100％増（2倍）。初期値はオンです。"));
        colorEmoji.Checked+=(_,_)=>colorEmoji.Content="オン";colorEmoji.Unchecked+=(_,_)=>colorEmoji.Content="オフ";
        colorEmoji.IsChecked=current.ColorEmoji;
        body.Children.Add(Row("カラー絵文字",colorEmoji,"Windows対応の絵文字をカラー表示します。非対応の文字は単色表示。初期値はオンです。"));
        playbackPanelVisible.Checked+=(_,_)=>playbackPanelVisible.Content="オン";playbackPanelVisible.Unchecked+=(_,_)=>playbackPanelVisible.Content="オフ";
        keepPlaybackSpeed.Checked+=(_,_)=>keepPlaybackSpeed.Content="オン";keepPlaybackSpeed.Unchecked+=(_,_)=>keepPlaybackSpeed.Content="オフ";
        keepPlaybackSpeed.IsChecked=current.KeepPlaybackSpeed;
        body.Children.Add(Row("再生速度を引き継ぐ",keepPlaybackSpeed,"動画の切り替え後も同じ倍率で再生します。MPC-BE側の変更も対象。初期値はオンです。"));
        playbackPanelVisible.IsChecked=current.PlaybackPanelVisible;
        body.Children.Add(Row("操作パネルを表示",playbackPanelVisible,"映像の左下で秒送り・倍速を操作できます。初期値はオンです。"));
        var pathRow=new DockPanel();var browse=new Button {Content="参照",Margin=new Thickness(10,0,0,0)};
        browse.Click+=(_,_)=>
        {
            var dialog=new OpenFolderDialog {Title="jk4などのフォルダが入ったNicoJKフォルダを選択",InitialDirectory=System.IO.Directory.Exists(logPath.Text)?logPath.Text:""};
            if(dialog.ShowDialog(this)==true)logPath.Text=dialog.FolderName;
        };
        DockPanel.SetDock(browse,Dock.Right);pathRow.Children.Add(browse);logPath.Text=current.NicoJkLogFolder;pathRow.Children.Add(logPath);
        autoPastLogs.IsChecked=current.AutoFetchPastLogs;
        externalAssPrefix.IsChecked=current.MatchExternalAssPrefix;
        body.Children.Add(Row("前方一致の外部ASS",externalAssPrefix,"ASSがない場合、動画名で始まる外部ASSを探します。"));
        autoLogRow=Row("過去ログの自動取得",autoPastLogs,"保存済みの実況コメントがない場合のみ取得します。");body.Children.Add(autoLogRow);
        logFolderRow=Row("NicoJK ログパス",pathRow,"jk4などを含むフォルダ。埋め込みコメントやASSのみの場合は指定不要です。");body.Children.Add(logFolderRow);
        preference.SelectedIndex=current.CommentPreference=="ASS"?1:0;
        body.Children.Add(Row("コメントの優先形式",preference,"MP4のオリジナルと外部ASSがある場合の優先順です。"));
        weight.SelectedValue=current.FontWeight;
        body.Children.Add(Row("フォント",FontField(current.FontFamily),"コメントに使用するフォントと表示例です。"));
        body.Children.Add(Row("文字の太さ",weight,"細い：400　標準：700　太い：900（対応フォントのみ）"));
        outline.SelectedValue=current.OutlineLevel;
        body.Children.Add(Row("縁取りの太さ",outline,"なし＋5段階。初期値：とても細い。カラー絵文字は対象外です。"));
        shadow.SelectedValue=current.ShadowLevel;
        body.Children.Add(Row("影の大きさ",shadow,"右下に黒い影を付けます。なし＋5段階。初期値：小さい。カラー絵文字は対象外です。"));
        body.Children.Add(SliderField("文字サイズ",size,50,300,5,current.FontSizePercent,v=>$"{v:0} %","100％が標準の文字サイズです。"));
        body.Children.Add(SliderField("文字速度",speed,Config.MinScrollSpeedPercent,300,5,current.ScrollSpeedPercent,v=>$"{v:0} %","動画の再生倍率とは独立して調整できます。"));
        body.Children.Add(SliderField("行間",spacing,0,100,5,current.LineSpacingPercent,v=>$"{v:0} %","行と行の余白を調整します。初期値30％です。"));
        body.Children.Add(SliderField("時間調整",advance,-10,10,.1,current.AdvanceSeconds,v=>$"{v:+0.0;-0.0;0.0} 秒","−10秒：遅く表示　0秒：調整なし　＋10秒：早く表示"));
        body.Children.Add(SliderField("不透明度",opacity,10,100,5,current.Opacity*100,v=>$"{v:0} %","文字・縁取り・影の濃さを調整します。"));
        body.Children.Add(SliderField("固定コメントの表示時間",fixedTime,1,30,1,current.FixedCommentSeconds,v=>$"{v:0} 秒","上・下固定コメントを表示する時間です。"));
        body.Children.Add(SliderField("同時表示数の上限",maximumComments,1,300,1,current.MaxVisibleComments,v=>$"{v:0} 件","同時に表示するコメント数を制限します。"));
        Loaded+=(_,_)=>Dispatcher.BeginInvoke(()=>{if(IsVisible)ShowFirstRunGuide();},System.Windows.Threading.DispatcherPriority.Loaded);
    }
    internal void ShowFirstRunGuide()
    {
        if(original.SetupCompleted || original.FirstRunGuideAcknowledged || Guide!=null)return;
        Tabs.SelectedIndex=0;layout.UpdateLayout();
        generalScroll.ScrollToVerticalOffset(generalScroll.VerticalOffset+autoLogRow.TranslatePoint(new Point(),generalScroll).Y-18);
        generalScroll.UpdateLayout();
        Guide=new FirstRunGuide(autoLogRow,logFolderRow,AcknowledgeFirstRunGuide);
        outer.IsHitTestVisible=false;KeyboardNavigation.SetTabNavigation(outer,KeyboardNavigationMode.None);
        layout.Children.Add(Guide);layout.UpdateLayout();Guide.Accept.Focus();
    }
    internal void AcknowledgeFirstRunGuide()
    {
        // Save only the acknowledgement, never uncommitted edits in the form.
        var saved=liveConfig.Clone();saved.FirstRunGuideAcknowledged=true;
        if(persist!=null)persist(saved);else saved.Save();
        liveConfig.FirstRunGuideAcknowledged=true;original.FirstRunGuideAcknowledged=true;
        if(Guide!=null){Guide.Accept.IsDefault=false;layout.Children.Remove(Guide);Guide.Detach();Guide=null;}
        outer.IsHitTestVisible=true;KeyboardNavigation.SetTabNavigation(outer,KeyboardNavigationMode.Continue);autoPastLogs.Focus();
    }
    static TextBlock Note(string text)=>new() {Text=text,FontSize=14,Foreground=SettingsTheme.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)};
    internal void SetCommentsVisible(bool visible){original.CommentsVisible=visible;commentsVisible.IsChecked=visible;}
    internal void SetAutoPastLogs(bool enabled){original.AutoFetchPastLogs=enabled;autoPastLogs.IsChecked=enabled;}
    static FrameworkElement Row(string label,FrameworkElement control,string note)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(176)});grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        grid.Children.Add(new TextBlock {Text=label,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,14,0)});
        control.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(control,1);grid.Children.Add(control);
        var help=Note(note);Grid.SetRow(help,1);Grid.SetColumn(help,1);grid.Children.Add(help);
        return new Border {Child=grid,BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(0,8,0,9)};
    }
    FrameworkElement FontField(string current)
    {
        var ja=System.Windows.Markup.XmlLanguage.GetLanguage("ja-JP");
        var choices=Fonts.SystemFontFamilies.Where(f=>!f.Source.StartsWith('@'))
            .Select(f=>new FontChoice(f.Source,f.FamilyNames.TryGetValue(ja,out string? name)?name:f.Source))
            .DistinctBy(f=>f.Source,StringComparer.OrdinalIgnoreCase).OrderBy(f=>f.Label,StringComparer.CurrentCulture).ToList();
        if(!choices.Any(f=>f.Source.Equals(current,StringComparison.OrdinalIgnoreCase)))choices.Insert(0,new(current,current));
        font.ItemsSource=choices;font.DisplayMemberPath=nameof(FontChoice.Label);font.SelectedValuePath=nameof(FontChoice.Source);
        var sample=new TextBlock {Text="あいうえお ABC 123",FontSize=18,FontWeight=FontWeight.FromOpenTypeWeight((int)weight.SelectedValue),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
        weight.SelectionChanged+=(_,_)=>{if(weight.SelectedValue is int value)sample.FontWeight=FontWeight.FromOpenTypeWeight(value);};
        font.SelectionChanged+=(_,_)=>{if(font.SelectedItem is FontChoice choice)sample.FontFamily=new FontFamily(choice.Source);};
        font.SelectedItem=choices.First(f=>f.Source.Equals(current,StringComparison.OrdinalIgnoreCase));
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(190)});
        row.Children.Add(font);var preview=new Border {Child=sample,BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(8,4,8,4),Margin=new Thickness(10,0,0,0)};
        Grid.SetColumn(preview,1);row.Children.Add(preview);return row;
    }
    static FrameworkElement SliderField(string label,Slider slider,double min,double max,double step,double value,Func<double,string> format,string note)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(112)});
        var number=new TextBox {FontSize=16,Height=36,TextAlignment=TextAlignment.Right,Margin=new Thickness(16,0,0,0),ToolTip=$"{min}～{max}。数値を直接入力できます。"};
        Grid.SetColumn(number,1);row.Children.Add(number);
        slider.Minimum=min;slider.Maximum=max;slider.TickFrequency=step;slider.IsSnapToTickEnabled=true;slider.SmallChange=step;slider.LargeChange=(max-min)/10;
        slider.ValueChanged+=(_,_)=>number.Text=format(slider.Value);slider.Value=Math.Clamp(value,min,max);number.Text=format(slider.Value);row.Children.Add(slider);
        void Commit()
        {
            string input=number.Text.Replace("%","").Replace("％","").Replace("秒","").Trim();
            if(double.TryParse(input,NumberStyles.Float,CultureInfo.CurrentCulture,out double parsed) && double.IsFinite(parsed))
                slider.Value=Math.Clamp(min+Math.Round((parsed-min)/step)*step,min,max);
            number.Text=format(slider.Value);
        }
        number.LostKeyboardFocus+=(_,_)=>Commit();number.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}};
        return Row(label,row,note);
    }
    internal void Apply(bool close)
    {
        try
        {
            Keyboard.ClearFocus();
            var candidate=original.Clone();candidate.NicoJkLogFolder=logPath.Text.Trim().Trim('"');
            candidate.FontFamily=((FontChoice)font.SelectedItem).Source;
            candidate.FontWeight=(int)weight.SelectedValue;
            candidate.OutlineLevel=(int)outline.SelectedValue;
            candidate.ShadowLevel=(int)shadow.SelectedValue;
            candidate.AccelerateLongComments=accelerateLongComments.IsChecked==true;
            candidate.ColorEmoji=colorEmoji.IsChecked==true;
            candidate.PlaybackPanelVisible=playbackPanelVisible.IsChecked==true;
            candidate.KeepPlaybackSpeed=keepPlaybackSpeed.IsChecked==true;
            candidate.CommentsVisible=commentsVisible.IsChecked==true;
            candidate.AutoFetchPastLogs=autoPastLogs.IsChecked==true;
            candidate.MatchExternalAssPrefix=externalAssPrefix.IsChecked==true;
            candidate.CommentPreference=preference.SelectedIndex==1?"ASS":"Original";
            candidate.FontSizePercent=size.Value;candidate.LineSpacingPercent=spacing.Value;candidate.ScrollSpeedPercent=speed.Value;candidate.AdvanceSeconds=advance.Value;
            candidate.Opacity=opacity.Value/100;candidate.FixedCommentSeconds=fixedTime.Value;candidate.MaxVisibleComments=(int)maximumComments.Value;
            ng.SaveTo(candidate);
            candidate.Validate(false);
            SettingsCommit.Save(candidate,autostart.IsChecked==true,originalStartup,startup,persist);
            original.CopyFrom(candidate);originalStartup=autostart.IsChecked==true;
            apply(candidate);
            error.Visibility=Visibility.Collapsed;
            if(close)Close();
        }
        catch(Exception ex){error.Text=ex.Message;error.Visibility=Visibility.Visible;}
    }
}
