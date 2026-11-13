using System.IO;
using System.Text.Json;

namespace NicoBE.Encoder;

internal sealed record EncoderQueueSnapshot(int Version,EncoderSettings Settings,string[] Files);

// A separate journal and exclusive lease per window prevents concurrent instances
// from restoring or overwriting each other's work. Only paths/settings are saved.
internal sealed class EncoderQueueRecovery : IDisposable
{
    internal static string Folder=>Path.Combine(EncoderPaths.Root,"queues");
    readonly FileStream lease;
    internal string FileName {get;}
    internal EncoderQueueSnapshot? Snapshot {get;private set;}
    EncoderQueueRecovery(string file)
    {
        FileName=file;
        lease=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
    }
    internal static EncoderQueueRecovery Create()
    {Directory.CreateDirectory(Folder);return new(Path.Combine(Folder,Guid.NewGuid().ToString("N")+".json"));}
    internal static EncoderQueueRecovery? RestoreFirst()
    {
        if(!Directory.Exists(Folder))return null;
        foreach(string file in Directory.EnumerateFiles(Folder,"*.json").OrderBy(p=>p,StringComparer.Ordinal))
        {
            EncoderQueueRecovery recovery;
            try{recovery=new(file);}catch(IOException){continue;}
            try
            {
                if(!File.Exists(file)){recovery.Dispose();continue;}
                if(new FileInfo(file).Length>8*1024*1024)throw new IOException("保存キューが大きすぎます: "+file);
                var state=JsonSerializer.Deserialize<EncoderQueueSnapshot>(File.ReadAllText(file),EncoderSettings.Json);
                if(state is not {Version:1} || state.Settings==null || state.Files==null || state.Files.Any(p=>string.IsNullOrWhiteSpace(p)||!Path.IsPathFullyQualified(p)))
                    throw new IOException("保存キューの形式が不正です: "+file);
                recovery.Snapshot=state;return recovery;
            }
            catch{recovery.Dispose();throw;}
        }
        return null;
    }
    internal void Save(IEnumerable<string> files,EncoderSettings settings)
    {
        var state=new EncoderQueueSnapshot(1,settings,files.ToArray());
        if(state.Files.Length==0){File.Delete(FileName);Snapshot=state;return;}
        string temporary=FileName+".tmp";
        try
        {
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(output,state,EncoderSettings.Json);output.Flush(true);}
            File.Move(temporary,FileName,true);Snapshot=state;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Dispose()=>lease.Dispose();
}
