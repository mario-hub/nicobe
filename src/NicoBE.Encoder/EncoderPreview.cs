using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NicoBE.Encoder;
public static class EncoderPreview
{
    public static int Run()
    {
        InstallerTest.Run().GetAwaiter().GetResult();
        string settingsFile=Path.Combine(EncoderPaths.Root,"temp", "test-results","encoder-settings-roundtrip.json");
        var saved=new EncoderSettings {Codec="H265",Backend="CPU",Width=1600,Height=900,RateControl="Bitrate",Quality=27,BitrateMbps=8.5,Speed="Quality",Deinterlace="Off",Audio="AAC128",FrameRate=24,EmbedComments=false,CommentSizePercent=135,OutputFolder=Path.Combine(EncoderPaths.Root,"temp", "test-results","encoder-custom-output")};
        saved.Save(settingsFile);var restored=EncoderSettings.Load(settingsFile);
        if(System.Text.Json.JsonSerializer.Serialize(saved)!=System.Text.Json.JsonSerializer.Serialize(restored))throw new Exception("エンコード設定の保存・復元検証に失敗しました。");
        var fallback=new EncoderSettings {Ffmpeg=Path.Combine(EncoderPaths.Root,"temp", "test-results","missing-ffmpeg.exe")};
        if(!EncoderCapabilities.Resolve(fallback).GetAwaiter().GetResult() || fallback.Backend!="CPU" || fallback.Passes!=2)throw new Exception("NVENC unavailable fallback failed");
        fallback.Backend="NVENC";fallback.RateControl="Quality";
        EncoderCapabilities.Resolve(fallback).GetAwaiter().GetResult();
        if(fallback.Passes!=1)throw new Exception("CPU quality must use one pass");
        var comments=new List<Comment> {new(0,"小文字50％","#FFFFFF",.4,CommentPosition.Scroll),new(1,"大文字200％","#FFFFFF",2.6,CommentPosition.Top)};
        string box=Path.Combine(EncoderPaths.Root,"temp", "test-results","size-comment-box.mp4"),ass=Path.ChangeExtension(box,"ass");
        var source=new Mp4CommentData(1,"jk1",new() {new(0,"改行\nテスト",new() {{"mail","small"}})});
        File.WriteAllBytes(box,Array.Empty<byte>());source.Append(box,CancellationToken.None);
        var embedded=Mp4CommentData.TryRead(box,CancellationToken.None)!;
        AssComments.Write(ass,comments,CancellationToken.None);var imported=AssComments.ReadFile(ass,CancellationToken.None);
        if(!source.ContentEquals(embedded) || imported.Count!=comments.Count || !comments.Zip(imported).All(pair=>Math.Abs(pair.First.SizeScale-pair.Second.SizeScale)<.001))throw new Exception("Comment size round trip failed");
        return Render();
    }
    public static int Render()
    {
        Directory.CreateDirectory(Path.Combine(EncoderPaths.Root,"temp", "test-results"));
        var window=new EncoderWindow(Array.Empty<string>());window.PreviewFailure();var content=(FrameworkElement)window.Content;
        double[]? headerLayout=null;
        for(int tab=0;tab<window.Tabs.Items.Count;tab++)
        {
        window.Tabs.SelectedIndex=tab;
        content.Measure(new Size(860,780));content.Arrange(new Rect(0,0,860,780));content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);content.UpdateLayout();
        var currentLayout=window.Tabs.Items.Cast<System.Windows.Controls.TabItem>()
            .SelectMany(t=>new[]{t.ActualWidth,t.TranslatePoint(new Point(),window.Tabs).X}).ToArray();
        if(headerLayout==null)headerLayout=currentLayout;
        else if(!headerLayout.Zip(currentLayout).All(p=>Math.Abs(p.First-p.Second)<.01))throw new Exception("Tab header widths or positions changed on selection");
        if(tab==0)
        {
            foreach(string name in new[]{"status","detail","errorText","warningText","elapsedText"})
            {
                var box=(System.Windows.Controls.TextBox)typeof(EncoderWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                box.SelectAll();
                if(!box.IsReadOnly || box.SelectedText.Length==0 || box.SelectedText!=box.Text || !System.Windows.Input.ApplicationCommands.Copy.CanExecute(null,box))
                    throw new Exception("Result text cannot be selected/copied: "+name);
                box.Select(0,0);
            }
        }
        var foreground=new RenderTargetBitmap(860,780,96,96,PixelFormats.Pbgra32);foreground.Render(content);
        var canvas=new DrawingVisual();using(var dc=canvas.RenderOpen()){dc.DrawRectangle(window.Background,null,new Rect(0,0,860,780));dc.DrawImage(foreground,new Rect(0,0,860,780));}
        var bitmap=new RenderTargetBitmap(860,780,96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=File.Create(Path.Combine(EncoderPaths.Root,"temp", "test-results",tab==0?"encoder-preview.png":$"encoder-preview-tab{tab+1}.png")))encoder.Save(output);
        }
        File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results","encoder-ui-checks.json"),System.Text.Json.JsonSerializer.Serialize(new{passed=true,stableTabWidthsAndPositions=true,resultTextSelectable=true,copyCommandEnabled=true,clipboardModified=false}));
        window.Close();return 0;
    }
}
