using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NicoBE;

internal sealed record LiveCommentBatch(RecordingInfo Recording,string Channel,List<Comment> Added,int Files,int Malformed,bool More,string? Warning);

// Owned by one background worker. The UI owns the accumulated, time-ordered comment list.
internal sealed class LiveCommentSource
{
    readonly record struct Fingerprint(ulong A,ulong B,ulong C,ulong D);
    readonly HashSet<Fingerprint> seen=new();
    readonly Dictionary<string,Tail> tails=new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Path,long Stamp)> catalog=new();
    readonly byte[] bytes=new byte[65536];
    readonly char[] chars=new char[Encoding.UTF8.GetMaxCharCount(65536)];
    RecordingInfo recording;
    long tsLength;
    int nextFile,malformed;
    bool discovered;
    public string Channel {get;}
    public string Folder {get;}
    internal long BytesRead {get;private set;}
    internal int IdentityCount=>seen.Count;

    public LiveCommentSource(RecordingInfo info,Config config)
    {
        recording=info;Channel=LogReader.ResolveChannel(info,config);
        Folder=string.IsNullOrWhiteSpace(config.NicoJkLogFolder)?"":Path.GetFullPath(Path.Combine(config.NicoJkLogFolder,Channel));
    }
    public LiveCommentBatch Read(bool rescan,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long length=new FileInfo(recording.Path).Length;
        if(length<tsLength)throw new IOException("再生中のTSが短くなりました。ファイルを開き直してください。");
        if(length!=tsLength)
        {
            var end=TsReader.ReadGrowingEnd(recording,token);
            if(end>recording.End)recording=recording with {End=end};
            tsLength=length;
        }
        double start=recording.Start.ToUnixTimeMilliseconds()/1000d;
        double endTime=recording.End.ToUnixTimeMilliseconds()/1000d;
        if(!discovered || rescan)
        {
            catalog.Clear();nextFile=0;
            if(Directory.Exists(Folder))
            {
                catalog.AddRange(Directory.EnumerateFiles(Folder,"*.txt")
                    .Select(path=>(Path:path,Stamp:long.TryParse(Path.GetFileNameWithoutExtension(path),out long n)?n:-1)).Where(f=>f.Stamp>=0)
                    .OrderBy(f=>f.Stamp));
                int first=catalog.FindLastIndex(f=>f.Stamp<=start);nextFile=Math.Max(0,first);
            }
            discovered=true;
        }
        while(nextFile<catalog.Count && catalog[nextFile].Stamp<=endTime)
        {
            string path=catalog[nextFile++].Path;
            tails.TryAdd(path,new Tail());
        }
        var added=new List<Comment>();bool more=false;string? warning=null;long budget=8*1024*1024;
        foreach(var (path,tail) in tails)
        {
            token.ThrowIfCancellationRequested();
            tail.Release(endTime,added);
            // A chunk beyond the current TS end is kept, not rescanned on every notification.
            try
            {
                long read=tail.Read(path,bytes,chars,ref budget,token,line=>Parse(line,start,endTime,tail,added),()=>malformed++);
                BytesRead+=read;more|=tail.More && tail.Future.Count==0;
            }
            catch(FileNotFoundException){}
            catch(DirectoryNotFoundException){}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){warning=ex.Message;}
        }
        added.Sort((a,b)=>a.MediaSeconds.CompareTo(b.MediaSeconds));
        return new(recording,Channel,added,tails.Count,malformed,more,warning);
    }
    void Parse(string line,double start,double end,Tail tail,List<Comment> added)
    {
        try
        {
            using var reader=XmlReader.Create(new StringReader(line),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
            var x=XElement.Load(reader,LoadOptions.PreserveWhitespace);
            if(x.Name!="chat" || x.HasElements || !long.TryParse((string?)x.Attribute("date"),NumberStyles.Integer,CultureInfo.InvariantCulture,out long date))
            {malformed++;return;}
            long usec=0;
            if(x.Attribute("date_usec") is {} micro && (!long.TryParse(micro.Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out usec) || usec<0 || usec>999999))
            {malformed++;return;}
            double timestamp=date+usec/1000000d;if(timestamp<start)return;
            var original=new SourceComment(timestamp-start,x.Value,x.Attributes().ToDictionary(a=>a.Name.ToString(),a=>a.Value,StringComparer.Ordinal));
            var comment=LogReader.ForPlayback(original);if(comment==null)return;
            string identity=$"{x.Attribute("thread")?.Value}|{x.Attribute("no")?.Value}|{date}|{usec}|{comment.UserId}|{comment.Text}";
            // Keep fixed-size SHA-256 identities rather than another full copy of every text/ID.
            Span<byte> hash=stackalloc byte[32];SHA256.HashData(Encoding.UTF8.GetBytes(identity),hash);
            var key=new Fingerprint(BinaryPrimitives.ReadUInt64LittleEndian(hash),BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
            if(!seen.Add(key))return;
            if(timestamp>end)tail.Future.Add((timestamp,comment));else added.Add(comment);
        }
        catch(Exception ex) when(ex is XmlException or ArgumentException){malformed++;}
    }

    sealed class Tail
    {
        long offset;DateTime creation,lastWrite;
        byte[] anchor=Array.Empty<byte>();
        Decoder decoder=Encoding.UTF8.GetDecoder();
        readonly StringBuilder line=new();StringBuilder? record;
        public readonly List<(double Timestamp,Comment Comment)> Future=new();
        public bool More {get;private set;}
        public void Release(double end,List<Comment> added)
        {
            for(int i=0;i<Future.Count;)
                if(Future[i].Timestamp<=end){added.Add(Future[i].Comment);Future.RemoveAt(i);}else i++;
        }
        public long Read(string path,byte[] bytes,char[] chars,ref long budget,CancellationToken token,Action<string> consume,Action malformed)
        {
            var info=new FileInfo(path);if(!info.Exists)return 0;
            long length=info.Length;DateTime created=info.CreationTimeUtc;
            bool reset=length<offset || creation!=default && creation!=created
                || length==offset && lastWrite!=default && lastWrite!=info.LastWriteTimeUtc;
            if(length==offset && !reset){More=false;return 0;}
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            length=stream.Length;
            if(!reset && anchor.Length>0 && length>=offset)
            {
                stream.Position=offset-anchor.Length;Span<byte> actual=stackalloc byte[64];
                int got=stream.Read(actual[..anchor.Length]);reset=got!=anchor.Length || !actual[..got].SequenceEqual(anchor);
            }
            if(reset){offset=0;decoder=Encoding.UTF8.GetDecoder();line.Clear();record=null;anchor=Array.Empty<byte>();}
            if(Future.Count>0 && !reset){More=offset<length;return 0;}
            creation=created;lastWrite=info.LastWriteTimeUtc;stream.Position=offset;long total=0;
            while(offset<length && budget>0)
            {
                token.ThrowIfCancellationRequested();
                int count=stream.Read(bytes,0,(int)Math.Min(bytes.Length,Math.Min(length-offset,budget)));
                if(count==0)break;
                offset+=count;total+=count;budget-=count;
                int countChars=decoder.GetChars(bytes,0,count,chars,0,false);
                for(int i=0;i<countChars;i++)
                {
                    if(chars[i]=='\n'){ProcessLine(line.ToString().TrimEnd('\r'),consume,malformed);line.Clear();}
                    else line.Append(chars[i]);
                    if(line.Length>1024*1024){line.Clear();record=null;malformed();}
                }
                anchor=bytes.AsSpan(Math.Max(0,count-64),Math.Min(count,64)).ToArray();
                if(Future.Count>0)break;
            }
            // Accept a complete last chat even if the writer has not emitted its newline yet.
            string last=line.ToString();
            if(last.Contains("</chat>",StringComparison.Ordinal) || last.TrimEnd().EndsWith("/>",StringComparison.Ordinal))
            {ProcessLine(last.TrimEnd('\r'),consume,malformed);line.Clear();}
            More=offset<length;return total;
        }
        void ProcessLine(string value,Action<string> consume,Action malformed)
        {
            value=value.TrimStart('\uFEFF');
            bool starts=value.StartsWith("<chat ",StringComparison.Ordinal) || value.StartsWith("<chat>",StringComparison.Ordinal);
            if(starts){if(record!=null)malformed();record=new(value);}
            else if(record!=null)record.Append('\n').Append(value);
            if(record==null)return;
            if(record.Length>1024*1024){record=null;malformed();return;}
            if(value.Contains("</chat>",StringComparison.Ordinal) || value.TrimEnd().EndsWith("/>",StringComparison.Ordinal))
            {consume(record.ToString());record=null;}
        }
    }
}
