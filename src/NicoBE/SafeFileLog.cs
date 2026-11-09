using System.IO;

namespace NicoBE;

// Diagnostics must never replace the original failure or stop playback.
internal static class SafeFileLog
{
    internal static void Append(string path,string text)
    {
        try
        {
            if(File.Exists(path) && new FileInfo(path).Length>5*1024*1024)File.Move(path,path+".1",true);
            File.AppendAllText(path,text);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException){}
    }
    internal static int Run(Func<int> action,Action<Exception> log,Action<Exception> display)
    {
        try{return action();}
        catch(Exception ex)
        {
            try{log(ex);}catch(Exception){} // Logging is best effort, even during initialization.
            display(ex);return 1;
        }
    }
}
