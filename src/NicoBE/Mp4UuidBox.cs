using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace NicoBE;

public static class Mp4UuidBox
{
    static (long Offset,int Count)? Find(Stream file,byte[] wanted,int limit,CancellationToken token,bool appending)
    {
        file.Position=0; (long,int)? found=null;
        Span<byte> header=stackalloc byte[16],id=stackalloc byte[16];
        int boxes=0;
        bool hasFileType=false,hasMovie=false,hasMedia=false;
        while(file.Position<file.Length)
        {
            token.ThrowIfCancellationRequested();
            // Some files carry an ASCII numeric footer outside the MP4 boxes.
            // Accept only a bounded, all-digit tail after the normal top-level
            // structure. Appending remains strict and never writes past it.
            if(!appending && hasFileType && hasMovie && hasMedia && IsNumericFooter(file))break;
            if(++boxes>100000 || file.Length-file.Position<8)throw new InvalidDataException("MP4のボックス構造が不正または上限を超えています。");
            long start=file.Position;file.ReadExactly(header[..8]);
            long size=BinaryPrimitives.ReadUInt32BigEndian(header);int headerSize=8;
            if(size==1)
            {
                if(file.Length-file.Position<8)throw new InvalidDataException("MP4の拡張サイズが不正です。");
                file.ReadExactly(header[8..]);ulong value=BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if(value>long.MaxValue)throw new InvalidDataException("MP4のサイズが不正です。");
                size=(long)value;headerSize=16;
            }
            if(size==0)
            {
                if(appending)throw new InvalidDataException("末尾までを占有するMP4ボックスには追記できません。");
                size=file.Length-start;
            }
            if(size<headerSize || size>file.Length-start)throw new InvalidDataException("MP4のサイズが不正です。");
            hasFileType|=header.Slice(4,4).SequenceEqual("ftyp"u8) && size>=headerSize+8;
            hasMovie|=header.Slice(4,4).SequenceEqual("moov"u8) && size>headerSize;
            hasMedia|=header.Slice(4,4).SequenceEqual("mdat"u8);
            if(header.Slice(4,4).SequenceEqual("uuid"u8))
            {
                if(size<headerSize+16)throw new InvalidDataException("MP4のUUIDが不正です。");
                file.ReadExactly(id);
                if(id.SequenceEqual(wanted))
                {
                    if(found!=null)throw new InvalidDataException("同じ種類のNicoBEボックスが複数あります。");
                    long count=size-headerSize-16;
                    if(count<=0 || count>limit)throw new InvalidDataException("NicoBEボックスのサイズが不正または上限を超えています。");
                    found=(file.Position,(int)count);
                }
            }
            file.Position=start+size;
        }
        return found;
    }
    static bool IsNumericFooter(Stream file)
    {
        long start=file.Position,remaining=file.Length-start;
        if(remaining is <=0 or >1024)return false;
        Span<byte> bytes=stackalloc byte[(int)remaining];
        try
        {
            file.ReadExactly(bytes);
            foreach(byte value in bytes)if(value is <(byte)'0' or >(byte)'9')return false;
            return true;
        }
        finally{file.Position=start;}
    }
    public static byte[]? Read(string path,byte[] id,int limit,CancellationToken token)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var found=Find(file,id,limit,token,false);if(found==null)return null;
        file.Position=found.Value.Offset;byte[] data=new byte[found.Value.Count];file.ReadExactly(data);return data;
    }
    public static void Append(string path,byte[] id,byte[] payload,int limit,CancellationToken token)
    {
        if(payload.Length==0 || payload.Length>limit)throw new InvalidDataException("NicoBEボックスのサイズが上限を超えています。");
        using var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        if(Find(file,id,limit,token,true)!=null)throw new InvalidDataException("NicoBEボックスは既に存在します。追記での更新には対応しません。");
        token.ThrowIfCancellationRequested();long original=file.Length;file.Position=original;
        try
        {
            Span<byte> header=stackalloc byte[8];BinaryPrimitives.WriteUInt32BigEndian(header,(uint)(24+payload.Length));"uuid"u8.CopyTo(header[4..]);
            file.Write(header);file.Write(id);file.Write(payload);file.Flush(true);
        }
        catch {file.SetLength(original);file.Flush(true);throw;}
    }
    public static T Deserialize<T>(byte[] json,JsonSerializerOptions? options=null)
    {
        using var document=JsonDocument.Parse(json);
        CheckNames(document.RootElement);
        return document.RootElement.Deserialize<T>(options)??throw new InvalidDataException("JSONを読み込めません。");
    }
    static void CheckNames(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            {
                if(!names.Add(property.Name))throw new InvalidDataException("JSONに重複した項目名があります。");
                CheckNames(property.Value);
            }
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())CheckNames(item);
    }
}
