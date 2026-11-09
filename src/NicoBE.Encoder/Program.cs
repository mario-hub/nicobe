using System.IO;
using System.Text.Json;
using System.Windows;

namespace NicoBE.Encoder;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return SafeFileLog.Run(()=>Run(args),ex=>SafeFileLog.Append(Path.Combine(EncoderPaths.Logs,"encoder-error.log"),ex+Environment.NewLine),
            ex=>MessageBox.Show("NicoBE Encoderを実行できませんでした。\n"+ex.Message+"\n\n保存先: "+EncoderPaths.Root+"\n保存先のアクセス権と空き容量を確認してください。","NicoBE Encoder",MessageBoxButton.OK,MessageBoxImage.Error));
    }
    static int Run(string[] args)
    {
        if(args.Contains("--pipe-child"))return PipeFailureTest.Child();
        EncoderPaths.Initialize();
        if(args.Contains("--failure-test"))return FailureTest.Run();
        if(args.Contains("--pipe-test"))return PipeFailureTest.Run().GetAwaiter().GetResult();
        if(args.Contains("--storage-test"))return StorageLayoutTest.Run();
        if(args.Contains("--installer-test")){InstallerTest.Run().GetAwaiter().GetResult();return 0;}
        if(args.Contains("--comment-format-test"))return CommentFormatTest.Run();
        if(args.Contains("--no-comments-test"))return NoCommentsTest.Run(args.First(a=>!a.StartsWith("--"))).GetAwaiter().GetResult();
        if(args.Contains("--av1-test"))return Av1Test.Run(args.First(a=>!a.StartsWith("--"))).GetAwaiter().GetResult();
        if(args.Contains("--audio-test"))return AudioTest.Run().GetAwaiter().GetResult();
        if(args.Contains("--format-test"))return SmokeTest.Formats(args.First(a=>!a.StartsWith("--"))).GetAwaiter().GetResult();
        if(args.Contains("--sidecar-test"))return SmokeTest.Formats(args.First(a=>!a.StartsWith("--")),true).GetAwaiter().GetResult();
        if(args.Contains("--passes-test"))return SmokeTest.Run(args.First(a=>!a.StartsWith("--")),true).GetAwaiter().GetResult();
        if(args.Contains("--smoke-test"))return SmokeTest.Run(args.First(a=>!a.StartsWith("--"))).GetAwaiter().GetResult();
        if(args.Contains("--recover"))return SmokeTest.Recover(args.First(a=>!a.StartsWith("--"))).GetAwaiter().GetResult();
        if(args.Contains("--ui-test"))return EncoderPreview.Run();
        if(args.Contains("--ui-preview"))return EncoderPreview.Render();
        var app=new Application();return app.Run(new EncoderWindow(args));
    }
}
