using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NicoBE;

internal static class MpcControlTransport
{
    internal const uint TimeoutMs=2000;
    internal static bool Send(nint target,nint source,uint expectedProcess,uint command,string text)
    {
        Native.GetWindowThreadProcessId(target,out uint actualProcess);
        if(actualProcess==0 || actualProcess!=expectedProcess || !Native.IsWindow(target))return false;
        nint ptr=Marshal.StringToHGlobalUni(text);
        long started=Stopwatch.GetTimestamp();
        try
        {
            var data=new Native.CopyData {Code=command,Bytes=(text.Length+1)*2,Data=ptr};
            bool completed=Native.SendMessageTimeout(target,Native.WM_COPYDATA,source,ref data,0x22,TimeoutMs,out _)!=0;
            int error=completed?0:Marshal.GetLastPInvokeError();
            TraceLog.Write($"control-send pid={expectedProcess} command=0x{command:X8} value={text} completed={completed} win32={error} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            // Timeout does not prove that the command was never processed. Never
            // retry automatically: relative seeks must not be applied twice.
            return completed;
        }
        finally{Marshal.FreeHGlobal(ptr);}
    }
}
