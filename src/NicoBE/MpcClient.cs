using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NicoBE;

public sealed class MpcClient : IDisposable
{
    public const uint SubtitleTracks=0x50000004, GetSubtitleTracks=0xa0003000, SetSubtitleTrack=0xa0002005;
    public const uint Connect=0x50000000, State=0x50000001, PlayMode=0x50000002, NowPlaying=0x50000003,
        Playlist=0x50000006, Position=0x50000007, Seek=0x50000008, End=0x50000009, Disconnect=0x5000000b;
    public const uint OpenFile=0xa0000000, PlayPause=0xa0000003, Play=0xa0000004, Pause=0xa0000005,
        SetPosition=0xa0002000, GetNowPlaying=0xa0003002, GetPlaylist=0xa0003003, GetPosition=0xa0003004, GetVersion=0xa0003006, SetSpeed=0xa0004008, CloseApp=0xa0004006;
    private readonly HwndSource source;
    private readonly Dispatcher dispatcher;
    private Process? process;
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private double lastReply,lastAttach=double.NegativeInfinity,lastWatch=double.NegativeInfinity;
    bool disposed;
    public nint PlayerWindow { get; private set; }
    public bool Connected => PlayerWindow != 0 && Native.IsWindow(PlayerWindow);
    public int? PlayerProcessId => process is { HasExited:false } ? process.Id : null;
    public event Action<uint,string>? Message;
    public MpcClient(nint hwnd, Dispatcher dispatcher)
    {
        this.dispatcher=dispatcher; source=HwndSource.FromHwnd(hwnd)!; source.AddHook(Hook);
    }
    private nint Hook(nint hwnd,int msg,nint wParam,nint lParam,ref bool handled)
    {
        if(msg!=Native.WM_COPYDATA || lParam==0) return 0;
        var data=Marshal.PtrToStructure<Native.CopyData>(lParam);
        uint code=(uint)data.Code;
        if(code < Connect || code > Disconnect || data.Bytes is < 2 or > 65536 || (data.Bytes & 1)!=0 || data.Data==0) return 0;
        Native.GetWindowThreadProcessId(wParam,out uint pid);
        if(process==null || pid != process.Id) return 0;
        string value=(Marshal.PtrToStringUni(data.Data,data.Bytes/2)??"").TrimEnd('\0');
        lastReply=clock.Elapsed.TotalSeconds;
        if(code==Connect && long.TryParse(value,out long handle))
        {
            var candidate=(nint)unchecked((uint)handle);
            Native.GetWindowThreadProcessId(candidate,out uint candidatePid);
            if(candidatePid!=pid) return 0;
            PlayerWindow=candidate;
        }
        if(code==Disconnect) PlayerWindow=0;
        dispatcher.BeginInvoke(() => Message?.Invoke(code,value),DispatcherPriority.Normal);
        handled=true; return 1;
    }
    public bool Send(uint command,string text="")
    {
        if(!Connected) return false;
        nint ptr=Marshal.StringToHGlobalUni(text);
        try
        {
            var data=new Native.CopyData {Code=command,Bytes=(text.Length+1)*2,Data=ptr};
            return Native.SendMessageTimeout(PlayerWindow,Native.WM_COPYDATA,source.Handle,ref data,2,150,out _)!=0;
        }
        finally {Marshal.FreeHGlobal(ptr);}
    }
    public static byte[] CommandLinePacket(params string[] args)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.Unicode);
        writer.Write((uint)args.Length);
        foreach(string arg in args)writer.Write(Encoding.Unicode.GetBytes(arg+"\0"));
        return stream.ToArray();
    }
    public bool SendCommandLine(nint window,params string[] args)
    {
        byte[] bytes=CommandLinePacket(args);nint ptr=Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,ptr,bytes.Length);
            var data=new Native.CopyData {Code=0x6ABE51,Bytes=bytes.Length,Data=ptr};
            return Native.SendMessageTimeout(window,Native.WM_COPYDATA,source.Handle,ref data,2,150,out nint accepted)!=0 && accepted!=0;
        }
        finally{Marshal.FreeHGlobal(ptr);}
    }
    public bool Attach(Process target,nint knownWindow=0)
    {
        target.Refresh();nint window=knownWindow!=0?knownWindow:target.MainWindowHandle;
        if(window==0 || !Native.IsWindow(window))return false;
        Native.GetWindowThreadProcessId(window,out uint pid);if(pid!=target.Id)return false;
        if(process?.Id!=target.Id){process?.Dispose();process=target;PlayerWindow=0;}
        lastAttach=clock.Elapsed.TotalSeconds;
        bool sent=SendCommandLine(window,"/slave",unchecked((int)source.Handle.ToInt64()).ToString(CultureInfo.InvariantCulture));
        if(sent)TraceLog.Write($"attach-request pid={target.Id} hwnd={window}");
        return sent;
    }
    public void Watch()
    {
        double now=clock.Elapsed.TotalSeconds;if(now-lastWatch<1)return;lastWatch=now;
        if(process is {HasExited:false})
        {
            if(Connected)Send(GetVersion);
            if(now-lastReply>2 && now-lastAttach>3)Attach(process);
            return;
        }
        if(PlayerWindow!=0){PlayerWindow=0;Message?.Invoke(Disconnect,"");}
        process?.Dispose();process=null;
    }
    public void Detach()
    {
        if(Connected)SendCommandLine(PlayerWindow,"/slave","0");
        PlayerWindow=0;
    }
    public void Dispose() {if(disposed)return;disposed=true;Detach();source.RemoveHook(Hook);process?.Dispose();}

    public static string[] SplitFields(string value)
    {
        var parts=new List<string>(); var field=new StringBuilder();
        for(int i=0;i<value.Length;i++)
        {
            if(value[i]=='\\' && i+1<value.Length && value[i+1]=='|') {field.Append('|');i++;}
            else if(value[i]=='|') {parts.Add(field.ToString());field.Clear();}
            else field.Append(value[i]);
        }
        parts.Add(field.ToString()); return parts.ToArray();
    }

}
