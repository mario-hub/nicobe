using System.Diagnostics;
using System.IO;
using System.Text;

namespace NicoBE.Encoder;

internal static class PipeFailureTest
{
    sealed class BrokenLog : StringWriter
    {
        public override Task WriteLineAsync(string? value)=>Task.FromException(new IOException("Simulated disk full"));
    }
    sealed class IgnoreProgress : IProgress<EncodeProgress> {public void Report(EncodeProgress progress){}}
    internal static int Child()
    {
        for(int i=0;i<30000;i++){Console.Error.WriteLine(new string('x',1024));Thread.Sleep(1);}
        return 0;
    }
    internal static async Task<int> Run()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string exe=Environment.ProcessPath!;
        var args=new List<string>();
        if(Path.GetFileNameWithoutExtension(exe).Equals("dotnet",StringComparison.OrdinalIgnoreCase))args.Add(typeof(Program).Assembly.Location);
        args.Add("--pipe-child");
        string log=Path.Combine(EncoderPaths.Temp,"pipe-failure-test.log");var elapsed=Stopwatch.StartNew();
        bool reported=false;
        try
        {
            await EncoderJob.RunPass(new EncoderSettings {Ffmpeg=exe},args,log,1,new IgnoreProgress(),timeout.Token,1,1,()=>new BrokenLog());
        }
        catch(IOException ex){reported=ex.InnerException?.Message=="Simulated disk full";}
        finally{File.Delete(log);}
        if(!reported || timeout.IsCancellationRequested)throw new Exception("Log failure did not terminate/report the child process promptly.");
        Paths.WriteJson("pipe-failure-test.json",new{passed=true,elapsedMilliseconds=elapsed.ElapsedMilliseconds});return 0;
    }
}
