using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NicoBE;

internal static class Native
{
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("psapi.dll",SetLastError=true)] public static extern bool EmptyWorkingSet(nint process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint length,uint flags);
    public static string CanonicalPath(string path)
    {
        using var file=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var text=new StringBuilder(32768);uint size=GetFinalPathNameByHandle(file,text,(uint)text.Capacity,0);
        if(size==0||size>=text.Capacity)return Path.GetFullPath(path);
        string full=text.ToString();if(full.StartsWith(@"\\?\UNC\"))return @"\\"+full[8..];
        return full.StartsWith(@"\\?\")?full[4..]:full;
    }
    public const int WM_COPYDATA = 0x4a;
    public delegate void WinEventProc(nint hook,uint eventType,nint hwnd,int objectId,int childId,uint threadId,uint time);
    public delegate bool EnumWindowsProc(nint hwnd,nint state);
    [DllImport("user32.dll",SetLastError=true)] public static extern nint SetWinEventHook(uint first,uint last,nint module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback,nint state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(nint hwnd,StringBuilder text,int capacity);
    [StructLayout(LayoutKind.Sequential)] public struct CopyData { public nuint Code; public int Bytes; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    [DllImport("user32.dll", SetLastError=true)] public static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, ref CopyData data, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetDlgItem(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern nint GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] public static extern nint SetWindowLong(nint hwnd, int index, nint value);
}
