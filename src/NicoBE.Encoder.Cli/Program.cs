using System.Diagnostics;
using System.Reflection;
using System.Text;
using NicoBE.Encoder;

namespace NicoBE.Encoder.Cli;

internal static class Program
{
    internal const string Help="""
        NicoBE Encoder CLI
        使い方: NicoBE.Encoder.Cli.exe <入力TS> [出力フォルダまたは出力.mp4/.mkv] [--settings <設定JSON>]

        出力先省略: 設定のOutputFolder（初期値: データ保存先のoutput）
        設定省略: データ保存先のencoder-settings.json。なければ初期値。
        データ保存先: 通常は実行ファイルの隣。Program Files配下の場合だけ
          %LOCALAPPDATA%\NicoBE\encoder（GUIと共通）。FFmpegは同NicoBE\ffmpeg。
        ファイル名指定: 拡張子を保存形式として優先。既存動画・同名ASSは上書きしません。
        相対パス: 引数はカレントディレクトリ基準、JSON内はそのJSONのフォルダ基準。
        設定ファイルは変更しません。FFmpegやコメントログは事前に設定してください。
        --help / -h: ヘルプ    --version: バージョン    --: 以降をパスとして扱う
        Ctrl+C: 中止。進捗・警告・エラーは標準エラー、完了結果は標準出力。
        終了コード: 0=成功（警告を含む）、1=処理失敗、2=引数・設定不備、130=中止
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        if(args.Length==1 && args[0] is "--help" or "-h"){Console.WriteLine(Help);return 0;}
        if(args.Length==1 && args[0]=="--version")
        {
            Console.WriteLine("NicoBE Encoder CLI "+typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);return 0;
        }
        if(args.Length==1 && args[0]=="--self-test")return CliTest.Run();
        return Run(args).GetAwaiter().GetResult();
    }

    static async Task<int> Run(string[] args)
    {
        (string Input,EncoderSettings Settings,string? OutputFile) request;
        try{request=CliRequest.Parse(args).Prepare();}
        catch(Exception ex){Console.Error.WriteLine("入力・設定エラー: "+ex.Message);Console.Error.WriteLine("使い方は --help を参照してください。");return 2;}
        using var cancellation=new CancellationTokenSource();
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        Console.CancelKeyPress+=cancel;
        var progress=new ConsoleProgress();
        try
        {
            EncoderPaths.Initialize();
            Console.Error.WriteLine("入力: "+request.Input);
            Console.Error.WriteLine("処理を開始します。中止はCtrl+C。");
            progress.Start();
            var result=await EncoderJob.Run(request.Input,request.Settings,progress,cancellation.Token,outputFile:request.OutputFile);
            progress.Stop();
            Console.WriteLine("完了: "+result.Output);
            Console.WriteLine("経過時間: "+progress.Elapsed);
            Console.WriteLine("ログ: "+result.Log);
            if(result.Warnings.Count>0)
            {
                Console.Error.WriteLine($"警告（{result.Warnings.Count}件）:");
                foreach(string warning in result.Warnings)Console.Error.WriteLine("- "+warning);
            }
            return 0;
        }
        catch(OperationCanceledException ex){progress.Stop();Console.Error.WriteLine("中止: "+ex.Message);return 130;}
        catch(Exception ex){progress.Stop();Console.Error.WriteLine("失敗: "+ex.Message);return 1;}
        finally{progress.Stop();Console.CancelKeyPress-=cancel;}
    }

    sealed class ConsoleProgress : IProgress<EncodeProgress>
    {
        readonly Stopwatch elapsed=new();
        readonly object gate=new();
        EncodeProgress current=new("準備中",0,"");
        Timer? timer;bool stopped;
        public string Elapsed=>elapsed.Elapsed.ToString(@"hh\:mm\:ss");
        public void Start(){elapsed.Start();timer=new Timer(_=>Tick(),null,0,1000);}
        public void Report(EncodeProgress value){lock(gate){current=value;}}
        void Tick()
        {
            lock(gate)
            {
                if(stopped)return;
                try{Console.Error.WriteLine($"[{Elapsed}] {current.Stage} {current.Fraction:P0} {current.Detail}");}
                catch(System.IO.IOException){} // A closed output pipe must not crash the timer thread.
            }
        }
        public void Stop(){lock(gate){stopped=true;timer?.Dispose();elapsed.Stop();}}
    }
}
