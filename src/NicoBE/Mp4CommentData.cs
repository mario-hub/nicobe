using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NicoBE;

public sealed record SourceComment(
    [property:JsonRequired] double MediaSeconds,
    [property:JsonRequired] string Text,
    [property:JsonRequired] Dictionary<string,string> Attributes);

public sealed record Mp4CommentData(
    [property:JsonRequired] int Version,
    string? Channel,
    [property:JsonRequired] List<SourceComment> Comments)
{
    public const int CurrentVersion=1;
    // Literal network-order UUID bytes; do not use Guid.ToByteArray().
    static readonly byte[] Id=Convert.FromHexString("5077DB8EAAF4174FA59FF650F165A0C3");
    const int MaxRaw=128*1024*1024,MaxCompressed=48*1024*1024;
    static readonly JsonSerializerOptions Json=new() {Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public void Validate()
    {
        if(Version!=CurrentVersion)throw new InvalidDataException($"コメント形式 Version {Version} は未対応です。");
        if(Channel!=null && !Regex.IsMatch(Channel,@"\Ajk[0-9]+\z") || Comments==null || Comments.Count>500000)
            throw new InvalidDataException("格納コメントの形式が不正または件数上限を超えています。");
        double previous=double.NegativeInfinity;
        foreach(var c in Comments)
        {
            if(c==null || !double.IsFinite(c.MediaSeconds) || c.MediaSeconds<previous || c.MediaSeconds<0
                || c.Text==null || c.Attributes==null || c.Attributes.Any(a=>string.IsNullOrEmpty(a.Key) || a.Value==null))
                throw new InvalidDataException("格納コメントに不正な値があります。");
            previous=c.MediaSeconds;
        }
    }
    public List<Comment> ForPlayback()=>Comments.Select(LogReader.ForPlayback).OfType<Comment>().ToList();
    public void Append(string path,CancellationToken token)
    {
        Validate();token.ThrowIfCancellationRequested();byte[] json=JsonSerializer.SerializeToUtf8Bytes(this,Json);
        if(json.Length>MaxRaw)throw new InvalidDataException("格納コメントが上限（128 MiB）を超えました。");
        using var packed=new MemoryStream();using(var gzip=new GZipStream(packed,CompressionLevel.Optimal,true))gzip.Write(json);
        Mp4UuidBox.Append(path,Id,packed.ToArray(),MaxCompressed,token);
    }
    public static Mp4CommentData? TryRead(string path,CancellationToken token)
    {
        byte[]? data=Mp4UuidBox.Read(path,Id,MaxCompressed,token);if(data==null)return null;
        using var packed=new MemoryStream(data);using var gzip=new GZipStream(packed,CompressionMode.Decompress);using var raw=new MemoryStream();
        byte[] buffer=new byte[65536];int read;
        while((read=gzip.Read(buffer))>0)
        {
            token.ThrowIfCancellationRequested();if(raw.Length+read>MaxRaw)throw new InvalidDataException("コメント展開サイズが上限を超えました。");
            raw.Write(buffer,0,read);
        }
        var result=Mp4UuidBox.Deserialize<Mp4CommentData>(raw.ToArray(),Json);result.Validate();return result;
    }
    public bool ContentEquals(Mp4CommentData other)=>Version==other.Version && Channel==other.Channel
        && Comments.Count==other.Comments.Count && Comments.Zip(other.Comments).All(pair=>
            pair.First.MediaSeconds==pair.Second.MediaSeconds && pair.First.Text==pair.Second.Text
            && pair.First.Attributes.Count==pair.Second.Attributes.Count
            && pair.First.Attributes.All(a=>pair.Second.Attributes.TryGetValue(a.Key,out var value) && value==a.Value));
}
