using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace NicoBE.Encoder;

internal static class EncoderWindowPlacement
{
    sealed record Placement(int X,int Y,int Width,int Height,uint Dpi,bool Maximized);
    [StructLayout(LayoutKind.Sequential)] struct Bounds {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo {public int Size;public Bounds Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd,out Bounds bounds);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint MonitorFromRect(ref Bounds bounds,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    static string FileName=>Path.Combine(EncoderPaths.Root,"encoder-window.json");
    static void Log(Exception ex)=>SafeFileLog.Append(Path.Combine(EncoderPaths.Logs,"encoder-error.log"),"window-placement "+ex+Environment.NewLine);

    internal static void Attach(Window window)
    {
        Placement? saved=null,last=null;bool maximized=false;
        try
        {
            if(File.Exists(FileName))saved=JsonSerializer.Deserialize<Placement>(File.ReadAllText(FileName));
            if(saved is not null && (saved.Width is <100 or >100000 || saved.Height is <100 or >100000
                || saved.Dpi is <48 or >960 || Math.Abs((long)saved.X)>1000000 || Math.Abs((long)saved.Y)>1000000))saved=null;
        }
        catch(Exception ex){Log(ex);}
        if(saved!=null)
        {
            window.WindowStartupLocation=WindowStartupLocation.Manual;
            window.SourceInitialized+=(_,_)=>
            {
                var rectangle=new Bounds{Left=saved.X,Top=saved.Y,Right=saved.X+saved.Width,Bottom=saved.Y+saved.Height};
                var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
                if(!GetMonitorInfo(MonitorFromRect(ref rectangle,2),ref info))return;
                nint hwnd=new WindowInteropHelper(window).Handle;
                // Enter the destination monitor while hidden so WPF receives its DPI.
                SetWindowPos(hwnd,0,info.Work.Left,info.Work.Top,0,0,0x0015);
                uint dpi=GetDpiForWindow(hwnd);if(dpi==0)dpi=96;
                double scale=dpi/(double)saved.Dpi;
                int width=Math.Min((int)Math.Round(saved.Width*scale),info.Work.Right-info.Work.Left);
                int height=Math.Min((int)Math.Round(saved.Height*scale),info.Work.Bottom-info.Work.Top);
                window.MinWidth=Math.Min(window.MinWidth,width*96d/dpi);
                window.MinHeight=Math.Min(window.MinHeight,height*96d/dpi);
                int x=Math.Clamp(saved.X,info.Work.Left,info.Work.Right-width),y=Math.Clamp(saved.Y,info.Work.Top,info.Work.Bottom-height);
                SetWindowPos(hwnd,0,x,y,width,height,0x0014);
                last=new(x,y,width,height,dpi,false);
                if(saved.Maximized)window.WindowState=WindowState.Maximized;
            };
        }
        void Capture()
        {
            if(!window.IsVisible)return;
            if(window.WindowState!=WindowState.Minimized)maximized=window.WindowState==WindowState.Maximized;
            if(window.WindowState!=WindowState.Normal)return;
            nint hwnd=new WindowInteropHelper(window).Handle;
            if(GetWindowRect(hwnd,out var r)&&r.Right>r.Left&&r.Bottom>r.Top)
                last=new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,Math.Max(96u,GetDpiForWindow(hwnd)),false);
        }
        window.ContentRendered+=(_,_)=>Capture();window.LocationChanged+=(_,_)=>Capture();
        window.SizeChanged+=(_,_)=>Capture();window.StateChanged+=(_,_)=>Capture();
        window.Closed+=(_,_)=>
        {
            if(last==null)return;
            string temporary=FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllText(temporary,JsonSerializer.Serialize(last with {Maximized=maximized}));
                File.Move(temporary,FileName,true);
            }
            catch(Exception ex){Log(ex);}
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception ex){Log(ex);}}
        };
    }
}
