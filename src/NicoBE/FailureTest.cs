using System.IO;

namespace NicoBE;

internal static class FailureTest
{
    internal static int Run()
    {
        string folder=Path.Combine(Paths.Root,"temp","failure-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string blocked=Path.Combine(folder,"logs");File.WriteAllText(blocked,"blocked");
            bool displayed=false,ran=false;Exception? original=null;
            int code=SafeFileLog.Run(()=>
            {
                try{Directory.CreateDirectory(blocked);}catch(Exception ex){original=ex;throw;}
                ran=true;return 0;
            },ex=>SafeFileLog.Append(Path.Combine(blocked,"error.log"),ex.ToString()),ex=>displayed=ReferenceEquals(ex,original));
            if(code!=1 || !displayed || ran)throw new Exception("Startup failure was not reported correctly.");
            displayed=false;
            code=SafeFileLog.Run(()=>throw new IOException("original"),_=>throw new IOException("secondary"),ex=>displayed=ex.Message=="original");
            if(code!=1 || !displayed)throw new Exception("Secondary logging failure replaced the original failure.");
            Paths.WriteJson("failure-test.json",new{passed=true,blockedLog=true,originalErrorPreserved=true});return 0;
        }
        finally{Directory.Delete(folder,true);}
    }
}
