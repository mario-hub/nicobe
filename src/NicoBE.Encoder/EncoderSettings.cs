using System.IO;
using System.Text.Json;

namespace NicoBE.Encoder;

public static class EncoderPaths
{
    public static string Root=>Paths.Root;
    public static string Output=>Path.Combine(Root,"output");
    public static string Logs=>Path.Combine(Root,"logs");
    public static string Temp=>Path.Combine(Root,"temp");
    public static string Settings=>Path.Combine(Root,"encoder-settings.json");
    public static void Initialize()
    {
        foreach(string path in new[]{Output,Logs,Temp})
        {
            Directory.CreateDirectory(path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("出力先のリンクフォルダは使用できません: "+path);
        }
        Environment.SetEnvironmentVariable("TEMP",Temp);Environment.SetEnvironmentVariable("TMP",Temp);
    }
}
public sealed class EncoderSettings
{
    public string Ffmpeg {get;set;}=FindFfmpeg();
    public string Codec {get;set;}="H265";
    public string Container {get;set;}="MP4";
    public string Backend {get;set;}="NVENC";
    public int Width {get;set;}=1920;
    public int Height {get;set;}=1080;
    public string RateControl {get;set;}="Bitrate";
    public int Quality {get;set;}=23;
    public double BitrateMbps {get;set;}=1.5;
    public string Speed {get;set;}="Standard";
    public int Passes {get;set;}=2;
    public string Deinterlace {get;set;}="Auto";
    public string Audio {get;set;}="AAC128";
    public int FrameRate {get;set;}=60;
    public string OutputFolder {get;set;}=EncoderPaths.Output;
    public bool EmbedComments {get;set;}=true;
    public bool SaveExternalAss {get;set;}=false;
    public int CommentSizePercent {get;set;}=100;
    public string NicoJkLogFolder {get;set;}=DefaultLogFolder();
    static string DefaultLogFolder(){try{return Config.Load(false).NicoJkLogFolder;}catch{return "";}}
    public static readonly JsonSerializerOptions Json=new() {WriteIndented=true};
    public static int QualityMinimum(string codec,string backend)=>codec=="AV1" && backend=="QSV"?1:0;
    public static int QualityMaximum(string codec,string backend)=>codec!="AV1"?51:backend switch {"CPU" or "NVENC"=>63,"AMF"=>255,_=>51};
    public string Ffprobe=>Path.Combine(Path.GetDirectoryName(Ffmpeg)??"","ffprobe.exe");
    public static string FindFfmpeg()
    {
        var installed=Directory.Exists(FfmpegInstaller.InstallRoot)?Directory.EnumerateFiles(FfmpegInstaller.InstallRoot,"ffmpeg.exe",SearchOption.AllDirectories).Where(p=>!p.Contains(".incoming-",StringComparison.OrdinalIgnoreCase)).OrderDescending():Enumerable.Empty<string>();
        var paths=installed.Concat(new[]{Path.Combine(Paths.ExecutableDirectory,"ffmpeg.exe")})
            .Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Where(Directory.Exists).Select(p=>Path.Combine(p,"ffmpeg.exe")));
        return paths.FirstOrDefault(File.Exists)??"";
    }
    public static EncoderSettings Load(string? path=null)
    {
        var settings=File.Exists(path??EncoderPaths.Settings)?JsonSerializer.Deserialize<EncoderSettings>(File.ReadAllText(path??EncoderPaths.Settings),Json)??new():new();
        if(settings.Audio=="AAC")settings.Audio="AAC128";
        if(settings.Container=="MP4+ASS")
        {settings.Container="MP4";settings.SaveExternalAss=settings.EmbedComments;settings.EmbedComments=false;}
        return settings;
    }
    public void Validate(bool requireFiles=true)
    {
        if(requireFiles && (!File.Exists(Ffmpeg)||!File.Exists(Ffprobe)))throw new Exception("同じフォルダにffmpeg.exeとffprobe.exeがある場所を指定してください。");
        if(string.IsNullOrWhiteSpace(OutputFolder)||!Path.IsPathFullyQualified(OutputFolder))throw new Exception("出力先を絶対パスで指定してください。");
        if(requireFiles && (EmbedComments || SaveExternalAss) && !Directory.Exists(NicoJkLogFolder))throw new Exception("コメント保存用のNicoJKログフォルダを指定してください。");
        if(Passes is not (1 or 2))throw new Exception("パス数は1または2を指定してください。");
        if(Container is not ("MP4" or "MKV"))throw new Exception("保存形式はMP4またはMKVを選択してください。");
        if(Passes==2 && !(Backend=="NVENC" || Backend=="CPU" && RateControl=="Bitrate"))throw new Exception("2パスはNVENC、またはCPUのビットレート指定で利用できます。");
        if(Codec is not ("H264" or "H265" or "AV1") || Backend is not ("CPU" or "NVENC" or "QSV" or "AMF")
            || (Width,Height) is not ((1920,1080) or (1600,900) or (1280,720))
            || RateControl is not ("Quality" or "Bitrate") || Quality<QualityMinimum(Codec,Backend) || Quality>QualityMaximum(Codec,Backend)
            || !double.IsFinite(BitrateMbps) || BitrateMbps is <.5 or >100 || CommentSizePercent is <50 or >200
            || Speed is not ("Fast" or "Standard" or "Quality") || Deinterlace is not ("Auto" or "On" or "Off")
            || FrameRate is not (24 or 30 or 60) || Audio is not ("AAC128" or "AAC192" or "AAC256" or "Copy"))throw new Exception("エンコード設定が範囲外です。");
    }
    public void Save(string? path=null,bool requireFiles=true)
    {
        Validate(requireFiles);Write(path??EncoderPaths.Settings);
    }
    public static void SaveFfmpegPath(string executable)
    {
        if(!File.Exists(executable)||!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!,"ffprobe.exe")))throw new IOException("FFmpegの実行ファイルが見つかりません。");
        var settings=Load();settings.Ffmpeg=executable;settings.Write(EncoderPaths.Settings);
    }
    void Write(string path)
    {
        string temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(this,Json));File.Move(temp,path,true);
    }
}
