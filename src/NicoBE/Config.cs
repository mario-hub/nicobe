using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NicoBE;

public sealed class Config
{
    public const double BaseFontSize = 38.4;
    public const double BaseScrollPixelsPerSecond = 288;
    public const double DefaultLineSpacingPercent = 60;
    public string NicoJkLogFolder { get; set; } = "";
    public string FontFamily { get; set; } = "Yu Gothic UI";
    public bool CommentsVisible { get; set; } = true;
    public string CommentPreference { get; set; } = "Original";
    public double FontSize { get; set; } = BaseFontSize;
    public double LineSpacingPercent { get; set; } = DefaultLineSpacingPercent;
    public double ScrollPixelsPerSecond { get; set; } = BaseScrollPixelsPerSecond;
    public double FixedCommentSeconds { get; set; } = 5;
    public int MaxVisibleComments { get; set; } = 70;
    public double Opacity { get; set; } = .95;
    public double CommentOffsetSeconds { get; set; }
    public int PollIntervalMs { get; set; } = 100;
    public int? ServiceId { get; set; }
    public Dictionary<string, string> ChannelOverrides { get; set; } = new();
    public bool SetupCompleted {get;set;}
    public List<string> NgIds {get;set;} = new();
    public List<string> NgWords {get;set;} = new();
    [JsonIgnore] public bool IsReady=>Directory.Exists(NicoJkLogFolder);
    [JsonIgnore] public double FontSizePercent {get=>FontSize/BaseFontSize*100;set=>FontSize=BaseFontSize*value/100;}
    [JsonIgnore] public double ScrollSpeedPercent {get=>ScrollPixelsPerSecond/BaseScrollPixelsPerSecond*100;set=>ScrollPixelsPerSecond=BaseScrollPixelsPerSecond*value/100;}
    [JsonIgnore] public double AdvanceSeconds {get=>-CommentOffsetSeconds;set=>CommentOffsetSeconds=-value;}

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static Config Load(bool requirePaths=true)
    {
        var c = File.Exists(Paths.Config)?JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.Config), Json) ?? throw new Exception("設定を読み込めません。"):new Config();
        c.Validate(requirePaths);return c;
    }
    public void Validate(bool requirePaths=true)
    {
        var c=this;
        if(CommentPreference is not ("Original" or "ASS"))throw new Exception("コメントの優先形式が不正です。");
        if(string.IsNullOrWhiteSpace(FontFamily))throw new Exception("コメントのフォントを指定してください。");
        NgIds=NgFilter.Normalize(NgIds,StringComparer.Ordinal);
        NgWords=NgFilter.Normalize(NgWords,StringComparer.OrdinalIgnoreCase);
        if (requirePaths && !Directory.Exists(c.NicoJkLogFolder)) throw new Exception("NicoJKのログフォルダが見つかりません。");
        if (!double.IsFinite(c.FontSize) || c.FontSize is < 16 or > 115.2
            || !double.IsFinite(c.LineSpacingPercent) || c.LineSpacingPercent is < 0 or > 200
            || c.ScrollPixelsPerSecond is < 30 or > 1000 || c.MaxVisibleComments is < 1 or > 300
            || c.PollIntervalMs is < 40 or > 500 || c.FixedCommentSeconds is < 1 or > 30 || c.Opacity is < .1 or > 1
            || !double.IsFinite(c.CommentOffsetSeconds)) throw new Exception("nicobe.json の描画設定が範囲外です。");
    }
    public Config Clone()=>JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this,Json),Json)!;
    public void CopyFrom(Config value)
    {
        foreach(var property in typeof(Config).GetProperties().Where(p=>p.CanWrite && !Attribute.IsDefined(p,typeof(JsonIgnoreAttribute))))property.SetValue(this,property.GetValue(value));
        ChannelOverrides=new(value.ChannelOverrides);
        NgIds=new(value.NgIds);NgWords=new(value.NgWords);
    }
    public void Save()
    {
        Validate(false);string temporary=Paths.Config+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(this,Json));File.Move(temporary,Paths.Config,true);
    }
}

public static class Paths
{
    public static string ExecutableDirectory => StorageLayout.Current.ExecutableDirectory;
#if NICOBE_ENCODER
    public static string Root => StorageLayout.Current.EncoderDirectory;
#else
    public static string Root => StorageLayout.Current.PlayerDirectory;
#endif
    // Encoder may read the player's log-folder setting as its initial value.
    public static string Config => Path.Combine(StorageLayout.Current.PlayerDirectory, "nicobe.json");
    public static string Runtime => Path.Combine(Root, "runtime");
    public static string Logs => Path.Combine(Root, "logs");
    public static void Initialize()
    {
        Directory.CreateDirectory(Logs);
        var temp = Path.Combine(Root, "temp"); Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp);
    }
    public static void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(Logs, name), JsonSerializer.Serialize(value, NicoBE.Config.Json));
}

public static class TraceLog
{
    private static readonly object Gate = new();
    public static void Write(string text)
    {
        lock (Gate) SafeFileLog.Append(Path.Combine(Paths.Logs, "nicobe.log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
    }
}
