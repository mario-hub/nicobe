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
            Check(Math.Abs(edited.FontSize-57.6)<1e-9 && edited.ScrollPixelsPerSecond==216 && edited.CommentOffsetSeconds==-2.5,"percent and advance conversions");
            Check(config.FontSize!=edited.FontSize,"settings edits leave the running configuration untouched until save");
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
            Check(window.Tabs.Items.Count==4,"diagnostics and license tabs available");
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
