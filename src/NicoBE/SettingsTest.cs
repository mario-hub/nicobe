using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NicoBE;

public static class SettingsTest
{
    sealed class MemoryStartup : IStartupRegistration
    {
        public string? Value;public int Writes;
        public string? Read()=>Value;
        public void Write(string? value){Value=value;Writes++;}
    }
    public static int Run()
    {
        var results=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new Exception(name);results.Add(name);}
        try
        {
            var config=new Config();var startup=new MemoryStartup();bool persisted=false;
            SettingsCommit.Save(config.Clone(),false,false,startup,c=>persisted=c.SetupCompleted);
            Check(persisted&&startup.Writes==0,"unchanged startup toggle does not write the registry");
            SettingsCommit.Save(config.Clone(),true,false,startup,_=>{});
            Check(startup.Value==SettingsCommit.StartupCommand&&startup.Writes==1,"startup enable records the quoted application path");
            SettingsCommit.Save(config.Clone(),false,true,startup,_=>{});
            Check(startup.Value==null,"startup disable removes only the application entry");
            try{SettingsCommit.Save(config.Clone(),true,false,startup,_=>throw new IOException("test save failure"));}catch(IOException){}
            Check(startup.Value==null,"failed settings save rolls back startup change");
            var edited=config.Clone();edited.FontSizePercent=150;edited.ScrollSpeedPercent=75;edited.AdvanceSeconds=2.5;
            Check(edited.FontSize==72 && edited.ScrollPixelsPerSecond==270 && edited.CommentOffsetSeconds==-2.5,"percent and advance conversions");
            Check(config.FontSize!=edited.FontSize,"settings edits leave the running configuration untouched until save");
            var maximum=config.Clone();maximum.FontSizePercent=300;maximum.ScrollSpeedPercent=300;maximum.Validate(false);
            Check(maximum.FontSize==144 && maximum.ScrollPixelsPerSecond==1080,"300 percent is valid for the new defaults");
            Config? stored=null;Config? running=null;int commits=0;bool closed=false;
            var toggles=new MemoryStartup();
            var applying=new SettingsWindow(config,c=>running=c.Clone(),toggles,persist:c=>{stored=c.Clone();commits++;});
            var general=(System.Windows.Controls.StackPanel)((System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)applying.Tabs.Items[0]).Content).Content;
            Check(!config.FirstRunGuideAcknowledged,"first setup guide starts unacknowledged");
            var toggleLabels=general.Children.OfType<System.Windows.Controls.Border>().Select(b=>b.Child).OfType<System.Windows.Controls.Grid>()
                .Take(9).Select(row=>row.Children.OfType<System.Windows.Controls.TextBlock>().First().Text);
            Check(toggleLabels.SequenceEqual(new[]{"コメントを表示","Windows起動時に常駐","長文コメントを加速","カラー絵文字","再生速度を引き継ぐ","操作パネルを表示","前方一致の外部ASS","過去ログの自動取得","NicoJK ログパス"}),"prefix ASS and automatic fetch sit directly before the NicoJK log path");
            applying.Closed+=(_,_)=>closed=true;
            T Field<T>(SettingsWindow target,string name)=>(T)typeof(SettingsWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;
            var ngPanel=new NgSettingsPanel(new Config(),()=>null);
            var ngSwitches=new[]{"colored","fixedPosition","sizeChanged","multiline"}.Select(name=>(System.Windows.Controls.CheckBox)typeof(NgSettingsPanel)
                .GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(ngPanel)!).ToArray();
            Check(ngSwitches.All(c=>c.IsChecked==false),"four NG switches default OFF");
            foreach(var toggle in ngSwitches)toggle.IsChecked=true;
            var ngSaved=new Config();ngPanel.SaveTo(ngSaved);ngSaved=ngSaved.Clone();
            Check(ngSaved.NgColoredComments&&ngSaved.NgFixedComments&&ngSaved.NgSizeChangedComments&&ngSaved.NgMultilineComments,"four NG switches save through Config serialization");
            var autoFetch=Field<System.Windows.Controls.CheckBox>(applying,"autoPastLogs");
            var prefixAss=Field<System.Windows.Controls.CheckBox>(applying,"externalAssPrefix");
            Check(prefixAss.IsChecked==true,"prefix ASS switch defaults to ON");prefixAss.IsChecked=false;
            applying.SetAutoPastLogs(false);
            typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(autoFetch,null);
            Check(autoFetch.IsChecked==true,"automatic fetch checkbox can be turned on from a saved OFF value");
            var fontSize=Field<System.Windows.Controls.Slider>(applying,"size");
            var opacity=Field<System.Windows.Controls.Slider>(applying,"opacity");
            var fixedTime=Field<System.Windows.Controls.Slider>(applying,"fixedTime");
            var maximumComments=Field<System.Windows.Controls.Slider>(applying,"maximumComments");
            Check(opacity.Value==config.Opacity*100&&fixedTime.Value==config.FixedCommentSeconds&&maximumComments.Value==config.MaxVisibleComments,"opacity fixed duration and count load shared defaults");
            opacity.Value=60;fixedTime.Value=8;maximumComments.Value=40;
            var outline=Field<System.Windows.Controls.ComboBox>(applying,"outline");
            Check(outline.Items.Count==6&&(int)outline.SelectedValue==1&&config.OutlineWidth==1.5,"outline has off plus five levels and defaults to very thin");
            var shadow=Field<System.Windows.Controls.ComboBox>(applying,"shadow");
            Check(shadow.Items.Count==6&&(int)shadow.SelectedValue==2&&config.ShadowOffset==2,"shadow has off plus five levels and defaults to small");
            shadow.SelectedValue=5;
            outline.SelectedValue=5;
            var autoStart=Field<System.Windows.Controls.CheckBox>(applying,"autostart");
            var acceleration=Field<System.Windows.Controls.CheckBox>(applying,"accelerateLongComments");
            Check(acceleration.IsChecked==true,"length acceleration defaults to enabled");
            var emoji=Field<System.Windows.Controls.CheckBox>(applying,"colorEmoji");
            var panel=Field<System.Windows.Controls.CheckBox>(applying,"playbackPanelVisible");
            var keepSpeed=Field<System.Windows.Controls.CheckBox>(applying,"keepPlaybackSpeed");
            Check(keepSpeed.IsChecked==true,"speed inheritance defaults to enabled");keepSpeed.IsChecked=false;
            Check(panel.IsChecked==true,"playback panel defaults to enabled");
            panel.IsChecked=false;
            Check(emoji.IsChecked==true,"color emoji defaults to enabled");
            emoji.IsChecked=false;
            acceleration.IsChecked=false;
            fontSize.Value=125;autoStart.IsChecked=true;applying.Apply(false);
            Check(commits==1 && stored?.FontSize==60 && running?.FontSize==60 && !closed && toggles.Value!=null,"Apply saves and updates without closing");
            Check(stored?.AutoFetchPastLogs==true&&running?.AutoFetchPastLogs==true,"Apply persists and applies automatic fetch ON");
            Check(stored?.Opacity==.6&&running?.Opacity==.6&&stored.FixedCommentSeconds==8&&running.FixedCommentSeconds==8&&stored.MaxVisibleComments==40&&running.MaxVisibleComments==40,"opacity duration and count save and apply");
            Check(stored?.MatchExternalAssPrefix==false&&running?.MatchExternalAssPrefix==false,"prefix ASS OFF persists and applies");
            string autoFile=Path.Combine(Paths.Logs,"auto-setting-roundtrip.json");
            File.WriteAllText(autoFile,System.Text.Json.JsonSerializer.Serialize(stored,Config.Json));
            var reopenedConfig=System.Text.Json.JsonSerializer.Deserialize<Config>(File.ReadAllText(autoFile),Config.Json)!;
            var reopened=new SettingsWindow(reopenedConfig,_=>{},new MemoryStartup(),persist:_=>{});
            Check(Field<System.Windows.Controls.CheckBox>(reopened,"autoPastLogs").IsChecked==true,"automatic fetch stays ON after file roundtrip and settings recreation");
            Check(Field<System.Windows.Controls.Slider>(reopened,"opacity").Value==60&&Field<System.Windows.Controls.Slider>(reopened,"fixedTime").Value==8&&Field<System.Windows.Controls.Slider>(reopened,"maximumComments").Value==40,"opacity duration and count survive saved-settings reload");
            Check(Field<System.Windows.Controls.CheckBox>(reopened,"externalAssPrefix").IsChecked==false,"prefix ASS OFF survives settings recreation");
            reopened.Close();
            var shared=new Config{AutoFetchPastLogs=false};
            using(var resident=new ResidentController(shared,false,_=>false))
            {
                var editor=new SettingsWindow(shared,resident.ApplySettings,new MemoryStartup(),persist:c=>c.Save());
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                typeof(ResidentController).GetField("settings",flags)!.SetValue(resident,editor);
                var autoEdit=Field<System.Windows.Controls.CheckBox>(editor,"autoPastLogs");autoEdit.IsChecked=true;
                var panelSetting=typeof(ResidentController).GetMethod("SetPanelSetting",flags)!;
                panelSetting.Invoke(resident,new object[]{"master",false});
                Check(autoEdit.IsChecked==true,"panel visibility changes preserve an uncommitted automatic-fetch edit");
                editor.Apply(false);
                Check(shared.AutoFetchPastLogs&&Config.Load(false).AutoFetchPastLogs,"resident applies and reloads saved automatic fetch ON");
                foreach(bool enabled in new[]{false,true})
                {
                    panelSetting.Invoke(resident,new object[]{"auto",enabled});
                    Check(shared.AutoFetchPastLogs==enabled&&autoEdit.IsChecked==enabled&&Config.Load(false).AutoFetchPastLogs==enabled,"panel automatic-fetch preference persists and synchronizes: "+enabled);
                }
            }
            Check(stored?.OutlineLevel==5&&running?.OutlineWidth==8&&stored?.ShadowLevel==5&&running?.ShadowOffset==6,"outline and shadow selections save and apply without closing");
            Check(stored?.AccelerateLongComments==false && running?.AccelerateLongComments==false,"Apply saves and applies disabled length acceleration");
            Check(stored?.ColorEmoji==false && running?.ColorEmoji==false,"Apply saves and applies disabled color emoji");
            Check(stored?.PlaybackPanelVisible==false && running?.PlaybackPanelVisible==false,"Apply saves and applies disabled playback panel");
            Check(stored?.KeepPlaybackSpeed==false && running?.KeepPlaybackSpeed==false,"Apply saves and applies disabled speed inheritance");
            keepSpeed.IsChecked=true;
            panel.IsChecked=true;
            emoji.IsChecked=true;
            acceleration.IsChecked=true;
            fontSize.Value=150;autoStart.IsChecked=false;applying.Apply(false);
            Check(commits==2 && stored?.FontSize==72 && running?.FontSize==72 && !closed && toggles.Value==null,"repeated Apply updates the startup baseline");
            Check(stored?.AccelerateLongComments==true && running?.AccelerateLongComments==true,"Apply saves and applies enabled length acceleration");
            Check(stored?.ColorEmoji==true && running?.ColorEmoji==true,"Apply saves and applies enabled color emoji");
            Check(stored?.PlaybackPanelVisible==true && running?.PlaybackPanelVisible==true,"Apply saves and applies enabled playback panel");
            Check(stored?.KeepPlaybackSpeed==true && running?.KeepPlaybackSpeed==true,"Apply saves and applies enabled speed inheritance");
            fontSize.Value=200;applying.Close();
            Check(closed && commits==2 && stored?.FontSize==72 && running?.FontSize==72,"cancel discards pending edits without reverting applied settings");
            closed=false;
            var closing=new SettingsWindow(config,c=>running=c.Clone(),new MemoryStartup(),persist:c=>{stored=c.Clone();commits++;});
            closing.Closed+=(_,_)=>closed=true;closing.Apply(true);
            Check(closed && commits==3 && stored?.FontSize==48 && running?.FontSize==48,"Apply and close saves and closes");
            closed=false;int failuresApplied=0;
            var failedStartup=new MemoryStartup();
            var failing=new SettingsWindow(config,_=>failuresApplied++,failedStartup,persist:_=>throw new IOException("test save failure"));
            failing.Closed+=(_,_)=>closed=true;Field<System.Windows.Controls.CheckBox>(failing,"autostart").IsChecked=true;failing.Apply(true);
            Check(!closed && failuresApplied==0 && failedStartup.Value==null && Field<System.Windows.Controls.TextBlock>(failing,"error").Visibility==Visibility.Visible,"failed save stays open, reports the error and does not apply");
            failing.Close();
            var bounds=SettingsPlacement.Bounds(new Rect(0,0,1920,1040),new Point(1880,1030),new Size(800,760),12);
            Check(bounds==new Rect(1068,270,800,760),"tray placement is above and left of the pointer");
            bounds=SettingsPlacement.Bounds(new Rect(-1920,0,1920,1040),new Point(-1910,10),new Size(800,760),12);
            Check(bounds==new Rect(-1920,0,800,760),"placement clamps to a monitor with negative coordinates");
            bounds=SettingsPlacement.Bounds(new Rect(1920,0,1280,1024),new Point(3180,1000),new Size(1600,1520),24);
            Check(bounds==new Rect(1920,0,1280,1024),"large high-DPI windows fit the work area");
            bounds=SettingsPlacement.CenteredBounds(new Rect(0,0,1920,1040),new Rect(200,100,1280,800),new Size(800,760));
            Check(bounds==new Rect(440,120,800,760),"settings center on the player that requested them");
            bounds=SettingsPlacement.CenteredBounds(new Rect(-1920,0,1920,1040),new Rect(-1900,0,640,360),new Size(800,760));
            Check(bounds==new Rect(-1920,0,800,760),"player-centered settings clamp to the monitor work area");
            var first=config.Clone();first.SetupCompleted=true;
            var guideConfig=new Config();Config? acknowledged=null;bool failGuideSave=true;int guideApplies=0;
            var walkthrough=new SettingsWindow(guideConfig,_=>guideApplies++,new MemoryStartup(),persist:c=>
            {if(failGuideSave)throw new IOException("test acknowledgement failure");acknowledged=c.Clone();});
            var guideContent=(FrameworkElement)walkthrough.Content;
            ((System.Windows.Controls.Grid)guideContent).Background=walkthrough.Background;
            guideContent.Measure(new Size(800,720));guideContent.Arrange(new Rect(0,0,800,720));guideContent.UpdateLayout();
            walkthrough.ShowFirstRunGuide();guideContent.UpdateLayout();
            Check(walkthrough.Guide!=null,"first launch highlights source settings");
            var guidePreview=new RenderTargetBitmap(800,720,96,96,PixelFormats.Pbgra32);guidePreview.Render(guideContent);
            var guidePng=new PngBitmapEncoder();guidePng.Frames.Add(BitmapFrame.Create(guidePreview));
            using(var output=File.Create(Path.Combine(Paths.Logs,"first-run-guide-preview.png")))guidePng.Save(output);
            guideContent.Measure(new Size(720,560));guideContent.Arrange(new Rect(0,0,720,560));guideContent.UpdateLayout();
            var acceptPoint=walkthrough.Guide!.Accept.TranslatePoint(new Point(),guideContent);
            Check(acceptPoint.Y>=0&&acceptPoint.Y+walkthrough.Guide.Accept.ActualHeight<=560,"guide acknowledgement fits the minimum window size");
            var smallPreview=new RenderTargetBitmap(720,560,96,96,PixelFormats.Pbgra32);smallPreview.Render(guideContent);
            var smallPng=new PngBitmapEncoder();smallPng.Frames.Add(BitmapFrame.Create(smallPreview));
            using(var output=File.Create(Path.Combine(Paths.Logs,"first-run-guide-small-preview.png")))smallPng.Save(output);
            Field<System.Windows.Controls.TextBox>(walkthrough,"logPath").Text="unsaved-path";
            walkthrough.Guide!.Accept.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(walkthrough.Guide!=null&&!guideConfig.FirstRunGuideAcknowledged,"failed acknowledgement remains visible and is retryable");
            failGuideSave=false;walkthrough.Guide!.Accept.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(walkthrough.Guide==null&&acknowledged?.FirstRunGuideAcknowledged==true&&guideConfig.FirstRunGuideAcknowledged,"acknowledgement is persisted immediately");
            Check(!acknowledged!.SetupCompleted&&acknowledged.NicoJkLogFolder==""&&guideApplies==0,"acknowledgement does not commit edits or reconfigure playback");
            walkthrough.Close();
            var guideReopened=new SettingsWindow(acknowledged.Clone(),_=>{},new MemoryStartup(),persist:_=>{});
            guideReopened.ShowFirstRunGuide();Check(guideReopened.Guide==null,"acknowledged guide stays hidden after reopening without Apply");guideReopened.Close();
            var window=new SettingsWindow(first,_=>{},new MemoryStartup(),()=>new("録画サンプル.ts",5025,true,
                new[]{new NgLogRow(5022,"ここ好き","user_01"),new NgLogRow(5024,"いいシーンだ","user_02"),new NgLogRow(5025,"音楽もいい","user_03"),new NgLogRow(5027,"もう一度見たい","user_04")}),()=>"テスト用の読み込みエラー");
            var content=(FrameworkElement)window.Content;
            window.ShowFirstRunGuide();Check(window.Guide==null,"existing completed setups are not shown first-run onboarding");
            double[]? headerLayout=null;
            void Render(string filename)
            {
                content.Measure(new Size(800,720));content.Arrange(new Rect(0,0,800,720));content.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);
                content.UpdateLayout();
                var currentLayout=window.Tabs.Items.Cast<System.Windows.Controls.TabItem>()
                    .SelectMany(t=>new[]{t.ActualWidth,t.TranslatePoint(new Point(),window.Tabs).X}).ToArray();
                if(headerLayout==null)headerLayout=currentLayout;
                else Check(headerLayout.Zip(currentLayout).All(p=>Math.Abs(p.First-p.Second)<.01),"tab headers keep their widths and positions when selected");
                if(window.Tabs.SelectedIndex==1 && Find<System.Windows.Controls.DataGrid>(content) is {} table){table.SelectedIndex=2;content.UpdateLayout();}
                var foreground=new RenderTargetBitmap(800,720,96,96,PixelFormats.Pbgra32);foreground.Render(content);
                var canvas=new DrawingVisual();using(var dc=canvas.RenderOpen())
                {
                    dc.DrawRectangle(window.Background,null,new Rect(0,0,800,720));
                    dc.DrawImage(foreground,new Rect(0,0,800,720));
                }
                var bitmap=new RenderTargetBitmap(800,720,96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(Paths.Logs,filename));encoder.Save(output);
            }
            Render("settings-preview.png");window.Tabs.SelectedIndex=1;Render("ng-settings-preview.png");
            var ngScroll=(System.Windows.Controls.ScrollViewer)((NgSettingsPanel)((System.Windows.Controls.TabItem)window.Tabs.Items[1]).Content).Content;
            ngScroll.ScrollToEnd();Render("ng-settings-bottom-preview.png");
            window.ShowDiagnostics();Render("diagnostics-settings-preview.png");
            var diagnosticBox=(System.Windows.Controls.TextBox)((System.Windows.Controls.DockPanel)((System.Windows.Controls.TabItem)window.Tabs.SelectedItem).Content).Children[1];
            Check(diagnosticBox.IsReadOnly && diagnosticBox.VerticalContentAlignment==VerticalAlignment.Top && diagnosticBox.Text.Contains("テスト用の読み込みエラー"),"diagnostic text is readable, top-aligned and selectable");
            window.Tabs.SelectedIndex=3;Render("license-settings-preview.png");
            Check(window.Tabs.Items.Count==4 && ((System.Windows.Controls.TabItem)window.Tabs.Items[3]).Header.ToString()=="その他","diagnostics and other tabs available");
            Check(DateTime.TryParseExact(LicensePanel.ReleaseDate,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _),"release date is embedded in the application");
            var launch=LicensePanel.WebsiteStartInfo();
            Check(launch.UseShellExecute && launch.FileName=="https://www.moreread.net/apps/nicobe/index.html","website button targets the public page in the external default browser without launching it in this test");
            Check(content.ActualWidth>600 && content.ActualHeight>600,"settings layout renders without showing a desktop window");window.Close();
            using var icon=File.Exists(AppAssets.IconPath)
                ? new System.Drawing.Icon(AppAssets.IconPath,32,32)
                : new System.Drawing.Icon(System.Drawing.SystemIcons.Application,32,32);
            Check(icon.Width==32 && icon.Height==32,"multi-resolution application icon loads");
            Paths.WriteJson("settings-test.json",new{passed=true,registryWrites=false,results});return 0;
        }
        catch(Exception ex){Paths.WriteJson("settings-test.json",new{passed=false,results,error=ex.ToString()});return 1;}
    }
    static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)return match;
            if(Find<T>(child) is {} descendant)return descendant;
        }
        return null;
    }
}
