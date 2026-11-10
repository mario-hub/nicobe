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
            Check(general.Children[0] is System.Windows.Controls.Border notice && notice.Child is System.Windows.Controls.TextBlock text
                && text.Text.Contains(@"C:\TvTest\Plugins\NicoJK") && text.FontSize>=16,"first setup shows a readable NicoJK path prompt with an example");
            var toggleLabels=general.Children.OfType<System.Windows.Controls.Border>().Select(b=>b.Child).OfType<System.Windows.Controls.Grid>()
                .Take(5).Select(row=>row.Children.OfType<System.Windows.Controls.TextBlock>().First().Text);
            Check(toggleLabels.SequenceEqual(new[]{"コメントを表示","Windows起動時に常駐","長文コメントを加速","カラー絵文字","操作パネルを表示"}),"playback panel switch is last among the toggles");
            applying.Closed+=(_,_)=>closed=true;
            T Field<T>(SettingsWindow target,string name)=>(T)typeof(SettingsWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;
            var fontSize=Field<System.Windows.Controls.Slider>(applying,"size");
            var autoStart=Field<System.Windows.Controls.CheckBox>(applying,"autostart");
            var acceleration=Field<System.Windows.Controls.CheckBox>(applying,"accelerateLongComments");
            Check(acceleration.IsChecked==true,"length acceleration defaults to enabled");
            var emoji=Field<System.Windows.Controls.CheckBox>(applying,"colorEmoji");
            var panel=Field<System.Windows.Controls.CheckBox>(applying,"playbackPanelVisible");
            Check(panel.IsChecked==true,"playback panel defaults to enabled");
            panel.IsChecked=false;
            Check(emoji.IsChecked==true,"color emoji defaults to enabled");
            emoji.IsChecked=false;
            acceleration.IsChecked=false;
            fontSize.Value=125;autoStart.IsChecked=true;applying.Apply(false);
            Check(commits==1 && stored?.FontSize==60 && running?.FontSize==60 && !closed && toggles.Value!=null,"Apply saves and updates without closing");
            Check(stored?.AccelerateLongComments==false && running?.AccelerateLongComments==false,"Apply saves and applies disabled length acceleration");
            Check(stored?.ColorEmoji==false && running?.ColorEmoji==false,"Apply saves and applies disabled color emoji");
            Check(stored?.PlaybackPanelVisible==false && running?.PlaybackPanelVisible==false,"Apply saves and applies disabled playback panel");
            panel.IsChecked=true;
            emoji.IsChecked=true;
            acceleration.IsChecked=true;
            fontSize.Value=150;autoStart.IsChecked=false;applying.Apply(false);
            Check(commits==2 && stored?.FontSize==72 && running?.FontSize==72 && !closed && toggles.Value==null,"repeated Apply updates the startup baseline");
            Check(stored?.AccelerateLongComments==true && running?.AccelerateLongComments==true,"Apply saves and applies enabled length acceleration");
            Check(stored?.ColorEmoji==true && running?.ColorEmoji==true,"Apply saves and applies enabled color emoji");
            Check(stored?.PlaybackPanelVisible==true && running?.PlaybackPanelVisible==true,"Apply saves and applies enabled playback panel");
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
            var window=new SettingsWindow(first,_=>{},new MemoryStartup(),()=>new("録画サンプル.ts",5025,true,
                new[]{new NgLogRow(5022,"ここ好き","user_01"),new NgLogRow(5024,"いいシーンだ","user_02"),new NgLogRow(5025,"音楽もいい","user_03"),new NgLogRow(5027,"もう一度見たい","user_04")}),()=>"テスト用の読み込みエラー");
            var content=(FrameworkElement)window.Content;
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
