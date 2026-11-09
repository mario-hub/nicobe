using System.IO;
using System.Text.Json;
using NicoBE.Encoder;

namespace NicoBE.Encoder.Cli;

internal sealed record CliRequest(string Input,string? Output,string? Settings)
{
    public static CliRequest Parse(string[] args)
    {
        var positional=new List<string>();string? settings=null;bool literal=false;
        for(int i=0;i<args.Length;i++)
        {
            string arg=args[i];
            if(!literal && arg=="--"){literal=true;continue;}
            if(!literal && arg=="--settings")
            {
                if(settings!=null || ++i==args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                    throw new ArgumentException("--settings は設定ファイルのパスを1回だけ指定してください。");
                settings=args[i];continue;
            }
            if(!literal && arg.StartsWith('-'))throw new ArgumentException("不明なオプション: "+arg);
            if(string.IsNullOrWhiteSpace(arg))throw new ArgumentException("空のパスは指定できません。");
            positional.Add(arg);
        }
        if(positional.Count is <1 or >2)throw new ArgumentException("入力TSを1つ、出力先を必要に応じて1つ指定してください。");
        return new(positional[0],positional.Count==2?positional[1]:null,settings);
    }

    public (string Input,EncoderSettings Settings,string? OutputFile) Prepare()
    {
        string input=Path.GetFullPath(Input);
        if(!File.Exists(input) || !new[]{".ts",".m2ts",".mts"}.Contains(Path.GetExtension(input).ToLowerInvariant()))
            throw new ArgumentException("入力TSファイルが見つからないか、拡張子がTS／M2TS／MTSではありません: "+input);
        string path=Settings==null?EncoderPaths.Settings:Path.GetFullPath(Settings);
        if(Settings!=null && !File.Exists(path))throw new ArgumentException("設定ファイルが見つかりません: "+path);
        EncoderSettings settings;
        if(File.Exists(path))
        {
            // Load() intentionally permits a missing GUI configuration; explicit CLI profiles must be strict.
            using var json=JsonDocument.Parse(File.ReadAllText(path));
            if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("設定ファイルはJSONオブジェクトで指定してください。");
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in json.RootElement.EnumerateObject())
            {
                if(!names.Add(property.Name) || typeof(EncoderSettings).GetProperty(property.Name)==null)
                    throw new ArgumentException("設定ファイルの項目が重複しているか不明です: "+property.Name);
                if(property.Value.ValueKind==JsonValueKind.Null)throw new ArgumentException("設定値をnullにはできません: "+property.Name);
            }
            settings=EncoderSettings.Load(path);
        }
        else settings=new();
        string baseDirectory=Path.GetDirectoryName(path)!;
        string Absolute(string value)=>string.IsNullOrWhiteSpace(value)?value:Path.GetFullPath(value,baseDirectory);
        settings.OutputFolder=Absolute(settings.OutputFolder);
        settings.Ffmpeg=Absolute(settings.Ffmpeg);
        settings.NicoJkLogFolder=Absolute(settings.NicoJkLogFolder);
        string? outputFile=null;
        if(Output!=null)
        {
            string output=Path.GetFullPath(Output);
            string extension=Path.GetExtension(output).ToLowerInvariant();
            if(!Directory.Exists(output) && extension is ".mp4" or ".mkv")
            {
                settings.Container=extension==".mkv"?"MKV":"MP4";
                settings.OutputFolder=Path.GetDirectoryName(output)!;outputFile=output;
            }
            else settings.OutputFolder=output;
        }
        settings.Validate();
        // Reject conflicts before starting FFmpeg; Run() checks again before encoding/publishing.
        EncoderJob.ResolveOutputPath(input,settings,outputFile);
        string? ancestor=settings.OutputFolder;
        while(ancestor!=null && !Directory.Exists(ancestor))
        {
            if(File.Exists(ancestor))throw new ArgumentException("出力先フォルダの位置にファイルが存在します: "+ancestor);
            ancestor=Path.GetDirectoryName(ancestor);
        }
        return(input,settings,outputFile);
    }
}
