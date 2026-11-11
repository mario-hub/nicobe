using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NicoBE.Encoder;

internal static class EncoderGuideTest
{
    internal static int Run()
    {
        string folder=Path.Combine(EncoderPaths.Temp,"test-results");Directory.CreateDirectory(folder);
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);}
        T Field<T>(EncoderWindow window,string name)=>(T)typeof(EncoderWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            string path=Path.Combine(folder,"guide-settings.json");
            var settings=new EncoderSettings {Ffmpeg="",Codec="H264"};settings.Save(path,requireFiles:false);
            Check(!EncoderSettings.Load(path).FfmpegGuideAcknowledged,"new settings require the guide");
            bool fail=true;
            var window=new EncoderWindow(Array.Empty<string>(),settings,()=>{if(fail)throw new IOException("保存失敗のテスト");EncoderSettings.AcknowledgeFfmpegGuide(path);});
            var root=(FrameworkElement)window.Content;
            root.Measure(new Size(800,640));root.Arrange(new Rect(0,0,800,640));root.UpdateLayout();
            window.ShowFirstRunGuide();root.UpdateLayout();
            Check(window.Guide!=null&&window.Tabs.SelectedIndex==3,"first launch opens basic settings and the spotlight");
            var target=Field<FrameworkElement>(window,"ffmpegSection");var point=target.TranslatePoint(new Point(),root);
            Check(point.Y>=0&&point.Y+target.ActualHeight<=640,"FFmpeg controls are visible at minimum window size");
            var button=window.Guide!.Accept;var buttonPoint=button.TranslatePoint(new Point(),root);
            Check(buttonPoint.Y>=0&&buttonPoint.Y+button.ActualHeight<=640,"guide acknowledgement is visible at minimum window size");
            var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(window.Background,null,new Rect(0,0,800,640));}
            var layer=new RenderTargetBitmap(800,640,96,96,PixelFormats.Pbgra32);layer.Render(root);
            using(var dc=drawing.RenderOpen()){dc.DrawRectangle(window.Background,null,new Rect(0,0,800,640));dc.DrawImage(layer,new Rect(0,0,800,640));}
            var image=new RenderTargetBitmap(800,640,96,96,PixelFormats.Pbgra32);image.Render(drawing);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(Path.Combine(folder,"encoder-guide-preview.png")))png.Save(output);
            Field<TextBox>(window,"ffmpeg").Text="unsaved-ffmpeg.exe";
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.Guide!=null&&!EncoderSettings.Load(path).FfmpegGuideAcknowledged,"save failure keeps the guide open for retry");
            fail=false;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var stored=EncoderSettings.Load(path);
            Check(window.Guide==null&&stored.FfmpegGuideAcknowledged,"acknowledgement persists immediately");
            Check(stored.Ffmpeg==""&&stored.Codec=="H264","acknowledgement preserves saved settings and ignores unsaved form values");
            var form=(EncoderSettings)typeof(EncoderWindow).GetMethod("ReadSettings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null)!;
            Check(form.FfmpegGuideAcknowledged,"subsequent settings saves retain acknowledgement");window.Close();
            var reopened=new EncoderWindow(Array.Empty<string>(),stored,()=>throw new Exception("unexpected save"));reopened.ShowFirstRunGuide();
            Check(reopened.Guide==null,"acknowledged guide stays hidden after reopening");reopened.Close();
            string legacy=Path.Combine(folder,"legacy-guide-settings.json");File.WriteAllText(legacy,"{}");
            Check(EncoderSettings.Load(legacy).FfmpegGuideAcknowledged,"existing settings without the flag do not show onboarding");
            File.WriteAllText(Path.Combine(folder,"encoder-guide-test.json"),JsonSerializer.Serialize(new{passed=true,checks}));return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(folder,"encoder-guide-test.json"),JsonSerializer.Serialize(new{passed=false,checks,error=ex.ToString()}));return 1;}
    }
}
