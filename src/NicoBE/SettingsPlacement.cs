using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

namespace NicoBE;

internal static class SettingsPlacement
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window,out Native.Rect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);

    internal static void BringToFront(Window window,nint player)
    {
        if(!window.IsVisible)return;
        if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;
        // An always-on-top player can cover even an active, ordinary window.
        // Match it only while settings are in use; don't keep settings pinned
        // over other applications after the user switches away.
        EventHandler? release=null;
        if(player!=0 && Native.IsWindow(player) && (Native.GetWindowLong(player,-20)&8)!=0 && !window.Topmost)
        {
            release=(_,_)=>{window.Topmost=false;window.Deactivated-=release;};
            window.Deactivated+=release;window.Topmost=true;
        }
        nint hwnd=new WindowInteropHelper(window).Handle;
        Native.SetWindowPos(hwnd,0,0,0,0,0,0x0013); // top of the current band; preserve position/size
        SetForegroundWindow(hwnd);
        window.Activate();window.Focus();
        if(Native.GetForegroundWindow()!=hwnd)
        {
            release?.Invoke(window,EventArgs.Empty);
            TraceLog.Write("settings-foreground: Windows did not grant foreground activation");
        }
    }

    // Inputs and output are physical pixels, including negative monitor coordinates.
    internal static Rect Bounds(Rect work,Point pointer,Size size,double gap)
    {
        double width=Math.Min(size.Width,work.Width),height=Math.Min(size.Height,work.Height);
        return new Rect(Math.Clamp(pointer.X-width-gap,work.Left,work.Right-width),
            Math.Clamp(pointer.Y-height,work.Top,work.Bottom-height),width,height);
    }
    internal static Rect CenteredBounds(Rect work,Rect player,Size size)
    {
        double width=Math.Min(size.Width,work.Width),height=Math.Min(size.Height,work.Height);
        return new Rect(Math.Clamp(player.Left+(player.Width-width)/2,work.Left,work.Right-width),
            Math.Clamp(player.Top+(player.Height-height)/2,work.Top,work.Bottom-height),width,height);
    }
    internal static void CenterOnPlayer(Window window,nint player)
    {
        if(!GetWindowRect(player,out _))return;
        window.WindowStartupLocation=WindowStartupLocation.Manual;
        void Place()
        {
            if(!GetWindowRect(player,out var target))return;
            nint hwnd=new WindowInteropHelper(window).Handle;
            var area=Forms.Screen.FromHandle(player).WorkingArea;
            Native.SetWindowPos(hwnd,0,target.Left+target.Width/2,target.Top+target.Height/2,0,0,0x0015);
            double dpi=Math.Max(96,GetDpiForWindow(hwnd))/96d;
            window.MinWidth=Math.Min(window.MinWidth,area.Width/dpi);window.MinHeight=Math.Min(window.MinHeight,area.Height/dpi);
            window.Width=Math.Min(window.Width,area.Width/dpi);window.Height=Math.Min(window.Height,area.Height/dpi);
            var bounds=CenteredBounds(new Rect(area.Left,area.Top,area.Width,area.Height),
                new Rect(target.Left,target.Top,target.Width,target.Height),new Size(window.Width*dpi,window.Height*dpi));
            Native.SetWindowPos(hwnd,0,(int)Math.Round(bounds.X),(int)Math.Round(bounds.Y),(int)Math.Round(bounds.Width),(int)Math.Round(bounds.Height),0x0014);
        }
        if(new WindowInteropHelper(window).Handle!=0)Place();
        else window.SourceInitialized+=(_,_)=>Place();
    }
    internal static void NearPointer(Window window)
    {
        var pointer=Forms.Cursor.Position;
        var area=Forms.Screen.FromPoint(pointer).WorkingArea;
        window.WindowStartupLocation=WindowStartupLocation.Manual;
        window.SourceInitialized+=(_,_)=>
        {
            nint hwnd=new WindowInteropHelper(window).Handle;
            // Enter the target monitor while hidden, allowing PerMonitorV2 to update DPI.
            Native.SetWindowPos(hwnd,0,area.Left,area.Top,0,0,0x0015); // no size/z-order/activation
            double dpi=Math.Max(96,GetDpiForWindow(hwnd))/96d;
            window.MinWidth=Math.Min(window.MinWidth,area.Width/dpi);
            window.MinHeight=Math.Min(window.MinHeight,area.Height/dpi);
            window.Width=Math.Min(window.Width,area.Width/dpi);
            window.Height=Math.Min(window.Height,area.Height/dpi);
            if(!GetWindowRect(hwnd,out var rect))return;
            var bounds=Bounds(new Rect(area.Left,area.Top,area.Width,area.Height),new Point(pointer.X,pointer.Y),
                new Size(Math.Min(rect.Right-rect.Left,area.Width),Math.Min(rect.Bottom-rect.Top,area.Height)),12*dpi);
            Native.SetWindowPos(hwnd,0,(int)bounds.X,(int)bounds.Y,(int)bounds.Width,(int)bounds.Height,0x0014);
        };
    }
}
