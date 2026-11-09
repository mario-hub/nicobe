using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace NicoBE;

public enum CommentPosition { Scroll, Top, Bottom }
public sealed class CommentLogUnavailableException(string message) : Exception(message);
public sealed record Comment(double MediaSeconds, string Text, string Color, double SizeScale, CommentPosition Position, string UserId="");
public sealed record PreparedComments(RecordingInfo Recording, string Channel, List<Comment> Comments, int Files, int Malformed)
{
    public AssSubtitle? Subtitle { get; init; }
    public List<SourceComment>? SourceComments { get; init; }
}

public static class LogReader
{
    public static PreparedComments Prepare(string path, Config config, CancellationToken token)
    {
        RecordingInfo info;
        if(Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase))
        {
            if(config.CommentPreference=="ASS" && ContainerComments.External(path,token) is {} preferred)return preferred;
            var embedded=Mp4CommentData.TryRead(path,token);
            if(embedded==null && ContainerComments.External(path,token) is {} external)return external;
            if(embedded!=null)
            {
                AssSubtitle? subtitle=null;
                try{subtitle=ContainerComments.External(path,token)?.Subtitle;}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
                var rendered=embedded.ForPlayback();
                double duration=rendered.Count==0?1:rendered[^1].MediaSeconds+30;
                info=new(path,0,0,0,DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch.AddSeconds(Math.Min(duration,315537897599d-62135596800d)),0);
                return new(info,embedded.Channel??"",rendered,0,0) {Subtitle=subtitle};
            }
            info=Mp4RecordingMetadata.Read(path,token).Recording(path);
        }
        else if(Path.GetExtension(path).Equals(".mkv",StringComparison.OrdinalIgnoreCase))return ContainerComments.ReadMkv(path,config,token);
        else info=TsReader.Read(path, config.ServiceId, token);
        return Prepare(info,config,token);
    }
    public static PreparedComments Prepare(RecordingInfo info,Config config,CancellationToken token,bool preserveSource=false)
    {
        string channel=ResolveChannel(info,config);
        var folder = System.IO.Path.Combine(config.NicoJkLogFolder, channel);
        try
        {
            if((File.GetAttributes(folder)&FileAttributes.Directory)==0)throw new IOException($"実況ログのパスがフォルダーではありません: {folder}");
        }
        catch(Exception ex) when(ex is DirectoryNotFoundException or FileNotFoundException)
        {throw new CommentLogUnavailableException($"実況ログフォルダーがありません: {folder}");}
        double start = info.Start.ToUnixTimeMilliseconds() / 1000d, end = info.End.ToUnixTimeMilliseconds() / 1000d;
        var files = Directory.EnumerateFiles(folder, "*.txt").Select(p => (Path:p, Stamp:long.TryParse(System.IO.Path.GetFileNameWithoutExtension(p), out long n) ? n : -1))
            .Where(f => f.Stamp >= 0 && f.Stamp <= end).OrderBy(f => f.Stamp).ToList();
        int first = files.FindLastIndex(f => f.Stamp <= start);
        if (first < 0) first = 0;
        // Logs are chronological segments. Include the segment spanning recording start and every later segment.
        var selected = files.Skip(first).ToArray();
        var source=preserveSource?new List<SourceComment>():null;
        var result = new List<Comment>(); var seen = new HashSet<string>(); int malformed = 0;
        foreach (var file in selected)
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            int lines = 0;
            foreach (var line in ChatRecords(reader,token))
            {
                if ((lines++ & 511) == 0) token.ThrowIfCancellationRequested();
                try
                {
                    using var xr = XmlReader.Create(new StringReader(line), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024*1024 });
                    var x = XElement.Load(xr,LoadOptions.PreserveWhitespace);
                    if(x.Name!="chat" || x.HasElements){malformed++;continue;}
                    if (!long.TryParse((string?)x.Attribute("date"), NumberStyles.Integer,CultureInfo.InvariantCulture,out long date)) { malformed++; continue; }
                    long usec=0;
                    if(x.Attribute("date_usec") is {} micro && (!long.TryParse(micro.Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out usec) || usec<0 || usec>999999)) {malformed++;continue;}
                    double timestamp = date + usec / 1000000d;
                    if (timestamp < start || timestamp > end) continue;
                    var original=new SourceComment(timestamp-start,x.Value,x.Attributes().ToDictionary(a=>a.Name.ToString(),a=>a.Value,StringComparer.Ordinal));
                    if(source?.Count>=500000)throw new InvalidDataException("コメント件数が上限（50万件）を超えました。");
                    source?.Add(original);
                    var rendered=ForPlayback(original);
                    if(rendered==null)continue;
                    string key=$"{x.Attribute("thread")?.Value}|{x.Attribute("no")?.Value}|{date}|{usec}|{rendered.UserId}|{rendered.Text}";
                    if(seen.Add(key))result.Add(rendered);
                }
                catch (Exception e) when (e is XmlException or ArgumentException) { malformed++; }
            }
        }
        result.Sort((a,b) => a.MediaSeconds.CompareTo(b.MediaSeconds));
        return new(info, channel, result, selected.Length, malformed) {SourceComments=source?.OrderBy(c=>c.MediaSeconds).ToList()};
    }
    internal static string ResolveChannel(RecordingInfo info,Config config)
    {
        var channelFile = System.IO.Path.Combine(Paths.Root, "channels.json");
        if (!File.Exists(channelFile)) channelFile = System.IO.Path.Combine(Paths.ExecutableDirectory, "channels.json");
        var map = File.Exists(channelFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(channelFile))!
            : ChannelMap.Default;
        var keys = new[] { $"{info.NetworkId}:{info.ServiceId}", $"{(info.NetworkId is 4 or 6 ? info.NetworkId : 15)}:{info.ServiceId}", info.ServiceId.ToString() };
        string? channel = keys.Select(k => config.ChannelOverrides.GetValueOrDefault(k)).FirstOrDefault(x => x != null)
            ?? keys.Select(k => map.GetValueOrDefault(k)).FirstOrDefault(x => x != null);
        if (channel == null || !System.Text.RegularExpressions.Regex.IsMatch(channel, "^jk[0-9]+$"))
            throw new CommentLogUnavailableException($"実況局が不明です。channelOverrides に \"{info.NetworkId}:{info.ServiceId}\": \"jk番号\" を指定してください。");
        return channel;
    }
    static IEnumerable<string> ChatRecords(TextReader reader,CancellationToken token)
    {
        StringBuilder? pending=null;
        while(reader.ReadLine() is {} line)
        {
            token.ThrowIfCancellationRequested();
            if(pending==null)
            {
                if(!line.StartsWith("<chat ",StringComparison.Ordinal) && !line.StartsWith("<chat>",StringComparison.Ordinal))continue;
                pending=new StringBuilder(line);
            }
            else if(line.StartsWith("<chat ",StringComparison.Ordinal))
            {yield return pending.ToString();pending=new StringBuilder(line);}
            else pending.Append('\n').Append(line);
            if(pending.Length>1024*1024)throw new InvalidDataException("1件のコメントが読み込み上限（1 Mi文字）を超えています。");
            if(line.Contains("</chat>",StringComparison.Ordinal) || line.TrimEnd().EndsWith("/>",StringComparison.Ordinal))
            {yield return pending.ToString();pending=null;}
        }
        if(pending!=null)yield return pending.ToString();
    }
    public static Comment? ForPlayback(SourceComment original)
    {
        var attributes=original.Attributes;
        string text=original.Text.Replace("\r\n","\n").Replace('\r','\n');
        if(attributes.GetValueOrDefault("deleted")=="1" || string.IsNullOrWhiteSpace(text)
            || text.TrimStart().StartsWith('/') || text.Length>1000)return null;
        var mail=(attributes.GetValueOrDefault("mail")??"").Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var position=mail.Contains("ue")?CommentPosition.Top:mail.Contains("shita")?CommentPosition.Bottom:CommentPosition.Scroll;
        double size=mail.Contains("big")?1.3:mail.Contains("small")?.8:1;
        string color=mail.FirstOrDefault(t=>Colors.ContainsKey(t) || System.Text.RegularExpressions.Regex.IsMatch(t,@"\A#[0-9a-fA-F]{6}\z"))??"white";
        return new(original.MediaSeconds,text,Colors.GetValueOrDefault(color,color),size,position,attributes.GetValueOrDefault("user_id")??"");
    }
    private static readonly Dictionary<string,string> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"]="#FFFFFF", ["red"]="#FF0000", ["pink"]="#FF8080", ["orange"]="#FFC000", ["yellow"]="#FFFF00",
        ["green"]="#00FF00", ["cyan"]="#00FFFF", ["blue"]="#0000FF", ["purple"]="#C000FF", ["black"]="#000000",
        ["white2"]="#CCCC99", ["niconicowhite"]="#CCCC99", ["red2"]="#CC0033", ["truered"]="#CC0033",
        ["pink2"]="#FF33CC", ["orange2"]="#FF6600", ["passionorange"]="#FF6600", ["yellow2"]="#999900",
        ["madyellow"]="#999900", ["green2"]="#00CC66", ["elementalgreen"]="#00CC66", ["cyan2"]="#00CCCC",
        ["blue2"]="#3399FF", ["marineblue"]="#3399FF", ["purple2"]="#6633CC", ["nobleviolet"]="#6633CC", ["black2"]="#666666"
    };
}
