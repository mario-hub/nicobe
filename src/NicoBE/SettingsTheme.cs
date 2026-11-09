using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NicoBE;

public static class SettingsTheme
{
    public static readonly Brush Muted=new SolidColorBrush(Color.FromRgb(183,193,205));
    public static readonly Brush Accent=new SolidColorBrush(Color.FromRgb(39,210,223));
    public static readonly Brush Line=new SolidColorBrush(Color.FromRgb(65,76,89));
    public static void Apply(Window window)
    {
        string assembly=typeof(SettingsTheme).Assembly.GetName().Name!;
        window.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri($"/{assembly};component/SettingsTheme.xaml",UriKind.Relative)});
        window.SetResourceReference(Window.BackgroundProperty,"WindowBrush");window.SetResourceReference(Window.ForegroundProperty,"TextBrush");
        window.SourceInitialized+=(_,_)=>
        {
            int dark=1;DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,20,ref dark,sizeof(int));
        };
    }
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
}
