using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NicoBE;

public sealed class ResidentInstance : IDisposable
{
    readonly Mutex mutex;
    readonly string name;
    readonly CancellationTokenSource stop=new();
    public bool IsFirst {get;}
    public ResidentInstance()
    {
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Paths.Root.ToUpperInvariant())))[..16];
        name="NicoBE-"+hash;mutex=new Mutex(true,@"Local\"+name,out bool first);IsFirst=first;
    }
    public void Forward(string[] args)
    {
        using var pipe=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.CurrentUserOnly);
        pipe.Connect(4000);using var writer=new StreamWriter(pipe,new UTF8Encoding(false));writer.WriteLine(JsonSerializer.Serialize(args));
    }
    public async Task Listen(Action<string[]> receive)
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader=new StreamReader(pipe,Encoding.UTF8);
                string? text=await reader.ReadLineAsync(stop.Token);
                if(text!=null && text.Length<32768)receive(JsonSerializer.Deserialize<string[]>(text)??Array.Empty<string>());
            }
            catch(OperationCanceledException){break;}
            catch(Exception ex){TraceLog.Write("resident-pipe "+ex.Message);}
        }
    }
    public void Dispose(){stop.Cancel();if(IsFirst)mutex.ReleaseMutex();mutex.Dispose();stop.Dispose();}
}
