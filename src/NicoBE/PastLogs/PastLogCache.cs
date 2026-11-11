using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace NicoBE;

public sealed record PastLogRange(long Start,long End)
{
    public bool Valid=>Start>=0&&End>Start;
    public static PastLogRange For(RecordingInfo r)=>new(CommentSources.Microseconds(r.Start),CommentSources.Microseconds(r.End));
}
public sealed record PastLogCoverage(long Start,long End,DateTimeOffset FetchedUtc,bool Stable);
public sealed record PastLogSnapshot(List<SourceComment> Comments,List<PastLogCoverage> Coverage,PastLogRange Range)
{
    public bool Complete=>Missing(Range,false).Count==0;
    public bool Stable=>Missing(Range,true).Count==0;
    public List<PastLogRange> Missing(PastLogRange range,bool stable)
    {
        var result=new List<PastLogRange>();long cursor=range.Start;
        foreach(var c in Coverage.Where(c=>!stable||c.Stable).OrderBy(c=>c.Start))
        {if(c.End<=cursor)continue;if(c.Start>cursor)result.Add(new(cursor,Math.Min(c.Start,range.End)));cursor=Math.Max(cursor,c.End);if(cursor>=range.End)break;}
        if(cursor<range.End)result.Add(new(cursor,range.End));
        return result.Where(r=>r.Valid).ToList();
    }
    public long ContiguousEnd
    {
        get{long end=Range.Start;foreach(var c in Coverage.OrderBy(c=>c.Start)){if(c.Start>end)break;end=Math.Max(end,c.End);}return Math.Min(end,Range.End);}
    }
}
public sealed class PastLogCache
{
    public const long Minute=60_000_000,Block=30*Minute;
    public string Root {get;}
    public PastLogCache(string root){Root=Path.GetFullPath(root);}
    sealed record Manifest(int Version,string Channel,long BlockStart,string DataFile,string Sha256,List<PastLogCoverage> Coverage);
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static void CheckChannel(string channel)
    {if(!System.Text.RegularExpressions.Regex.IsMatch(channel,"^jk[0-9]{1,8}$"))throw new InvalidDataException("実況チャンネルが不正です。");}
    public string ChannelDirectory(string channel){CheckChannel(channel);return Path.Combine(Root,channel);}
    string ManifestPath(string channel,long block)
    {
        string day=DateTimeOffset.FromUnixTimeSeconds(block/1000000).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd");
        return Path.Combine(ChannelDirectory(channel),day,(block/1000000)+".json");
    }
    public PastLogSnapshot Read(string channel,PastLogRange range,long zero,CancellationToken token)
    {
        CheckChannel(channel);var comments=new List<SourceComment>();var coverage=new List<PastLogCoverage>();
        if(!range.Valid)return new(comments,coverage,range);
        for(long block=range.Start/Block*Block;block<range.End;block+=Block)
        {
            token.ThrowIfCancellationRequested();var entry=Load(channel,block,token);if(entry==null)continue;
            coverage.AddRange(entry.Value.Manifest.Coverage);
            foreach(var c in entry.Value.Comments){long t=CommentSources.Timestamp(c,0);if(t>=range.Start&&t<range.End)comments.Add(c with {MediaSeconds=(t-zero)/1000000d});}
        }
        return new(comments.OrderBy(c=>c.MediaSeconds).ToList(),coverage,range);
    }
    (Manifest Manifest,List<SourceComment> Comments)? Load(string channel,long block,CancellationToken token)
    {
        // A writer may retire the old data file after we read its manifest.
        return LoadOnce(channel,block,token)??LoadOnce(channel,block,token);
    }
    (Manifest Manifest,List<SourceComment> Comments)? LoadOnce(string channel,long block,CancellationToken token)
    {
        string path=ManifestPath(channel,block);
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>1024*1024)return null;
            var m=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
            if(m==null||m.Version!=1||m.Channel!=channel||m.BlockStart!=block||string.IsNullOrWhiteSpace(m.DataFile)||m.DataFile!=Path.GetFileName(m.DataFile)
                || !m.DataFile.StartsWith((block/1000000)+".",StringComparison.Ordinal)||!m.DataFile.EndsWith(".xml",StringComparison.Ordinal)
                ||m.Coverage==null||m.Coverage.Any(c=>c==null||c.Start<block||c.End>block+Block||c.End<=c.Start))return null;
            string data=Path.Combine(Path.GetDirectoryName(path)!,m.DataFile);
            if(!File.Exists(data)||new FileInfo(data).Length>PastLogClient.MaxBytes)return null;
            byte[] bytes=File.ReadAllBytes(data);token.ThrowIfCancellationRequested();
            if(Convert.ToHexString(SHA256.HashData(bytes))!=m.Sha256)return null;
            return(m,Parse(bytes,token));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or XmlException or InvalidDataException or ArgumentException){return null;}
    }
    public void Save(string channel,PastLogRange range,IReadOnlyList<SourceComment> comments,DateTimeOffset fetched,CancellationToken token)
    {
        // Caller holds the shared channel gate for the read/merge/commit transaction.
        long stableEnd=CommentSources.Microseconds(fetched.AddMinutes(-15));
        for(long block=range.Start/Block*Block;block<range.End;block+=Block)
        {
            token.ThrowIfCancellationRequested();long start=Math.Max(block,range.Start),end=Math.Min(block+Block,range.End);
            var old=Load(channel,block,token);var next=old?.Comments.Where(c=>{long t=CommentSources.Timestamp(c,0);return t<start||t>=end;}).ToList()??new();
            next.AddRange(comments.Where(c=>{long t=CommentSources.Timestamp(c,0);return t>=start&&t<end;}));
            next=CommentSources.Merge(Array.Empty<SourceComment>(),next,0);
            var spans=new List<PastLogCoverage>();
            foreach(var c in old?.Manifest.Coverage??new())
            {if(c.End<=start||c.Start>=end)spans.Add(c);else{if(c.Start<start)spans.Add(c with{End=start});if(c.End>end)spans.Add(c with{Start=end});}}
            if(start<Math.Min(end,stableEnd))spans.Add(new(start,Math.Min(end,stableEnd),fetched,true));
            if(end>Math.Max(start,stableEnd))spans.Add(new(Math.Max(start,stableEnd),end,fetched,false));
            string path=ManifestPath(channel,block),directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
            string name=(block/1000000)+"."+Guid.NewGuid().ToString("N")+".xml",data=Path.Combine(directory,name);
            byte[] bytes=Serialize(next);if(bytes.Length>PastLogClient.MaxBytes||next.Count>500000)throw new InvalidDataException("過去ログの保存上限を超えました。");
            string part=data+".part";
            try
            {
                File.WriteAllBytes(part,bytes);token.ThrowIfCancellationRequested();File.Move(part,data);
                var manifest=new Manifest(1,channel,block,name,Convert.ToHexString(SHA256.HashData(bytes)),spans.OrderBy(c=>c.Start).ToList());
                AtomicJson(path,manifest);
                // Readers holding the former generation may finish; failed cleanup is harmless.
                if(old.HasValue)try{File.Delete(Path.Combine(directory,old.Value.Manifest.DataFile));}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            finally{try{File.Delete(part);}catch(IOException){}}
        }
    }
    public static void AtomicJson<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string part=path+"."+Guid.NewGuid().ToString("N")+".part";
        try{File.WriteAllText(part,JsonSerializer.Serialize(value,Json),new UTF8Encoding(false));File.Move(part,path,true);}
        finally{try{File.Delete(part);}catch(IOException){}}
    }
    public static List<SourceComment> Parse(byte[] bytes,CancellationToken token)
    {
        using var input=new MemoryStream(bytes);using var xr=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=PastLogClient.MaxBytes});
        var doc=XDocument.Load(xr,LoadOptions.PreserveWhitespace);
        if(doc.Root?.Name!="packet")throw new InvalidDataException("過去ログAPIの応答が不正です。");
        var result=new List<SourceComment>();
        foreach(var x in doc.Root.Elements())
        {
            token.ThrowIfCancellationRequested();if(x.Name!="chat")throw new InvalidDataException("過去ログAPIの応答に不明な要素があります。");
            if(x.HasElements||x.Value.Length>1024*1024||result.Count>=500000)throw new InvalidDataException("過去ログAPIのコメントが不正または上限超過です。");
            var c=new SourceComment(0,x.Value,x.Attributes().ToDictionary(a=>a.Name.ToString(),a=>a.Value,StringComparer.Ordinal));
            if(!long.TryParse(c.Attributes.GetValueOrDefault("date"),out long date)||date is <0 or >=253402300800)throw new InvalidDataException("過去ログAPIの投稿時刻が不正です。");
            long time=CommentSources.Timestamp(c,0);result.Add(c with {MediaSeconds=time/1000000d});
        }
        return result;
    }
    static byte[] Serialize(IEnumerable<SourceComment> comments)
    {
        using var output=new MemoryStream();using(var writer=XmlWriter.Create(output,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true,NewLineHandling=NewLineHandling.Entitize}))
        {writer.WriteStartElement("packet");foreach(var c in comments){writer.WriteStartElement("chat");foreach(var a in c.Attributes)writer.WriteAttributeString(a.Key,a.Value);writer.WriteString(c.Text);writer.WriteEndElement();}writer.WriteEndElement();}
        return output.ToArray();
    }
}
