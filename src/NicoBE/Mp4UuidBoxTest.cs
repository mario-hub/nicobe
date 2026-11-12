using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NicoBE;

internal static class Mp4UuidBoxTest
{
    internal static void Verify(Action<bool,string> check)
    {
        string root=Path.Combine(Paths.Root,"temp","test-results","mp4-footer");Directory.CreateDirectory(root);
        byte[] id=Enumerable.Range(0,16).Select(x=>(byte)x).ToArray(),other=new byte[16],payload={1,2,3};
        byte[] Box(string type,byte[] content,bool extended=false)
        {
            int header=extended?16:8;var data=new byte[header+content.Length];
            BinaryPrimitives.WriteUInt32BigEndian(data,extended?1u:(uint)data.Length);Encoding.ASCII.GetBytes(type).CopyTo(data,4);
            if(extended)BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8),(ulong)data.Length);
            content.CopyTo(data,header);return data;
        }
        byte[] Join(params byte[][] arrays)=>arrays.SelectMany(a=>a).ToArray();
        byte[] Digits(int count)=>Encoding.ASCII.GetBytes(new string('7',count));
        var ftyp=Box("ftyp",Encoding.ASCII.GetBytes("isom\0\0\0\0"));
        var moov=Box("moov",Box("free",Array.Empty<byte>()));
        var mdat=Box("mdat",new byte[16],true);
        var prefix=Join(ftyp,moov,mdat);var uuid=Box("uuid",Join(id,payload));
        string Save(string name,byte[] data){string path=Path.Combine(root,name+".mp4");File.WriteAllBytes(path,data);return path;}
        void Reject(string name,byte[] data)
        {
            try{Mp4UuidBox.Read(Save(name,data),id,64,CancellationToken.None);}
            catch(InvalidDataException){check(true,name+" rejected");return;}
            throw new Exception(name+" unexpectedly accepted");
        }
        foreach(int count in new[]{1,7,105,1024})
        {
            string plain=Save("plain-"+count,Join(prefix,Digits(count)));
            check(Mp4UuidBox.Read(plain,id,64,CancellationToken.None)==null,"Numeric footer without NicoBE metadata: "+count);
            string tagged=Save("tagged-"+count,Join(prefix,uuid,Digits(count)));
            check(Mp4UuidBox.Read(tagged,id,64,CancellationToken.None)!.SequenceEqual(payload),"Metadata survives numeric footer: "+count);
            foreach(string path in new[]{plain,tagged})
            {
                var before=File.ReadAllBytes(path);bool rejected=false;
                try{Mp4UuidBox.Append(path,other,payload,64,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                check(rejected&&File.ReadAllBytes(path).SequenceEqual(before),"Append refuses numeric footer without modifying bytes");
            }
        }
        Reject("oversized-footer",Join(prefix,Digits(1025)));
        Reject("binary-footer",Join(prefix,new byte[]{0}));
        Reject("text-footer",Join(prefix,Encoding.ASCII.GetBytes("123x45678")));
        Reject("digits-before-next-box",Join(prefix,Digits(105),uuid));
        Reject("digits-without-movie",Join(ftyp,mdat,Digits(105)));
        Reject("digits-without-media",Join(ftyp,moov,Digits(105)));
        Reject("digits-without-file-type",Join(moov,mdat,Digits(105)));
        Reject("duplicate-uuid",Join(prefix,uuid,uuid,Digits(105)));
        var broken=Box("free",new byte[8]);BinaryPrimitives.WriteUInt32BigEndian(broken,10000);
        Reject("invalid-box-before-footer",Join(prefix,broken,Digits(105)));
        Reject("truncated-media",Join(ftyp,moov,mdat[..^1]));
        string valid=Save("valid",prefix);Mp4UuidBox.Append(valid,id,payload,64,CancellationToken.None);
        check(Mp4UuidBox.Read(valid,id,64,CancellationToken.None)!.SequenceEqual(payload),"Normal strict append and read still work");
    }
}
