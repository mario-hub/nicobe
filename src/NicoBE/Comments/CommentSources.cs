using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NicoBE;

public static class CommentSources
{
    public static long Microseconds(DateTimeOffset value)=>(value.UtcTicks-DateTimeOffset.UnixEpoch.Ticks)/10;
    public static long Timestamp(SourceComment value,long zero)
    {
        if(long.TryParse(value.Attributes.GetValueOrDefault("date"),NumberStyles.Integer,CultureInfo.InvariantCulture,out long sec)
            && sec is >=0 and <253402300800)
        {
            long micro=0;
            if(value.Attributes.TryGetValue("date_usec",out string? text)
                && (!long.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out micro)||micro is <0 or >=1000000))
                throw new InvalidDataException("コメントのdate_usecが不正です。");
            return checked(sec*1000000+micro);
        }
        return checked(zero+(long)Math.Round(value.MediaSeconds*1000000));
    }
    public static string Identity(SourceComment value,long zero,bool fallback=false)
    {
        var a=value.Attributes;
        // Length-prefixed fields avoid ambiguous delimiters in arbitrary chat text.
        var fields=new[]{Timestamp(value,zero).ToString(CultureInfo.InvariantCulture),
            fallback?"":a.GetValueOrDefault("thread")??"",fallback?"":a.GetValueOrDefault("no")??"",
            a.GetValueOrDefault("user_id")??"",value.Text.Replace("\r\n","\n").Replace('\r','\n')};
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(fields.Select(f=>f.Length+":"+f)))));
    }
    public static List<SourceComment> Merge(IEnumerable<SourceComment> local,IEnumerable<SourceComment> remote,long zero,CancellationToken token=default)
    {
        var result=new List<SourceComment>();var seen=new HashSet<string>(StringComparer.Ordinal);
        var cross=new Dictionary<string,List<SourceComment>>(StringComparer.Ordinal);
        var localFallback=new Dictionary<string,Queue<SourceComment>>(StringComparer.Ordinal);
        foreach(var c in local)
        {
            token.ThrowIfCancellationRequested();
            if(!seen.Add(Identity(c,zero)))continue;
            result.Add(c);
            string crossKey=CrossKey(c);if(!cross.TryGetValue(crossKey,out var candidates))cross[crossKey]=candidates=new();candidates.Add(c);
            if(!string.IsNullOrEmpty(c.Attributes.GetValueOrDefault("user_id")))
            {
                string f=Identity(c,zero,true);
                if(!localFallback.TryGetValue(f,out var queue))localFallback[f]=queue=new();
                queue.Enqueue(c);
            }
        }
        foreach(var c in remote)
        {
            token.ThrowIfCancellationRequested();
            string crossKey=CrossKey(c);
            if(cross.TryGetValue(crossKey,out var candidates))
            {
                int match=candidates.FindIndex(x=>Equivalent(x,c,zero));
                if(match>=0)
                {
                    var matched=candidates[match];candidates.RemoveAt(match);
                    string fallback=Identity(matched,zero,true);
                    if(localFallback.TryGetValue(fallback,out var queue))localFallback[fallback]=new(queue.Where(x=>!ReferenceEquals(x,matched)));
                    continue;
                }
            }
            if(!seen.Add(Identity(c,zero)))continue;
            string f=Identity(c,zero,true);
            if(!string.IsNullOrEmpty(c.Attributes.GetValueOrDefault("user_id")) && localFallback.TryGetValue(f,out var matches))
            {
                var match=matches.FirstOrDefault(x=>MissingNumber(x)||MissingNumber(c));
                if(match!=null)
                {
                    localFallback[f]=new(matches.Where(x=>!ReferenceEquals(x,match)));
                    if(cross.TryGetValue(CrossKey(match),out var other))other.Remove(match);
                    continue;
                }
            }
            result.Add(c);
        }
        return result.OrderBy(c=>c.MediaSeconds).ToList();
    }
    public static string CrossKey(SourceComment c)
    {
        var fields=new[]{c.Attributes.GetValueOrDefault("no")??"",c.Attributes.GetValueOrDefault("user_id")??"",c.Text.Replace("\r\n","\n").Replace('\r','\n')};
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(fields.Select(f=>f.Length+":"+f)))));
    }
    public static bool Equivalent(SourceComment a,SourceComment b,long zero)
    {
        if(Identity(a,zero)==Identity(b,zero))return true;
        if(string.IsNullOrEmpty(a.Attributes.GetValueOrDefault("no"))||string.IsNullOrEmpty(a.Attributes.GetValueOrDefault("user_id"))||CrossKey(a)!=CrossKey(b))return false;
        string at=a.Attributes.GetValueOrDefault("thread")??"",bt=b.Attributes.GetValueOrDefault("thread")??"";
        bool Ch(string t)=>System.Text.RegularExpressions.Regex.IsMatch(t,@"^ch[0-9]+_[0-9]+$");
        bool Lv(string t)=>System.Text.RegularExpressions.Regex.IsMatch(t,@"^lv[0-9]+$");
        // Verified NicoJK vs API pair: channel_timestamp vs live ID, and <=1us conversion rounding.
        // Only these known thread forms, same no/user/text, one-to-one across sources qualify.
        return ((Ch(at)&&Lv(bt))||(Lv(at)&&Ch(bt))) && Math.Abs(Timestamp(a,zero)-Timestamp(b,zero))<=1;
    }
    static bool MissingNumber(SourceComment c)=>string.IsNullOrEmpty(c.Attributes.GetValueOrDefault("thread"))||string.IsNullOrEmpty(c.Attributes.GetValueOrDefault("no"));
}

internal sealed class CommentSourceSelection
{
    public bool Saved {get;private set;}
    public bool Embedded {get;private set;}
    public bool Past {get;private set;}
    public bool Ass {get;private set;}
    public bool Manual {get;private set;}
    public void Choose(string source,bool on)
    {
        Manual=true;
        if(source=="saved"){Saved=on;if(on)Ass=false;}
        if(source=="embedded"){Embedded=on;if(on)Ass=false;}
        if(source=="past"){Past=on;if(on)Ass=false;}
        if(source=="ass"){Ass=on;if(on)Saved=Embedded=Past=false;}
    }
    public void Automatic(bool saved,bool past,bool ass,bool complete,string preference,bool embedded=false)
    {
        if(Manual)return;
        Ass=ass && !(past&&complete) && (!(saved||embedded)||preference=="ASS");
        Saved=saved&&!Ass;Embedded=embedded&&!Ass;Past=past&&!Ass;
    }
}
